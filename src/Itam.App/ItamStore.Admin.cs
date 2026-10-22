using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamAdminChange(string Area, string Action, JsonElement Data, string? Reason);
public sealed record ItamBackupInput(Guid? PersonId, Guid? BackupId, DateOnly? From, DateOnly? To, string? Note);

public sealed partial class ItamStore
{
    // The signed-in person and the lists the forms use ------------------------------------------------------------------
    public async Task<object> Me(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var roles = await Rows(c, null, "SELECT role_code,region_code FROM itam_access.role_assignment WHERE employee_id=@i ORDER BY role_code,region_code", r => new { code = r.GetString(0), label = ItamRoles.Label(r.GetString(0)), region = S(r, 1) }, token, ("@i", s.Id));
        return new
        {
            id = s.Id, name = s.Name, roles,
            can = new { viewAssets = s.CanViewAny, register = s.CanRegisterAny, poView = s.PoView, poManage = s.PoManage, rolesManage = s.RolesManage, masterData = s.MasterData,
                auditView = s.AuditView, backupsView = s.BackupsView, ownBackup = s.OwnBackup, keys = s.CanSeeKeys, handover = s.CanHandoverAny || s.ViewsAll || s.AdTasks || s.CucmTasks, tasks = s.AdTasks || s.CucmTasks || s.ViewsAll, admin = s.MasterData || s.RolesManage || s.AuditView },
            lists = await Lists(c, token), regions = s.Regions, viewsAll = s.ViewsAll, openTasks = await OpenTaskCount(c, s, token)
        };
    }

    static async Task<object> Lists(DbConnection c, CancellationToken token) => new
    {
        types = await Rows(c, null, "SELECT type_code,name_en,sort_order,active FROM itam_ref.asset_type ORDER BY sort_order", r => new { code = r.GetString(0), name = r.GetString(1), sort = r.GetInt32(2), active = r.GetBoolean(3),
            fields = ItamTypeFields.For(r.GetString(0)), manufacturerUsed = ItamTypeFields.ManufacturerUsed(r.GetString(0)), modelUsed = ItamTypeFields.ModelUsed(r.GetString(0)), serialUsed = ItamTypeFields.SerialUsed(r.GetString(0)), serialRequired = ItamTypeFields.SerialRequired(r.GetString(0)), serviceTag = ItamTypeFields.ServiceTagUsed(r.GetString(0)),
            mac = ItamTypeFields.MacUsed(r.GetString(0)), macRequired = ItamTypeFields.MacRequired(r.GetString(0)), ip = ItamTypeFields.IpUsed(r.GetString(0)), key = ItamTypeFields.KeyUsed(r.GetString(0)) }, token),
        regions = await Rows(c, null, "SELECT region_code,name_en,sort_order,active FROM itam_ref.region ORDER BY sort_order", r => new { code = r.GetString(0), name = r.GetString(1), sort = r.GetInt32(2), active = r.GetBoolean(3) }, token),
        locations = await Rows(c, null, "SELECT location_id,name_en,kind,region_code,city,active FROM itam_ref.location ORDER BY region_code,name_en", r => new { id = r.GetInt32(0), name = r.GetString(1), kind = r.GetString(2), region = r.GetString(3), city = S(r, 4), active = r.GetBoolean(5) }, token),
        suppliers = await Rows(c, null, "SELECT supplier_id,name_en,vat_no,active FROM itam_ref.supplier ORDER BY name_en", r => new { id = r.GetInt32(0), name = r.GetString(1), vat = S(r, 2), active = r.GetBoolean(3) }, token),
        manufacturers = await Rows(c, null, "SELECT manufacturer_id,name_en,active FROM itam_ref.manufacturer ORDER BY name_en", r => new { id = r.GetInt32(0), name = r.GetString(1), active = r.GetBoolean(2) }, token),
        models = await Rows(c, null, "SELECT model_id,manufacturer_id,type_code,name_en,active FROM itam_ref.model ORDER BY name_en", r => new { id = r.GetInt32(0), manufacturer = r.GetInt32(1), type = r.GetString(2), name = r.GetString(3), active = r.GetBoolean(4) }, token),
        values = await Rows(c, null, "SELECT list_code,item_code,name_en,sort_order,active FROM itam_ref.value_item ORDER BY list_code,sort_order", r => new { list = r.GetString(0), code = r.GetString(1), name = r.GetString(2), sort = r.GetInt32(3), active = r.GetBoolean(4) }, token),
        statuses = ItamStatuses.All.Select(x => new { code = x.Code, label = x.Label })
    };

    public async Task<object> Employees(ItamUser user, string? text, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.HasItRole, "You cannot search employees.");
        var like = "%" + (text ?? "").Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        return await Rows(c, null, "SELECT TOP 30 employee_id,display_name,account,job_title,department,enabled FROM itam_access.employee WHERE display_name LIKE @q OR account LIKE @q OR email LIKE @q ORDER BY enabled DESC,display_name",
            r => new { id = r.GetGuid(0), name = r.GetString(1), account = r.GetString(2), title = S(r, 3), department = S(r, 4), enabled = r.GetBoolean(5) }, token, ("@q", like));
    }

    // Administration -------------------------------------------------------------------------------------------------------------------
    public async Task<object> Admin(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.MasterData || s.RolesManage || s.AuditView, "The Administration Center is for the IT Administrator, the IT Manager and the Auditor.");
        var names = await Names(c, null, token);
        string N(Guid g) => names.TryGetValue(g, out var n) ? n : g.ToString();
        return new
        {
            can = new { masterData = s.MasterData, roles = s.RolesManage, audit = s.AuditView },
            lists = await Lists(c, token),
            roles = await Rows(c, null, "SELECT a.assignment_id,a.employee_id,e.display_name,e.enabled,a.role_code,a.region_code,a.created_at_utc FROM itam_access.role_assignment a JOIN itam_access.employee e ON e.employee_id=a.employee_id ORDER BY a.role_code,e.display_name,a.region_code",
                r => new { id = r.GetInt32(0), employee = r.GetGuid(1), name = r.GetString(2), enabled = r.GetBoolean(3), role = r.GetString(4), roleLabel = ItamRoles.Label(r.GetString(4)), region = S(r, 5), since = Utc(r, 6) }, token),
            roleCodes = ItamRoles.All.Select(r => new { code = r, label = ItamRoles.Label(r) }),
            audit = s.AuditView ? await Rows(c, null, "SELECT TOP 300 at_utc,actor_id,entity,entity_key,action,after_json,reason FROM itam_access.audit ORDER BY audit_id DESC",
                r => (object)new { at = Utc(r, 0), who = N(r.GetGuid(1)), entity = r.GetString(2), key = r.GetString(3), action = r.GetString(4), detail = S(r, 5), reason = S(r, 6) }, token) : [],
            employees = new { total = await Scalar(c, null, "SELECT COUNT(*) FROM itam_access.employee", token), active = await Scalar(c, null, "SELECT COUNT(*) FROM itam_access.employee WHERE enabled=1", token),
                lastRead = await Scalar(c, null, "SELECT MAX(synced_at_utc) FROM itam_access.employee", token) }
        };
    }

    sealed record RefIn(int? Id, string? Code, string? Name, string? Kind, string? Region, string? City, string? Vat, int? Manufacturer, string? Type, string? List, int? Sort, bool Active);
    sealed record RoleIn(int? Id, Guid? Employee, string? Role, string? Region);

    public async Task Change(ItamUser user, ItamAdminChange change, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Task Log(string key, string action, object? after) => Audit(c, tx, s.Id, change.Area, key, action, null, after, change.Reason, token);
        if (change.Area == "role")
        {
            Require(s.RolesManage, "Only the IT Manager and the IT Administrator give or remove roles.");
            var x = change.Data.Deserialize<RoleIn>(Json) ?? throw new ArgumentException("The request is empty.");
            if (change.Action == "delete")
            {
                var row = (await Rows(c, tx, "SELECT employee_id,role_code,region_code FROM itam_access.role_assignment WHERE assignment_id=@i", r => (E: r.GetGuid(0), Role: r.GetString(1), Region: S(r, 2)), token, ("@i", x.Id ?? 0))).SingleOrDefault();
                if (row.Role is null) throw new ArgumentException("The role was not found.");
                await Exec(c, tx, "DELETE itam_access.role_assignment WHERE assignment_id=@i", token, ("@i", x.Id));
                if (Convert.ToInt32(await Scalar(c, tx, "SELECT COUNT(*) FROM itam_access.role_assignment WHERE role_code IN(N'ItManager',N'ItAdministrator')", token)) == 0)
                    throw new ArgumentException("Someone must keep the IT Manager or IT Administrator role, so roles can still be given.");
                await Log(row.E.ToString(), "Remove role", new { row.Role, row.Region });
            }
            else
            {
                var role = ItamRoles.All.Contains(x.Role) ? x.Role! : throw new ArgumentException("Choose a role.");
                if (await Scalar(c, tx, "SELECT 1 FROM itam_access.employee WHERE employee_id=@e AND enabled=1", token, ("@e", x.Employee ?? Guid.Empty)) is null) throw new ArgumentException("Choose an active employee.");
                string? region = role == ItamRoles.SupportSpecialist ? (await Scalar(c, tx, "SELECT region_code FROM itam_ref.region WHERE region_code=@r AND active=1", token, ("@r", x.Region ?? "")) as string ?? throw new ArgumentException("Choose the region of the IT Support Specialist.")) : null;
                if (await Scalar(c, tx, "SELECT 1 FROM itam_access.role_assignment WHERE employee_id=@e AND role_code=@r AND (region_code=@g OR (region_code IS NULL AND @g IS NULL))", token, ("@e", x.Employee), ("@r", role), ("@g", region)) is not null)
                    throw new ArgumentException("The person already has this role" + (region is null ? "." : $" for {region}."));
                await Exec(c, tx, "INSERT itam_access.role_assignment(employee_id,role_code,region_code,created_by) VALUES(@e,@r,@g,@b)", token, ("@e", x.Employee), ("@r", role), ("@g", region), ("@b", s.Id));
                await Log(x.Employee.ToString()!, "Give role", new { role, region });
            }
            await tx.CommitAsync(token);
            return;
        }
        Require(s.MasterData, "Only the IT Administrator changes master data.");
        var d = change.Data.Deserialize<RefIn>(Json) ?? throw new ArgumentException("The request is empty.");
        var name = Text(d.Name, "Name", 200);
        switch (change.Area)
        {
            case "type":
            {
                var code = Text(d.Code, "Short code", 10).ToUpperInvariant();
                if (code.Any(ch => ch is < 'A' or > 'Z')) throw new ArgumentException("Short code: capital letters only, for example PC.");
                if (await Exec(c, tx, "UPDATE itam_ref.asset_type SET name_en=@n,sort_order=@s,active=@a WHERE type_code=@c", token, ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active)) == 0)
                    await Exec(c, tx, "INSERT itam_ref.asset_type(type_code,name_en,sort_order,active) VALUES(@c,@n,@s,@a)", token, ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active));
                await Log(code, "Save", new { code, name, d.Sort, d.Active }); break;
            }
            case "region":
            {
                var code = Text(d.Code, "Code", 20);
                if (await Exec(c, tx, "UPDATE itam_ref.region SET name_en=@n,sort_order=@s,active=@a WHERE region_code=@c", token, ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active)) == 0)
                    await Exec(c, tx, "INSERT itam_ref.region(region_code,name_en,sort_order,active) VALUES(@c,@n,@s,@a)", token, ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active));
                await Log(code, "Save", new { code, name, d.Sort, d.Active }); break;
            }
            case "location":
            {
                var kind = d.Kind is "ITWarehouse" or "Branch" ? d.Kind : throw new ArgumentException("Choose IT warehouse or branch.");
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.region WHERE region_code=@r", token, ("@r", d.Region ?? "")) is null) throw new ArgumentException("Choose the region.");
                if (await Scalar(c, tx, "SELECT location_id FROM itam_ref.location WHERE name_en=@n AND location_id<>@i", token, ("@n", name), ("@i", d.Id ?? 0)) is not null) throw new ArgumentException($"{name} is already in the list.");
                if (d.Id is int id && await Scalar(c, tx, "SELECT region_code FROM itam_ref.location WHERE location_id=@i", token, ("@i", id)) is string oldRegion && oldRegion != d.Region
                    && await Scalar(c, tx, "SELECT 1 FROM itam_data.asset WHERE location_id=@i", token, ("@i", id)) is not null)
                    throw new ArgumentException("Assets are kept here: its region cannot change (it decides who sees them).");
                (string, object?)[] v = [("@i", d.Id ?? 0), ("@n", name), ("@k", kind), ("@r", d.Region), ("@c", Optional(d.City, "City", 100)), ("@a", d.Active)];
                if (d.Id is > 0) await Exec(c, tx, "UPDATE itam_ref.location SET name_en=@n,kind=@k,region_code=@r,city=@c,active=@a WHERE location_id=@i", token, v);
                else await Exec(c, tx, "INSERT itam_ref.location(name_en,kind,region_code,city,active) VALUES(@n,@k,@r,@c,@a)", token, v);
                await Log(name, "Save", new { name, kind, d.Region, d.City, d.Active }); break;
            }
            case "supplier":
            {
                var vat = Optional(d.Vat, "VAT number", 20);
                if (vat is not null && (vat.Length != 15 || vat.Any(ch => !char.IsAsciiDigit(ch)))) throw new ArgumentException("VAT number: 15 digits.");
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE name_en=@n AND supplier_id<>@i", token, ("@n", name), ("@i", d.Id ?? 0)) is not null) throw new ArgumentException($"{name} is already in the list.");
                if (d.Id is > 0) await Exec(c, tx, "UPDATE itam_ref.supplier SET name_en=@n,vat_no=@v,active=@a WHERE supplier_id=@i", token, ("@i", d.Id), ("@n", name), ("@v", vat), ("@a", d.Active));
                else await Exec(c, tx, "INSERT itam_ref.supplier(name_en,vat_no,active) VALUES(@n,@v,@a)", token, ("@n", name), ("@v", vat), ("@a", d.Active));
                await Log(name, "Save", new { name, vat, d.Active }); break;
            }
            case "manufacturer":
            {
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.manufacturer WHERE name_en=@n AND manufacturer_id<>@i", token, ("@n", name), ("@i", d.Id ?? 0)) is not null) throw new ArgumentException($"{name} is already in the list.");
                if (d.Id is > 0) await Exec(c, tx, "UPDATE itam_ref.manufacturer SET name_en=@n,active=@a WHERE manufacturer_id=@i", token, ("@i", d.Id), ("@n", name), ("@a", d.Active));
                else await Exec(c, tx, "INSERT itam_ref.manufacturer(name_en,active) VALUES(@n,@a)", token, ("@n", name), ("@a", d.Active));
                await Log(name, "Save", new { name, d.Active }); break;
            }
            case "model":
            {
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.manufacturer WHERE manufacturer_id=@m", token, ("@m", d.Manufacturer ?? 0)) is null) throw new ArgumentException("Choose the manufacturer.");
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.asset_type WHERE type_code=@t", token, ("@t", d.Type ?? "")) is null) throw new ArgumentException("Choose the product type.");
                if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.model WHERE manufacturer_id=@m AND type_code=@t AND name_en=@n AND model_id<>@i", token, ("@m", d.Manufacturer), ("@t", d.Type), ("@n", name), ("@i", d.Id ?? 0)) is not null) throw new ArgumentException($"{name} is already in the list.");
                if (d.Id is int mid && mid > 0)
                {
                    if (await Scalar(c, tx, "SELECT 1 FROM itam_data.asset WHERE model_id=@i UNION SELECT 1 FROM itam_data.po_line WHERE model_id=@i", token, ("@i", mid)) is not null
                        && await Scalar(c, tx, "SELECT 1 FROM itam_ref.model WHERE model_id=@i AND (manufacturer_id<>@m OR type_code<>@t)", token, ("@i", mid), ("@m", d.Manufacturer), ("@t", d.Type)) is not null)
                        throw new ArgumentException("Assets use this model: its manufacturer and type cannot change (fix the name only).");
                    await Exec(c, tx, "UPDATE itam_ref.model SET manufacturer_id=@m,type_code=@t,name_en=@n,active=@a WHERE model_id=@i", token, ("@i", mid), ("@m", d.Manufacturer), ("@t", d.Type), ("@n", name), ("@a", d.Active));
                }
                else await Exec(c, tx, "INSERT itam_ref.model(manufacturer_id,type_code,name_en,active) VALUES(@m,@t,@n,@a)", token, ("@m", d.Manufacturer), ("@t", d.Type), ("@n", name), ("@a", d.Active));
                await Log(name, "Save", new { name, d.Manufacturer, d.Type, d.Active }); break;
            }
            case "value":
            {
                var list = d.List is "ComputerType" or "OperatingSystem" or "PrinterType" or "TonerType" ? d.List : throw new ArgumentException("Unknown list.");
                var code = Text(d.Code, "Code", 60);
                if (await Exec(c, tx, "UPDATE itam_ref.value_item SET name_en=@n,sort_order=@s,active=@a WHERE list_code=@l AND item_code=@c", token, ("@l", list), ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active)) == 0)
                    await Exec(c, tx, "INSERT itam_ref.value_item(list_code,item_code,name_en,sort_order,active) VALUES(@l,@c,@n,@s,@a)", token, ("@l", list), ("@c", code), ("@n", name), ("@s", d.Sort ?? 0), ("@a", d.Active));
                await Log($"{list}:{code}", "Save", new { list, code, name, d.Sort, d.Active }); break;
            }
            default: throw new ArgumentException("Unknown change.");
        }
        await tx.CommitAsync(token);
    }

    // Backups during leave (My Backup) ----------------------------------------------------------------------------------------------
    public async Task<object> Backups(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.OwnBackup || s.BackupsView, "My Backup is for people with an IT role.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var rows = await Rows(c, null, "SELECT b.backup_id,b.person_id,p.display_name,b.backup_employee_id,k.display_name,b.leave_from,b.leave_to,b.note,b.set_by,e.display_name,b.set_at_utc,b.cancelled_at_utc " +
            "FROM itam_access.leave_backup b JOIN itam_access.employee p ON p.employee_id=b.person_id JOIN itam_access.employee k ON k.employee_id=b.backup_employee_id LEFT JOIN itam_access.employee e ON e.employee_id=b.set_by " +
            (s.BackupsView ? "" : "WHERE b.person_id=@me OR b.backup_employee_id=@me ") + "ORDER BY b.leave_from DESC",
            r =>
            {
                var from = Day(r, 5)!.Value; var to = Day(r, 6)!.Value; var cancelled = !r.IsDBNull(11);
                return new { id = r.GetInt32(0), person = r.GetGuid(1), personName = r.GetString(2), backup = r.GetGuid(3), backupName = r.GetString(4), from, to, note = S(r, 7), setByName = S(r, 9), setAt = Utc(r, 10),
                    status = cancelled ? "Cancelled" : to < today ? "Ended" : from > today ? "Upcoming" : "Active" };
            }, token, ("@me", s.Id));
        return new { me = s.Id, all = s.BackupsView, canSetForOthers = s.BackupsAll, items = rows };
    }

    public async Task SetBackup(ItamUser user, ItamBackupInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var person = x.PersonId ?? s.Id;
        if (person == s.Id) Require(s.OwnBackup, "My Backup is for people with an IT role.");
        else Require(s.BackupsAll, "Only the IT Manager and the IT Administrator set a backup for someone else.");
        var backup = x.BackupId ?? throw new ArgumentException("Choose your backup.");
        if (backup == person) throw new ArgumentException("You cannot be your own backup.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_access.employee WHERE employee_id=@e AND enabled=1", token, ("@e", backup)) is null) throw new ArgumentException("Choose an active employee as backup.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        if (x.From is null || x.To is null) throw new ArgumentException("Give the leave dates (from and to).");
        if (x.To < x.From) throw new ArgumentException("The leave ends before it starts.");
        if (x.To < today) throw new ArgumentException("The leave is already over.");
        (string, object?)[] range = [("@p", person), ("@k", backup), ("@f", DateParam(x.From)), ("@t", DateParam(x.To))];
        if (await Scalar(c, tx, "SELECT 1 FROM itam_access.leave_backup WHERE person_id=@p AND cancelled_at_utc IS NULL AND leave_from<=@t AND leave_to>=@f", token, range) is not null)
            throw new ArgumentException("A backup is already set for these dates; cancel it first.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_access.leave_backup WHERE person_id=@k AND cancelled_at_utc IS NULL AND leave_from<=@t AND leave_to>=@f", token, range) is not null)
            throw new ArgumentException("Your backup is on leave on the same dates; choose someone else.");
        var note = Optional(x.Note, "Note", 500);
        var id = await Scalar(c, tx, "INSERT itam_access.leave_backup(person_id,backup_employee_id,leave_from,leave_to,note,set_by) OUTPUT inserted.backup_id VALUES(@p,@k,@f,@t,@n,@b)", token, [.. range, ("@n", note), ("@b", s.Id)]);
        await Audit(c, tx, s.Id, "backup", id?.ToString() ?? "", "Set", null, new { person, backup, x.From, x.To, note }, null, token);
        await tx.CommitAsync(token);
    }

    public async Task CancelBackup(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(token);
        var s = await Scope(c, tx, user, token);
        var row = (await Rows(c, tx, "SELECT person_id,leave_to,cancelled_at_utc FROM itam_access.leave_backup WHERE backup_id=@i", r => (Person: r.GetGuid(0), To: Day(r, 1)!.Value, Cancelled: !r.IsDBNull(2)), token, ("@i", id))).SingleOrDefault();
        if (row.Person == Guid.Empty || row.Cancelled) throw new ArgumentException("The backup was not found.");
        Require(row.Person == s.Id || s.BackupsAll, "You can cancel your own backups only.");
        if (row.To < DateOnly.FromDateTime(UtcNow.AddHours(3))) throw new ArgumentException("The leave is over; it stays in the history.");
        await Exec(c, tx, "UPDATE itam_access.leave_backup SET cancelled_at_utc=SYSUTCDATETIME() WHERE backup_id=@i", token, ("@i", id));
        await Audit(c, tx, s.Id, "backup", id.ToString(), "Cancel", null, null, null, token);
        await tx.CommitAsync(token);
    }
}
