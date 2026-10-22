using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamUpload(string FileName, string MediaType, byte[] Content);

/// <summary>IT Assets data and rules. Every public method checks the person's roles on the server.</summary>
public sealed partial class ItamStore(Func<DbConnection> connections, IItamSecrets secrets, TimeProvider? clock = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal DateTime UtcNow => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    // SQL helpers --------------------------------------------------------------------------------------------------------
    internal async Task<DbConnection> Open(CancellationToken token) { var c = connections(); await c.OpenAsync(token); return c; }
    static DbCommand Q(DbConnection c, DbTransaction? tx, string sql, (string, object?)[] values)
    {
        var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql; q.CommandTimeout = 60;
        foreach (var (n, v) in values) { var p = q.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value; q.Parameters.Add(p); }
        return q;
    }
    internal static async Task<List<T>> Rows<T>(DbConnection c, DbTransaction? tx, string sql, Func<DbDataReader, T> map, CancellationToken token, params (string, object?)[] values)
    {
        await using var q = Q(c, tx, sql, values); await using var r = await q.ExecuteReaderAsync(token); var list = new List<T>();
        while (await r.ReadAsync(token)) list.Add(map(r)); return list;
    }
    internal static async Task<object?> Scalar(DbConnection c, DbTransaction? tx, string sql, CancellationToken token, params (string, object?)[] values)
    { await using var q = Q(c, tx, sql, values); var v = await q.ExecuteScalarAsync(token); return v is DBNull ? null : v; }
    internal static async Task<int> Exec(DbConnection c, DbTransaction? tx, string sql, CancellationToken token, params (string, object?)[] values)
    { await using var q = Q(c, tx, sql, values); return await q.ExecuteNonQueryAsync(token); }
    internal static string? S(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    internal static int? I(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    internal static decimal? D(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    internal static DateOnly? Day(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
    internal static DateTime Utc(DbDataReader r, int i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
    internal static object? DateParam(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    internal static async Task Audit(DbConnection c, DbTransaction tx, Guid actor, string entity, string key, string action, object? before, object? after, string? reason, CancellationToken token)
        => await Exec(c, tx, "INSERT itam_access.audit(actor_id,entity,entity_key,action,before_json,after_json,reason) VALUES(@a,@e,@k,@x,@b,@f,@r)", token,
            ("@a", actor), ("@e", entity), ("@k", key), ("@x", action), ("@b", before is null ? null : JsonSerializer.Serialize(before, Json)),
            ("@f", after is null ? null : JsonSerializer.Serialize(after, Json)), ("@r", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 1000)]));

    internal static async Task Event(DbConnection c, DbTransaction tx, int asset, Guid actor, string kind, string? detail, CancellationToken token)
        => await Exec(c, tx, "INSERT itam_data.asset_event(asset_id,actor_id,kind,detail) VALUES(@a,@b,@k,@d)", token,
            ("@a", asset), ("@b", actor), ("@k", kind), ("@d", detail is null ? null : detail[..Math.Min(detail.Length, 2000)]));

    /// <summary>Next number of a sequence, taken inside the caller's transaction (one asset sequence for all types).</summary>
    internal static async Task<int> Next(DbConnection c, DbTransaction tx, string counter, CancellationToken token)
        => Convert.ToInt32(await Scalar(c, tx, "UPDATE itam_data.counter WITH (UPDLOCK) SET next_value=next_value+1 OUTPUT deleted.next_value WHERE counter_code=@c", token, ("@c", counter))
            ?? throw new InvalidOperationException("Missing counter " + counter));

    // Who is signed in ---------------------------------------------------------------------------------------------------------
    public async Task<ItamScope> Scope(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        return await Scope(c, null, user, token);
    }
    internal static async Task<ItamScope> Scope(DbConnection c, DbTransaction? tx, ItamUser user, CancellationToken token)
    {
        var who = (await Rows(c, tx, "SELECT display_name,enabled FROM itam_access.employee WHERE employee_id=@id", r => (Name: r.GetString(0), Enabled: r.GetBoolean(1)), token, ("@id", user.Id))).SingleOrDefault();
        if (who.Name is null || !who.Enabled) throw new ItamAccessDenied("Your account is not an active employee in IT Assets.");
        var roles = await Rows(c, tx, "SELECT role_code,region_code FROM itam_access.role_assignment WHERE employee_id=@id", r => (Role: r.GetString(0), Region: S(r, 1)), token, ("@id", user.Id));
        return new(user.Id, who.Name, [.. roles.Select(r => r.Role).Distinct()], [.. roles.Where(r => r.Role == ItamRoles.SupportSpecialist && r.Region is not null).Select(r => r.Region!).Distinct()],
            [.. roles.Where(r => r.Role == ItamRoles.InkOfficer && r.Region is not null).Select(r => r.Region!).Distinct()]);
    }
    internal static void Require(bool allowed, string message) { if (!allowed) throw new ItamAccessDenied(message); }

    // Text checks ------------------------------------------------------------------------------------------------------------------
    internal static string Text(string? value, string label, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new ArgumentException($"{label} is required.");
        if (v.Length > max) throw new ArgumentException($"{label}: up to {max} characters.");
        return v;
    }
    internal static string? Optional(string? value, string label, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length > max) throw new ArgumentException($"{label}: up to {max} characters.");
        return v.Length == 0 ? null : v;
    }

    // Files --------------------------------------------------------------------------------------------------------------------------
    public const int MaxFileBytes = 10 * 1024 * 1024;
    /// <summary>PDF, JPEG or PNG up to 10 MB, checked by content; returns the media type.</summary>
    public static string CheckFile(ItamUpload f, bool pdfOnly)
    {
        if (f.Content.Length is 0 or > MaxFileBytes) throw new ArgumentException($"{f.FileName}: files must be up to 10 MB.");
        var type = f.Content is [0x25, 0x50, 0x44, 0x46, ..] ? "application/pdf" : f.Content is [0xFF, 0xD8, 0xFF, ..] ? "image/jpeg" : f.Content is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : null;
        if (type is null || (pdfOnly && type != "application/pdf")) throw new ArgumentException(pdfOnly ? $"{f.FileName}: upload the PDF document." : $"{f.FileName}: upload a PDF, JPEG or PNG file.");
        return type;
    }
    internal static async Task<long> SaveFile(DbConnection c, DbTransaction tx, ItamUpload f, string type, Guid actor, CancellationToken token)
    {
        var name = Path.GetFileName(f.FileName).Trim(); if (name.Length == 0) name = "document"; if (name.Length > 200) name = name[^200..];
        return Convert.ToInt64(await Scalar(c, tx, "INSERT itam_data.stored_file(file_name,media_type,byte_length,sha256,content,uploaded_by) OUTPUT inserted.file_id VALUES(@n,@t,@l,@h,@c,@b)", token,
            ("@n", name), ("@t", type), ("@l", f.Content.Length), ("@h", SHA256.HashData(f.Content)), ("@c", f.Content), ("@b", actor)));
    }
    internal static async Task<ItamUpload?> LoadFile(DbConnection c, DbTransaction? tx, long id, CancellationToken token)
        => (await Rows(c, tx, "SELECT file_name,media_type,content FROM itam_data.stored_file WHERE file_id=@f", r => new ItamUpload(r.GetString(0), r.GetString(1), (byte[])r.GetValue(2)), token, ("@f", id))).SingleOrDefault();

    // Employees (read-only copy of Active Directory) ------------------------------------------------------------------------------------
    /// <summary>Updates the employee copy from a full read of the users OU: new and changed accounts are written; accounts no
    /// longer found are kept and marked disabled (history and custody stay linked). Returns (added, changed, disabled).</summary>
    public async Task<(int Added, int Changed, int Disabled)> SyncEmployees(IReadOnlyCollection<ItamEmployee> directory, CancellationToken token)
    {
        if (directory.Count == 0) throw new InvalidOperationException("The directory read returned no accounts; nothing changed.");
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var have = (await Rows(c, tx, "SELECT employee_id,account,display_name,email,job_title,department,manager_id,enabled,mobile,branch,company,hr_code FROM itam_access.employee",
            r => new ItamEmployee(r.GetGuid(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4), S(r, 5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetBoolean(7), S(r, 8), S(r, 9), S(r, 10), S(r, 11)), token)).ToDictionary(e => e.Id);
        int added = 0, changed = 0, disabled = 0;
        foreach (var e in directory)
        {
            (string, object?)[] v = [("@i", e.Id), ("@a", e.Account), ("@n", e.Name), ("@m", e.Email), ("@j", e.JobTitle), ("@d", e.Department), ("@g", e.ManagerId), ("@e", e.Enabled),
                ("@mo", e.Mobile), ("@b", e.Branch), ("@co", e.Company), ("@h", e.HrCode)];
            if (!have.TryGetValue(e.Id, out var old)) { await Exec(c, tx, "INSERT itam_access.employee(employee_id,account,display_name,email,job_title,department,manager_id,enabled,mobile,branch,company,hr_code) VALUES(@i,@a,@n,@m,@j,@d,@g,@e,@mo,@b,@co,@h)", token, v); added++; }
            else if (old != e) { await Exec(c, tx, "UPDATE itam_access.employee SET account=@a,display_name=@n,email=@m,job_title=@j,department=@d,manager_id=@g,enabled=@e,mobile=@mo,branch=@b,company=@co,hr_code=@h,synced_at_utc=SYSUTCDATETIME() WHERE employee_id=@i", token, v); changed++; }
        }
        var seen = directory.Select(e => e.Id).ToHashSet();
        foreach (var gone in have.Values.Where(e => e.Enabled && !seen.Contains(e.Id)))
        { await Exec(c, tx, "UPDATE itam_access.employee SET enabled=0,synced_at_utc=SYSUTCDATETIME() WHERE employee_id=@i", token, ("@i", gone.Id)); disabled++; }
        await tx.CommitAsync(token);
        return (added, changed, disabled);
    }

    internal static async Task<Dictionary<Guid, string>> Names(DbConnection c, DbTransaction? tx, CancellationToken token)
        => (await Rows(c, tx, "SELECT employee_id,display_name FROM itam_access.employee", r => (r.GetGuid(0), r.GetString(1)), token)).ToDictionary(x => x.Item1, x => x.Item2);
}

/// <summary>Applies the IT Assets migrations in order with a ledger (name + SHA-256), only to ITAM_Production,
/// ITAM_Test or an ITAM_Validation_ database.</summary>
public static class ItamSchemaInstaller
{
    static readonly string[] Names = ["001_itam_foundation.sql", "002_itam_handover.sql", "003_itam_ink_invoices.sql"];
    public static int Count => Names.Length;
    public static void RequireTarget(string database)
    {
        if (database is not ("ITAM_Production" or "ITAM_Test") && !System.Text.RegularExpressions.Regex.IsMatch(database, @"^ITAM_Validation_[0-9]{17}_[a-f0-9]{8}$"))
            throw new InvalidOperationException("The IT Assets installer requires its own database.");
    }
    static string Read(string name)
    {
        var resource = typeof(ItamSchemaInstaller).Assembly.GetManifestResourceNames().SingleOrDefault(r => r.EndsWith(".Migrations." + name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Missing migration " + name);
        using var reader = new StreamReader(typeof(ItamSchemaInstaller).Assembly.GetManifestResourceStream(resource)!, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    public static async Task<int> Apply(DbConnection connection, string database, CancellationToken token = default)
    {
        RequireTarget(database);
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        if (connection.Database != database) throw new InvalidOperationException("IT Assets database target mismatch.");
        await using var tx = await connection.BeginTransactionAsync(token);
        async Task<object?> Run(string sql, params (string, object)[] p)
        {
            await using var q = connection.CreateCommand(); q.Transaction = tx; q.CommandText = sql; q.CommandTimeout = 120;
            foreach (var (n, v) in p) { var x = q.CreateParameter(); x.ParameterName = n; x.Value = v; q.Parameters.Add(x); }
            return await q.ExecuteScalarAsync(token);
        }
        await Run("SET XACT_ABORT ON; DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'ITAM schema installation',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000; IF @r<0 THROW 51000,'Schema installation is busy.',1;");
        await Run("""
            IF OBJECT_ID(N'dbo.itam_schema_migration',N'U') IS NULL
            BEGIN
              IF SCHEMA_ID(N'itam_data') IS NOT NULL THROW 51000,'Untracked IT Assets schema requires review.',1;
              CREATE TABLE dbo.itam_schema_migration(name nvarchar(200) NOT NULL PRIMARY KEY,sha256 char(64) NOT NULL,applied_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            END;
            """);
        var applied = 0;
        foreach (var name in Names)
        {
            var sql = Read(name); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            if (await Run("SELECT sha256 FROM dbo.itam_schema_migration WHERE name=@n", ("@n", name)) is string existing)
            {
                if (existing != hash) throw new InvalidOperationException("An applied IT Assets migration differs from this package.");
                continue;
            }
            await Run(sql);
            await Run("INSERT dbo.itam_schema_migration(name,sha256) VALUES(@n,@h)", ("@n", name), ("@h", hash));
            applied++;
        }
        await tx.CommitAsync(token);
        return applied;
    }
}
