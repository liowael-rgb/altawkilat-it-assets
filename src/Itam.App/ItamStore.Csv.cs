using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamCsvImport(bool CheckOnly, string? Reason);
public sealed record ItamCsvRowError(int Row, string Error);
public sealed record ItamCsvResult(string List, int Rows, int Added, int Changed, int Unchanged, IReadOnlyList<ItamCsvRowError> Errors, bool Saved);

/// <summary>CSV files of the Administration Center (owner, 22 October 2026): export every list, and import master data and roles.
/// Columns are matched by their header, so a file exported here, edited in Excel and imported back works. An import is
/// all-or-nothing: each row goes through the same checks as the forms and is kept in the audit log; one wrong row and nothing is
/// saved (the rows with errors are listed). "Check only" runs the same checks and saves nothing. Import adds and updates rows;
/// it never deletes (set Active to No instead). Roles are only added by import; remove a role on the Roles page.</summary>
public sealed partial class ItamStore
{
    public const int MaxCsvRows = 5000;

    /// <summary>The CSV lists: key, title, the columns, importable.</summary>
    public static readonly (string Key, string Title, string[] Columns, bool Import)[] CsvLists =
    [
        ("types", "Asset types", ["Code", "Name", "Order", "Active"], true),
        ("regions", "Regions", ["Code", "Name", "Order", "Active"], true),
        ("locations", "IT warehouses and branches", ["Name", "Kind", "Region", "City", "Active"], true),
        ("suppliers", "Suppliers", ["Name", "Arabic name", "VAT number", "Active"], true),
        ("manufacturers", "Manufacturers", ["Name", "Active"], true),
        ("models", "Models", ["Manufacturer", "Type", "Model", "Active"], true),
        ("values", "Value lists", ["List", "Code", "Name", "Order", "Active"], true),
        ("roles", "Roles", ["Username", "Name", "Role", "Region"], true),
        ("employees", "Employees (from AD)", ["Username", "Name", "Email", "Job description", "Department", "Office", "Region", "SAP ID", "Enabled"], false),
        ("audit", "Audit log", ["When (UTC)", "Who", "What", "Key", "Action", "Details", "Reason"], false),
    ];

    static readonly (string Code, string Label)[] ValueLists = [("ComputerType", "Computer type"), ("OperatingSystem", "Operating system"), ("PrinterType", "Printer type"), ("TonerType", "Toner type")];

    static (string Key, string Title, string[] Columns, bool Import) CsvList(string key)
        => CsvLists.FirstOrDefault(l => l.Key == key) is { Key: not null } l ? l : throw new ArgumentException("Unknown list.");

    static void CsvAccess(ItamScope s, string key, bool import)
    {
        if (key == "roles") Require(import ? s.RolesManage : s.MasterData || s.RolesManage || s.AuditView, "Only the IT Manager gives roles.");
        else if (key == "audit") Require(s.AuditView, "The audit log is for the IT Manager.");
        else if (key == "employees") Require(s.MasterData || s.RolesManage || s.AuditView, "The Administration Center is for the IT Manager.");
        else Require(import ? s.MasterData : s.MasterData || s.RolesManage || s.AuditView, "Only the IT Manager changes master data.");
    }

    // ---- Export ------------------------------------------------------------------------------------------------------------------
    public async Task<ItamUpload> ExportCsv(ItamUser user, string key, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var list = CsvList(key);
        CsvAccess(s, key, import: false);
        static string Yes(bool b) => b ? "Yes" : "No";
        List<string?[]> rows = key switch
        {
            "types" => await Rows(c, null, "SELECT type_code,name_en,sort_order,active FROM itam_ref.asset_type ORDER BY sort_order,type_code", r => new string?[] { r.GetString(0), r.GetString(1), r.GetInt32(2).ToString(), Yes(r.GetBoolean(3)) }, token),
            "regions" => await Rows(c, null, "SELECT region_code,name_en,sort_order,active FROM itam_ref.region ORDER BY sort_order,region_code", r => new string?[] { r.GetString(0), r.GetString(1), r.GetInt32(2).ToString(), Yes(r.GetBoolean(3)) }, token),
            "locations" => await Rows(c, null, "SELECT name_en,kind,region_code,city,active FROM itam_ref.location ORDER BY region_code,name_en", r => new string?[] { r.GetString(0), r.GetString(1) == "ITWarehouse" ? "IT warehouse" : "Branch", r.GetString(2), S(r, 3), Yes(r.GetBoolean(4)) }, token),
            "suppliers" => await Rows(c, null, "SELECT name_en,name_ar,vat_no,active FROM itam_ref.supplier ORDER BY name_en", r => new string?[] { r.GetString(0), S(r, 1), S(r, 2), Yes(r.GetBoolean(3)) }, token),
            "manufacturers" => await Rows(c, null, "SELECT name_en,active FROM itam_ref.manufacturer ORDER BY name_en", r => new string?[] { r.GetString(0), Yes(r.GetBoolean(1)) }, token),
            "models" => await Rows(c, null, "SELECT m.name_en,t.name_en,o.name_en,o.active FROM itam_ref.model o JOIN itam_ref.manufacturer m ON m.manufacturer_id=o.manufacturer_id JOIN itam_ref.asset_type t ON t.type_code=o.type_code ORDER BY m.name_en,t.sort_order,o.name_en",
                r => new string?[] { r.GetString(0), r.GetString(1), r.GetString(2), Yes(r.GetBoolean(3)) }, token),
            "values" => await Rows(c, null, "SELECT list_code,item_code,name_en,sort_order,active FROM itam_ref.value_item ORDER BY list_code,sort_order", r => new string?[] { r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3).ToString(), Yes(r.GetBoolean(4)) }, token),
            "roles" => await Rows(c, null, "SELECT e.account,e.display_name,a.role_code,a.region_code FROM itam_access.role_assignment a JOIN itam_access.employee e ON e.employee_id=a.employee_id ORDER BY e.display_name,a.role_code,a.region_code",
                r => new string?[] { r.GetString(0), r.GetString(1), ItamRoles.Label(r.GetString(2)), S(r, 3) }, token),
            "employees" => await Rows(c, null, "SELECT account,display_name,email,job_title,department,branch,region,sap_id,enabled FROM itam_access.employee ORDER BY enabled DESC,display_name",
                r => new string?[] { r.GetString(0), r.GetString(1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), S(r, 6), S(r, 7), Yes(r.GetBoolean(8)) }, token),
            _ => await Rows(c, null, "SELECT TOP 50000 a.at_utc,COALESCE(e.display_name,CONVERT(nvarchar(36),a.actor_id)),a.entity,a.entity_key,a.action,a.after_json,a.reason FROM itam_access.audit a LEFT JOIN itam_access.employee e ON e.employee_id=a.actor_id ORDER BY a.audit_id DESC",
                r => new string?[] { r.GetDateTime(0).ToString("yyyy-MM-dd HH:mm:ss"), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), S(r, 5), S(r, 6) }, token),
        };
        var day = UtcNow.AddHours(3).ToString("yyyy-MM-dd");
        return new($"IT Assets - {list.Title} ({day}).csv", "text/csv; charset=utf-8", Csv.Write(list.Columns, rows));
    }

    // ---- Import ------------------------------------------------------------------------------------------------------------------
    public async Task<ItamCsvResult> ImportCsv(ItamUser user, string key, ItamCsvImport input, ItamUpload? file, CancellationToken token)
    {
        var list = CsvList(key);
        if (!list.Import) throw new ArgumentException($"{list.Title} cannot be imported.");
        if (file is null || file.Content.Length == 0) throw new ArgumentException("Choose the CSV file.");
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a .csv file (in Excel: File > Save As > CSV UTF-8).");
        var table = Csv.Read(file.Content);
        if (table.Count == 0) throw new ArgumentException("The file is empty.");
        var header = table[0].Select(h => h.Trim()).ToArray();
        int Col(string name) => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var required = key == "roles" ? ["Username", "Role"] : list.Columns.Where(n => n is not ("Order" or "Active" or "City" or "VAT number" or "Arabic name")).ToArray();
        if (required.FirstOrDefault(n => Col(n) < 0) is { } missing)
            throw new ArgumentException($"The column \"{missing}\" is missing. The first row must be the column names: {string.Join(", ", list.Columns)} (export the list to get the right file).");
        var data = table.Skip(1).Select((cells, i) => (Row: i + 2, Cells: cells)).Where(x => x.Cells.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        if (data.Count == 0) throw new ArgumentException("The file has no rows under the column names.");
        if (data.Count > MaxCsvRows) throw new ArgumentException($"Up to {MaxCsvRows} rows per file.");

        var reason = $"CSV import ({Path.GetFileName(file.FileName)})" + (string.IsNullOrWhiteSpace(input.Reason) ? "" : ": " + input.Reason.Trim());
        if (reason.Length > 1000) reason = reason[..1000];
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        CsvAccess(s, key, import: true);
        int added = 0, changed = 0, unchanged = 0; var errors = new List<ItamCsvRowError>();
        foreach (var (row, cells) in data)
        {
            string? V(string name) { var i = Col(name); return i < 0 || i >= cells.Length ? null : Csv.Clean(cells[i]); }
            await tx.SaveAsync("row", token);
            try
            {
                var (area, data1, isNew, same) = await CsvRow(c, tx, key, V, n => Col(n) >= 0, token);
                if (same) { unchanged++; continue; }
                await ApplyChange(c, tx, s, new ItamAdminChange(area, "save", JsonSerializer.SerializeToElement(data1, Json), reason), token);
                if (isNew) added++; else changed++;
            }
            catch (Exception ex) when (ex is ArgumentException or ItamAccessDenied or DbException)
            {
                await tx.RollbackAsync("row", token);
                errors.Add(new(row, ex is DbException ? "The database refused this row (check the values)." : ex.Message));
                if (errors.Count >= 50) break;
            }
        }
        var save = !input.CheckOnly && errors.Count == 0;
        if (save) await tx.CommitAsync(token); else await tx.RollbackAsync(token);
        return new(list.Title, data.Count, added, changed, unchanged, errors, save);
    }

    static bool Active(string? v) => string.IsNullOrWhiteSpace(v) ? true : v.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "active" => true,
        "no" or "n" or "false" or "0" or "inactive" => false,
        _ => throw new ArgumentException($"Active: Yes or No (not \"{v}\")."),
    };

    static int Order(string? v) => string.IsNullOrWhiteSpace(v) ? 0 : int.TryParse(v.Trim(), out var n) && n is >= 0 and <= 100000 ? n : throw new ArgumentException($"Order: a whole number (not \"{v}\").");

    /// <summary>One CSV row to the form's change: the area, its data, whether it is new, and whether it is already the same.</summary>
    async Task<(string Area, object Data, bool IsNew, bool Same)> CsvRow(DbConnection c, DbTransaction tx, string key, Func<string, string?> v, Func<string, bool> inFile, CancellationToken token)
    {
        static bool Eq(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
        switch (key)
        {
            case "types":
            case "regions":
            {
                var code = key == "types" ? (v("Code") ?? "").ToUpperInvariant() : v("Code");
                var name = v("Name"); var sort = Order(v("Order")); var active = Active(v("Active"));
                var sql = key == "types" ? "SELECT name_en,sort_order,active FROM itam_ref.asset_type WHERE type_code=@c" : "SELECT name_en,sort_order,active FROM itam_ref.region WHERE region_code=@c";
                var old = (await Rows(c, tx, sql, r => (N: r.GetString(0), S: r.GetInt32(1), A: r.GetBoolean(2)), token, ("@c", code ?? ""))).Cast<(string N, int S, bool A)?>().SingleOrDefault();
                return (key == "types" ? "type" : "region", new { code, name, sort, active }, old is null, old is { } o && Eq(o.N, name) && o.S == sort && o.A == active);
            }
            case "locations":
            {
                var name = v("Name"); var kindText = (v("Kind") ?? "").Replace(" ", "").ToLowerInvariant();
                var kind = kindText switch { "itwarehouse" or "warehouse" => "ITWarehouse", "branch" => "Branch", _ => throw new ArgumentException("Kind: IT warehouse or Branch.") };
                var region = await Scalar(c, tx, "SELECT region_code FROM itam_ref.region WHERE region_code=@r OR name_en=@r", token, ("@r", v("Region") ?? "")) as string ?? throw new ArgumentException($"Region \"{v("Region")}\" is not in the list of regions.");
                var city = v("City"); var active = Active(v("Active"));
                var old = (await Rows(c, tx, "SELECT location_id,kind,region_code,city,active FROM itam_ref.location WHERE name_en=@n", r => (Id: r.GetInt32(0), K: r.GetString(1), R: r.GetString(2), C: S(r, 3), A: r.GetBoolean(4)), token, ("@n", name ?? ""))).Cast<(int Id, string K, string R, string? C, bool A)?>().SingleOrDefault();
                return ("location", new { id = old?.Id, name, kind, region, city, active }, old is null, old is { } o && o.K == kind && o.R == region && Eq(o.C, city) && o.A == active);
            }
            case "suppliers":
            {
                var name = v("Name"); var active = Active(v("Active"));
                var old = (await Rows(c, tx, "SELECT supplier_id,vat_no,active,name_ar FROM itam_ref.supplier WHERE name_en=@n", r => (Id: r.GetInt32(0), V: S(r, 1), A: r.GetBoolean(2), Ar: S(r, 3)), token, ("@n", name ?? ""))).Cast<(int Id, string? V, bool A, string? Ar)?>().SingleOrDefault();
                // A column left out of the file keeps what the supplier has (an empty cell clears it).
                var vat = inFile("VAT number") ? v("VAT number") : old?.V; var nameAr = inFile("Arabic name") ? v("Arabic name") : old?.Ar;
                return ("supplier", new { id = old?.Id, name, nameAr, vat, active }, old is null, old is { } o && Eq(o.V, vat) && Eq(o.Ar, nameAr) && o.A == active);
            }
            case "manufacturers":
            {
                var name = v("Name"); var active = Active(v("Active"));
                var old = (await Rows(c, tx, "SELECT manufacturer_id,active FROM itam_ref.manufacturer WHERE name_en=@n", r => (Id: r.GetInt32(0), A: r.GetBoolean(1)), token, ("@n", name ?? ""))).Cast<(int Id, bool A)?>().SingleOrDefault();
                return ("manufacturer", new { id = old?.Id, name, active }, old is null, old is { } o && o.A == active);
            }
            case "models":
            {
                var maker = await Scalar(c, tx, "SELECT manufacturer_id FROM itam_ref.manufacturer WHERE name_en=@n", token, ("@n", v("Manufacturer") ?? "")) as int?
                    ?? throw new ArgumentException($"Manufacturer \"{v("Manufacturer")}\" is not in the list: add it (or import Manufacturers) first.");
                var type = await Scalar(c, tx, "SELECT type_code FROM itam_ref.asset_type WHERE type_code=@t OR name_en=@t", token, ("@t", v("Type") ?? "")) as string ?? throw new ArgumentException($"Type \"{v("Type")}\" is not an asset type.");
                var name = v("Model"); var active = Active(v("Active"));
                var old = (await Rows(c, tx, "SELECT model_id,active FROM itam_ref.model WHERE manufacturer_id=@m AND type_code=@t AND name_en=@n", r => (Id: r.GetInt32(0), A: r.GetBoolean(1)), token, ("@m", maker), ("@t", type), ("@n", name ?? ""))).Cast<(int Id, bool A)?>().SingleOrDefault();
                return ("model", new { id = old?.Id, name, manufacturer = maker, type, active }, old is null, old is { } o && o.A == active);
            }
            case "values":
            {
                var listText = v("List") ?? "";
                var list = ValueLists.FirstOrDefault(l => string.Equals(l.Code, listText, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Label, listText, StringComparison.OrdinalIgnoreCase)).Code
                    ?? throw new ArgumentException($"List: {string.Join(", ", ValueLists.Select(l => l.Code))}.");
                var code = v("Code"); var name = v("Name"); var sort = Order(v("Order")); var active = Active(v("Active"));
                var old = (await Rows(c, tx, "SELECT name_en,sort_order,active FROM itam_ref.value_item WHERE list_code=@l AND item_code=@c", r => (N: r.GetString(0), S: r.GetInt32(1), A: r.GetBoolean(2)), token, ("@l", list), ("@c", code ?? ""))).Cast<(string N, int S, bool A)?>().SingleOrDefault();
                return ("value", new { list, code, name, sort, active }, old is null, old is { } o && Eq(o.N, name) && o.S == sort && o.A == active);
            }
            case "roles":
            {
                var account = ItamSignIn.Account(v("Username")) ?? throw new ArgumentException("Username is empty.");
                var employee = await Scalar(c, tx, "SELECT employee_id FROM itam_access.employee WHERE account=@a AND enabled=1", token, ("@a", account)) as Guid?
                    ?? throw new ArgumentException($"{account} is not an active employee (employees come from AD every hour).");
                var roleText = v("Role") ?? "";
                var role = ItamRoles.All.FirstOrDefault(r => string.Equals(r, roleText.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) || string.Equals(ItamRoles.Label(r), roleText, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Role \"{roleText}\" is not an IT Assets role.");
                var region = ItamRoles.PerRegion(role) ? v("Region") : null;
                var has = await Scalar(c, tx, "SELECT 1 FROM itam_access.role_assignment WHERE employee_id=@e AND role_code=@r AND (region_code=@g OR (region_code IS NULL AND @g IS NULL))", token, ("@e", employee), ("@r", role), ("@g", region)) is not null;
                return ("role", new { employee, role, region }, !has, has);
            }
            default: throw new ArgumentException("Unknown list.");
        }
    }
}

/// <summary>CSV for Excel: UTF-8 with BOM, comma, CRLF, quoted when needed. A cell that starts with = + - @ is written with a
/// leading apostrophe so Excel shows it as text and never runs it as a formula (and Clean removes it again on import).</summary>
public static class Csv
{
    public static byte[] Write(IEnumerable<string> header, IEnumerable<string?[]> rows)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string?> cells) => sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        Line(header);
        foreach (var r in rows) Line(r);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    static string Cell(string? v)
    {
        v ??= "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n', ';']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>A cell as typed: trimmed, null when empty, the formula guard removed.</summary>
    public static string? Clean(string? v)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@') v = v[1..];
        return v;
    }

    /// <summary>Reads a CSV file (UTF-8, or the Windows code page when it is not UTF-8); the separator (comma, semicolon or tab) is
    /// taken from the first line, as Excel writes it in different regional settings.</summary>
    public static List<string[]> Read(byte[] content)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(content); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var firstLine = text.Split('\n')[0];
        var sep = new[] { ',', ';', '\t' }.OrderByDescending(ch => firstLine.Count(x => x == ch)).First();
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add([.. row]); row.Clear();
                if (rows.Count > ItamStore.MaxCsvRows + 1) throw new ArgumentException($"Up to {ItamStore.MaxCsvRows} rows per file.");
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add([.. row]); }
        return rows;
    }
}
