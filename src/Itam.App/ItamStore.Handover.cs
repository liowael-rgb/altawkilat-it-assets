using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace Itam.App;

public sealed record ItamDeliveryInput(Guid? EmployeeId, DateOnly? DeliveredOn, string? OwnerType, string? ComputerName, string? Notes);
public sealed record ItamReturnInput(DateTime? ReceivedAt, string? Condition, int? LocationId, string? Notes);
public sealed record ItamAssessmentInput(string? Review, string? Recommendation);
public sealed record ItamTaskDone(string? Note);

/// <summary>Build step 2, handover (forms 03 Asset Delivery, 05 Physical Asset Receipt, 06 Technical Assessment, 35 Asset
/// Label). The employee signs the printed delivery note on paper himself; IT uploads the scan. One open custody per asset.</summary>
public sealed partial class ItamStore
{
    /// <summary>Days after issue before a delivery still waiting for its signed copy is shown as overdue (to confirm).</summary>
    public int SignedCopyDays { get; init; } = 3;
    /// <summary>Address the QR code on the asset label opens (the asset in IT Assets); the Asset No. is appended.</summary>
    public string LabelBaseUrl { get; init; } = "https://hub.altawkilat.com/itasm/a/";

    public static readonly string[] OwnerTypes = ["Personal", "Department", "Temporary", "Other"];
    public static readonly (string Code, string Label)[] ReturnConditions = [("Working", "Working"), ("Damaged", "Damaged"), ("Faulty", "Faulty")];
    public static readonly (string Code, string Label)[] Recommendations = [("Reuse", "Reuse"), ("RepairByIt", "Repair by IT"), ("RepairOutside", "Repair outside"), ("Dispose", "Dispose")];
    static string RecommendationLabel(string code) => Recommendations.FirstOrDefault(r => r.Code == code).Label ?? code;

    internal sealed record NoteRow(string En, string Ar, string? Value);
    internal sealed record NoteSnapshot(List<NoteRow> Employee, List<NoteRow> Product, string EmployeeName, string IssuedByName);

    sealed record HandAsset(int Id, string No, string Type, string TypeName, string Region, string Status, int LocationId, string? Manufacturer, string? Model,
        string? Serial, string? Tag, string? Mac, string? Ip, Dictionary<string, string> Attrs);

    static async Task<HandAsset> LoadHandAsset(DbConnection c, DbTransaction? tx, int id, bool lockRow, CancellationToken token)
    {
        var a = (await Rows(c, tx, "SELECT a.asset_id,a.asset_no,a.type_code,t.name_en,g.region_code,a.status,a.location_id,m.name_en,mo.name_en,a.serial_no,a.service_tag,a.mac_address,a.ip_address " +
            $"FROM itam_data.asset a {(lockRow ? "WITH (UPDLOCK,HOLDLOCK) " : "")}JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id WHERE a.asset_id=@i",
            r => new HandAsset(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), S(r, 7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), []),
            token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The asset was not found.");
        foreach (var (k, v) in await Rows(c, tx, "SELECT attr_code,value FROM itam_data.asset_attribute WHERE asset_id=@i", r => (r.GetString(0), r.GetString(1)), token, ("@i", id))) a.Attrs[k] = v;
        return a;
    }

    /// <summary>The product rows printed on the notes, in the order of the SharePoint delivery note, for the asset's type.</summary>
    static async Task<List<NoteRow>> ProductRows(DbConnection c, DbTransaction? tx, HandAsset a, string? computerName, CancellationToken token)
    {
        var names = (await Rows(c, tx, "SELECT list_code,item_code,name_en FROM itam_ref.value_item", r => (r.GetString(0) + "|" + r.GetString(1), r.GetString(2)), token)).ToDictionary(x => x.Item1, x => x.Item2);
        string? Attr(string code) { if (!a.Attrs.TryGetValue(code, out var v)) return null; var f = ItamTypeFields.For(a.Type).FirstOrDefault(x => x.Code == code); return f?.List is { } l && names.TryGetValue(l + "|" + v, out var n) ? n : v; }
        var rows = new List<NoteRow> { new("Product Name", "اسم المنتج", a.TypeName) };
        void Add(string en, string ar, string? v) { if (!string.IsNullOrWhiteSpace(v)) rows.Add(new(en, ar, v)); }
        Add("Computer Type", "نوع الحاسوب", Attr("ComputerType")); Add("Computer Name", "اسم الحاسوب", computerName ?? Attr("ComputerName"));
        Add("Software Name", "اسم البرنامج", Attr("Software")); Add("Device Name", "اسم الجهاز", Attr("DeviceName"));
        Add("Manufacturer", "الشركة المصنعة", a.Manufacturer); Add("Device Model", "الموديل", a.Model); Add("Toner Code", "رمز الحبر", Attr("TonerCode"));
        Add("Processor", "نوع المعالج", Attr("Processor")); Add("Capacity (GB)", "المساحة التخزينية", Attr("CapacityGb")); Add("Memory (GB)", "مساحة الذاكرة", Attr("MemoryGb"));
        Add("Service Tag", "رمز الخدمة", a.Tag); Add("Serial No.", "الرقم التسلسلي", a.Serial); Add("MAC Address", "عنوان MAC", a.Mac); Add("IP Address", "عنوان IP", a.Ip);
        Add("Extension No.", "رقم التحويلة", Attr("Extension")); Add("Printer Type", "نوع الطابعة", Attr("PrinterType")); Add("Printer Location", "موقع الطابعة", Attr("PrinterLocation"));
        Add("Toner Type", "نوع الحبر", Attr("TonerType")); Add("Device Description", "وصف الجهاز", Attr("DeviceDescription")); Add("Operating System", "نظام التشغيل", Attr("OperatingSystem"));
        return rows;
    }

    // Deliver ------------------------------------------------------------------------------------------------------------------------
    /// <summary>Deliver an In Store asset to an active employee (form 03): delivery note Asset No.-n, the asset In Use with the
    /// employee, Waiting for signed copy until IT uploads the copy the employee signed on paper.</summary>
    public async Task<object> Deliver(ItamUser user, int assetId, ItamDeliveryInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var a = await LoadHandAsset(c, tx, assetId, true, token);
        Require(s.CanHandover(a.Region), $"You can deliver assets in your regions only ({string.Join(", ", s.Regions)}).");
        if (a.Status != "InStore") throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}: only an asset In Store can be delivered.");
        if (await Scalar(c, tx, "SELECT delivery_no FROM itam_data.delivery WHERE asset_id=@a AND received_at_utc IS NULL", token, ("@a", a.Id)) is string open)
            throw new ArgumentException($"{a.No} is still on delivery note {open}: receive it back first.");
        var emp = (await Rows(c, tx, "SELECT e.display_name,e.account,e.email,e.mobile,e.job_title,e.department,e.branch,e.company,e.hr_code,m.display_name,e.enabled " +
            "FROM itam_access.employee e LEFT JOIN itam_access.employee m ON m.employee_id=e.manager_id WHERE e.employee_id=@e",
            r => (Name: r.GetString(0), Account: r.GetString(1), Email: S(r, 2), Mobile: S(r, 3), Title: S(r, 4), Dept: S(r, 5), Branch: S(r, 6), Company: S(r, 7), Hr: S(r, 8), Manager: S(r, 9), Enabled: r.GetBoolean(10)),
            token, ("@e", x.EmployeeId ?? Guid.Empty))).SingleOrDefault();
        if (emp.Name is null) throw new ArgumentException("Choose the employee.");
        if (!emp.Enabled) throw new ArgumentException($"{emp.Name} is disabled in Active Directory: new deliveries go to active employees only.");
        var owner = OwnerTypes.Contains(x.OwnerType) ? x.OwnerType! : throw new ArgumentException("Choose the owner type (Personal, Department, Temporary or Other).");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var on = x.DeliveredOn ?? today; if (on > today) throw new ArgumentException("The delivery date cannot be in the future.");
        string? computer = null;
        if (a.Type == "PC")
        {
            // Computer-name rule: required for computers; refused while it is on another open delivery; freed by the receipt.
            computer = (x.ComputerName ?? "").Trim().ToUpperInvariant();
            if (computer.Length == 0) throw new ArgumentException("Computer name is required for a computer.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(computer, "^[A-Z0-9-]{1,15}$")) throw new ArgumentException("Computer name: up to 15 letters, numbers or dashes.");
            if (await Scalar(c, tx, "SELECT d.delivery_no FROM itam_data.delivery d WHERE d.computer_name=@n AND d.received_at_utc IS NULL", token, ("@n", computer)) is string other)
                throw new ArgumentException($"Computer name {computer} is on delivery note {other}: choose another name.");
        }
        var notes = Optional(x.Notes, "Notes", 1000);
        var no = $"{a.No}-{await Next(c, tx, "Delivery", token)}";
        var employeeRows = new List<NoteRow> { new("HR Code", "الكود الوظيفي", emp.Hr), new("Employee Name", "اسم الموظف", emp.Name), new("Employee Email", "البريد الالكتروني", emp.Email),
            new("Mobile", "رقم الجوال", emp.Mobile), new("Job Title", "المسمى الوظيفي", emp.Title), new("Branch Name", "اسم الفرع", emp.Branch), new("Department", "اسم القسم", emp.Dept),
            new("Company Name", "اسم الشركة", emp.Company), new("Direct Manager", "المدير المباشر", emp.Manager) };
        var snapshot = new NoteSnapshot(employeeRows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList(), await ProductRows(c, tx, a, computer, token), emp.Name, s.Name);
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.delivery(delivery_no,asset_id,employee_id,delivered_on,owner_type,computer_name,notes,from_location_id,snapshot_json,issued_by) " +
            "OUTPUT inserted.delivery_id VALUES(@n,@a,@e,@d,@o,@c,@x,@l,@j,@b)", token,
            ("@n", no), ("@a", a.Id), ("@e", x.EmployeeId), ("@d", DateParam(on)), ("@o", owner), ("@c", computer), ("@x", notes), ("@l", a.LocationId),
            ("@j", JsonSerializer.Serialize(snapshot, Json)), ("@b", s.Id)));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InUse',stock_location=N'Employee' WHERE asset_id=@a", token, ("@a", a.Id));
        if (computer is not null)
            await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'; INSERT itam_data.asset_attribute(asset_id,attr_code,value) VALUES(@a,N'ComputerName',@v)", token, ("@a", a.Id), ("@v", computer));
        await Event(c, tx, a.Id, s.Id, "Delivered", $"Delivery note {no} to {emp.Name} ({owner}){(computer is null ? "" : ", computer name " + computer)}; waiting for the signed copy", token);
        await Audit(c, tx, s.Id, "delivery", no, "Issue", null, new { asset = a.No, employee = emp.Account, on, owner, computer }, null, token);
        await tx.CommitAsync(token);
        return new { id, deliveryNo = no };
    }

    sealed record DeliveryRow(int Id, string No, int AssetId, string AssetNo, string Type, string Region, Guid EmployeeId, DateOnly On, string Owner, string? Computer, string? Notes,
        string Snapshot, Guid IssuedBy, DateTime IssuedAt, long? SignedFile, Guid? SignedBy, DateTime? SignedAt, DateTime? ReceivedAt, Guid? ReceivedBy, string? Condition, int? ReceiptLocation,
        string? ReceiptLocationName, string? ReceiptNotes, string Version);

    static async Task<DeliveryRow> LoadDelivery(DbConnection c, DbTransaction? tx, int id, bool lockRow, CancellationToken token)
        => (await Rows(c, tx, "SELECT d.delivery_id,d.delivery_no,a.asset_id,a.asset_no,a.type_code,g.region_code,d.employee_id,d.delivered_on,d.owner_type,d.computer_name,d.notes," +
            "d.snapshot_json,d.issued_by,d.issued_at_utc,d.signed_file_id,d.signed_by,d.signed_at_utc,d.received_at_utc,d.received_by,d.receipt_condition,d.receipt_location_id,rl.name_en,d.receipt_notes," +
            $"CONVERT(varchar(30),CONVERT(varbinary(8),d.version),2) FROM itam_data.delivery d {(lockRow ? "WITH (UPDLOCK,HOLDLOCK) " : "")}JOIN itam_data.asset a ON a.asset_id=d.asset_id " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.location rl ON rl.location_id=d.receipt_location_id WHERE d.delivery_id=@i",
            r => new DeliveryRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6), Day(r, 7)!.Value, r.GetString(8), S(r, 9), S(r, 10),
                r.GetString(11), r.GetGuid(12), Utc(r, 13), r.IsDBNull(14) ? null : r.GetInt64(14), r.IsDBNull(15) ? null : r.GetGuid(15), r.IsDBNull(16) ? null : Utc(r, 16),
                r.IsDBNull(17) ? null : Utc(r, 17), r.IsDBNull(18) ? null : r.GetGuid(18), S(r, 19), I(r, 20), S(r, 21), S(r, 22), r.GetString(23)),
            token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The delivery note was not found.");

    /// <summary>Upload the delivery note the employee signed on paper himself (scan or photo). Kept unchanged once uploaded.</summary>
    public async Task UploadSigned(ItamUser user, int deliveryId, ItamUpload? file, CancellationToken token)
    {
        if (file is null) throw new ArgumentException("Attach the scan of the signed delivery note (PDF, JPEG or PNG).");
        var type = CheckFile(file, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var d = await LoadDelivery(c, tx, deliveryId, true, token);
        Require(s.CanHandover(d.Region), "You cannot upload signed delivery notes for this asset.");
        if (d.SignedFile is not null) throw new ArgumentException($"The signed copy of {d.No} is already uploaded; it is kept unchanged.");
        var f = await SaveFile(c, tx, file, type, s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.delivery SET signed_file_id=@f,signed_by=@b,signed_at_utc=SYSUTCDATETIME() WHERE delivery_id=@i", token, ("@f", f), ("@b", s.Id), ("@i", d.Id));
        await Event(c, tx, d.AssetId, s.Id, "Signed copy", $"Signed delivery note {d.No} uploaded", token);
        await Audit(c, tx, s.Id, "delivery", d.No, "Signed copy", null, new { file = file.FileName, bytes = file.Content.Length }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>Receive the asset back physically (form 05): receipt note = delivery number + -R; the asset is In Store where it
    /// is received; the computer name is freed; the infrastructure removal task is created.</summary>
    public async Task<object> ReturnAsset(ItamUser user, int deliveryId, ItamReturnInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var d = await LoadDelivery(c, tx, deliveryId, true, token);
        Require(s.CanHandover(d.Region), "You cannot receive this asset back.");
        if (d.ReceivedAt is not null) throw new ArgumentException($"{d.AssetNo} was already received back on {d.No}-R.");
        var at = x.ReceivedAt?.ToUniversalTime() ?? UtcNow;
        if (at > UtcNow.AddMinutes(5)) throw new ArgumentException("The receipt date cannot be in the future.");
        if (DateOnly.FromDateTime(at.AddHours(3)) < d.On) throw new ArgumentException("The receipt date cannot be before the delivery date.");
        var condition = ReturnConditions.Any(r => r.Code == x.Condition) ? x.Condition! : throw new ArgumentException("Choose the condition (Working, Damaged or Faulty).");
        var loc = (await Rows(c, tx, "SELECT region_code,kind,name_en FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Region: r.GetString(0), Kind: r.GetString(1), Name: r.GetString(2)), token, ("@l", x.LocationId ?? 0))).SingleOrDefault();
        if (loc.Region is null) throw new ArgumentException("Choose the IT warehouse or branch receiving the asset.");
        Require(s.CanHandover(loc.Region), $"You can receive assets into your regions only ({string.Join(", ", s.Regions)}).");
        var notes = Optional(x.Notes, "Notes", 1000);
        await Exec(c, tx, "UPDATE itam_data.delivery SET received_at_utc=@t,received_by=@b,receipt_condition=@c,receipt_location_id=@l,receipt_notes=@n WHERE delivery_id=@i", token,
            ("@t", at), ("@b", s.Id), ("@c", condition), ("@l", x.LocationId), ("@n", notes), ("@i", d.Id));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InStore',stock_location=@k,location_id=@l,device_condition=N'Used' WHERE asset_id=@a", token, ("@k", loc.Kind), ("@l", x.LocationId), ("@a", d.AssetId));
        if (d.Computer is not null) await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'", token, ("@a", d.AssetId));
        var receipt = d.No + "-R";
        // Infrastructure clean-up after a return: computers leave AD and Endpoint Central; IP phone extensions leave CUCM.
        if (d.Type == "PC" && d.Computer is not null)
            await Exec(c, tx, "INSERT itam_data.task(kind,asset_id,delivery_id,subject) VALUES(N'RemoveFromAd',@a,@d,@s)", token, ("@a", d.AssetId), ("@d", d.Id),
                ("@s", $"Remove computer {d.Computer} ({d.AssetNo}) from Active Directory and Endpoint Central"));
        if (d.Type == "IP")
        {
            var ext = await Scalar(c, tx, "SELECT value FROM itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'Extension'", token, ("@a", d.AssetId)) as string;
            await Exec(c, tx, "INSERT itam_data.task(kind,asset_id,delivery_id,subject) VALUES(N'RemoveFromCucm',@a,@d,@s)", token, ("@a", d.AssetId), ("@d", d.Id),
                ("@s", $"Remove {(ext is null ? "the extension" : "extension " + ext)} of IP phone {d.AssetNo} from CUCM"));
        }
        await Event(c, tx, d.AssetId, s.Id, "Received back", $"Receipt note {receipt}: {condition}, kept at {loc.Name}{(notes is null ? "" : ". " + notes)}", token);
        await Audit(c, tx, s.Id, "delivery", d.No, "Receipt", null, new { receipt, condition, location = x.LocationId, at }, null, token);
        await tx.CommitAsync(token);
        return new { receiptNo = receipt };
    }

    /// <summary>Technical assessment of a returned asset (form 06): TA- + delivery number; Dispose starts Pending disposal
    /// approval (the assessment does not approve disposal).</summary>
    public async Task<object> Assess(ItamUser user, int deliveryId, ItamAssessmentInput x, ItamUpload? evidence, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var d = await LoadDelivery(c, tx, deliveryId, true, token);
        Require(s.CanHandover(d.Region), "You cannot assess this asset.");
        if (d.ReceivedAt is null) throw new ArgumentException($"{d.AssetNo} is still with the employee: receive it back before the technical assessment.");
        if (await Scalar(c, tx, "SELECT assessment_no FROM itam_data.assessment WHERE delivery_id=@d", token, ("@d", d.Id)) is string done) throw new ArgumentException($"{done} is already recorded for this delivery.");
        var review = Text(x.Review, "Technical review", 4000);
        var rec = Recommendations.Any(r => r.Code == x.Recommendation) ? x.Recommendation! : throw new ArgumentException("Choose the recommendation.");
        long? file = evidence is null ? null : await SaveFile(c, tx, evidence, CheckFile(evidence, pdfOnly: false), s.Id, token);
        var no = "TA-" + d.No;
        await Exec(c, tx, "INSERT itam_data.assessment(assessment_no,delivery_id,review,recommendation,evidence_file_id,assessed_by) VALUES(@n,@d,@r,@c,@f,@b)", token,
            ("@n", no), ("@d", d.Id), ("@r", review), ("@c", rec), ("@f", file), ("@b", s.Id));
        var status = await Scalar(c, tx, "SELECT status FROM itam_data.asset WHERE asset_id=@a", token, ("@a", d.AssetId)) as string;
        if (rec == "Dispose" && status == "InStore")
            await Exec(c, tx, "UPDATE itam_data.asset SET status=N'PendingDisposal' WHERE asset_id=@a", token, ("@a", d.AssetId));
        await Event(c, tx, d.AssetId, s.Id, "Technical assessment", $"{no}: {RecommendationLabel(rec)}{(rec == "Dispose" && status == "InStore" ? " (Pending disposal approval)" : "")}", token);
        await Audit(c, tx, s.Id, "assessment", no, "Add", null, new { rec, evidence = file is not null }, null, token);
        await tx.CommitAsync(token);
        return new { assessmentNo = no };
    }

    // Pages ------------------------------------------------------------------------------------------------------------------------------
    /// <summary>The handover part of the asset page: every delivery note of the asset (newest first) and what the person may do.</summary>
    public async Task<object> AssetHandover(ItamUser user, int assetId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var a = await LoadHandAsset(c, null, assetId, false, token);
        Require(s.CanView(a.Type, a.Region), "You cannot see this asset.");
        var names = await Names(c, null, token);
        string? N(Guid? g) => g is Guid x && names.TryGetValue(x, out var n) ? n : null;
        var rows = await Rows(c, null, "SELECT delivery_id FROM itam_data.delivery WHERE asset_id=@a ORDER BY delivery_id DESC", r => r.GetInt32(0), token, ("@a", assetId));
        var list = new List<object>();
        foreach (var id in rows)
        {
            var d = await LoadDelivery(c, null, id, false, token);
            var ta = (await Rows(c, null, "SELECT assessment_no,recommendation,review,assessed_by,assessed_at_utc,evidence_file_id FROM itam_data.assessment WHERE delivery_id=@d",
                r => new { no = r.GetString(0), recommendation = RecommendationLabel(r.GetString(1)), review = r.GetString(2), by = N(r.GetGuid(3)), at = Utc(r, 4), evidence = !r.IsDBNull(5) }, token, ("@d", id))).SingleOrDefault();
            list.Add(new
            {
                d.Id, no = d.No, employee = names.GetValueOrDefault(d.EmployeeId), deliveredOn = d.On, ownerType = d.Owner, computerName = d.Computer, notes = d.Notes,
                issuedBy = N(d.IssuedBy), issuedAt = d.IssuedAt, signedAt = d.SignedAt, signedBy = N(d.SignedBy), hasSigned = d.SignedFile is not null,
                overdue = d.SignedFile is null && d.ReceivedAt is null && d.IssuedAt < UtcNow.AddDays(-SignedCopyDays),
                receivedAt = d.ReceivedAt, receivedBy = N(d.ReceivedBy), condition = d.Condition, receivedAtLocation = d.ReceiptLocationName, receiptNotes = d.ReceiptNotes,
                receiptNo = d.ReceivedAt is null ? null : d.No + "-R", assessment = ta
            });
        }
        return new
        {
            canHandle = s.CanHandover(a.Region), canDeliver = s.CanHandover(a.Region) && a.Status == "InStore", canLabel = s.CanPrintLabel(a.Region),
            needsComputerName = a.Type == "PC", ownerTypes = OwnerTypes, conditions = ReturnConditions.Select(x => new { code = x.Code, label = x.Label }),
            recommendations = Recommendations.Select(x => new { code = x.Code, label = x.Label }), deliveries = list
        };
    }

    /// <summary>The Handover page: delivery notes (waiting for the signed copy first, with the overdue ones), returns, and the
    /// infrastructure tasks.</summary>
    public async Task<object> Handover(ItamUser user, string? status, string? text, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.CanHandoverAny || s.ViewsAll || s.AdTasks || s.CucmTasks, "You cannot see handovers.");
        var st = status is "waiting" or "open" or "returned" or "signed" or "all" ? status : "waiting";
        var t = (text ?? "").Trim(); var like = "%" + t.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var rows = await Rows(c, null,
            "SELECT TOP 300 d.delivery_id,d.delivery_no,a.asset_id,a.asset_no,a.type_code,t.name_en,g.region_code,e.display_name,d.delivered_on,d.issued_at_utc,d.signed_at_utc,d.received_at_utc,d.computer_name,d.owner_type,ib.display_name " +
            "FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "JOIN itam_access.employee e ON e.employee_id=d.employee_id LEFT JOIN itam_access.employee ib ON ib.employee_id=d.issued_by " +
            "WHERE (@st=N'all' OR (@st=N'waiting' AND d.signed_file_id IS NULL AND d.received_at_utc IS NULL) OR (@st=N'open' AND d.received_at_utc IS NULL) OR (@st=N'returned' AND d.received_at_utc IS NOT NULL) OR (@st=N'signed' AND d.signed_file_id IS NOT NULL)) " +
            "AND (@q=N'' OR d.delivery_no LIKE @lk OR a.asset_no LIKE @lk OR e.display_name LIKE @lk OR d.computer_name LIKE @lk) ORDER BY d.delivery_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), assetId = r.GetInt32(2), assetNo = r.GetString(3), type = r.GetString(4), typeName = r.GetString(5), region = r.GetString(6), employee = r.GetString(7),
                deliveredOn = Day(r, 8), issuedAt = Utc(r, 9), signedAt = r.IsDBNull(10) ? (DateTime?)null : Utc(r, 10), receivedAt = r.IsDBNull(11) ? (DateTime?)null : Utc(r, 11), computerName = S(r, 12), ownerType = r.GetString(13), issuedBy = S(r, 14) },
            token, ("@st", st), ("@q", t), ("@lk", like));
        var visible = rows.Where(x => s.CanView(x.type, x.region)).Select(x => new { x.id, x.no, x.assetId, x.assetNo, x.typeName, x.region, x.employee, x.deliveredOn, x.issuedAt, x.signedAt, x.receivedAt, x.computerName, x.ownerType, x.issuedBy,
            state = x.receivedAt is not null ? "Returned" : x.signedAt is not null ? "Signed" : "Waiting for signed copy", overdue = x.signedAt is null && x.receivedAt is null && x.issuedAt < UtcNow.AddDays(-SignedCopyDays) }).ToList();
        var every = (await Rows(c, null, "SELECT a.type_code,g.region_code,d.signed_file_id,d.received_at_utc,d.issued_at_utc FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id",
            r => (Type: r.GetString(0), Region: r.GetString(1), Signed: !r.IsDBNull(2), Returned: !r.IsDBNull(3), Issued: Utc(r, 4)), token)).Where(x => s.CanView(x.Type, x.Region)).ToList();
        var counts = every.Where(x => !x.Returned).ToList();
        var tasks = await Tasks(c, s, token);
        return new
        {
            status = st, items = visible, signedCopyDays = SignedCopyDays, tasks,
            // The number on each filter button.
            filterCounts = new { waiting = counts.Count(x => !x.Signed), open = counts.Count, returned = every.Count(x => x.Returned), all = every.Count },
            counts = new { waiting = counts.Count(x => !x.Signed), overdue = counts.Count(x => !x.Signed && x.Issued < UtcNow.AddDays(-SignedCopyDays)), withEmployees = counts.Count, tasks = tasks.Count(x => x.done is null) }
        };
    }

    sealed record TaskView(int id, string kind, string subject, int assetId, string assetNo, DateTime created, DateTime? done, string? doneBy, string? note, bool canDo);
    /// <summary>The Tasks page: the System Administrator's computer removals (AD and Endpoint Central) and the Voice
    /// Administrator's extension removals (CUCM); the national IT roles see all of them read-only.</summary>
    public async Task<object> MyTasks(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.AdTasks || s.CucmTasks || s.ViewsAll, "You have no infrastructure tasks.");
        var tasks = await Tasks(c, s, token);
        return new { tasks, mine = new { ad = s.AdTasks, cucm = s.CucmTasks }, open = tasks.Count(t => t.canDo) };
    }

    /// <summary>Open tasks the person must do (the count on the Tasks menu item).</summary>
    internal static async Task<int> OpenTaskCount(DbConnection c, ItamScope s, CancellationToken token)
        => !s.AdTasks && !s.CucmTasks ? 0 : Convert.ToInt32(await Scalar(c, null,
            "SELECT COUNT(*) FROM itam_data.task WHERE done_at_utc IS NULL AND ((kind=N'RemoveFromAd' AND @ad=1) OR (kind=N'RemoveFromCucm' AND @cu=1))", token, ("@ad", s.AdTasks), ("@cu", s.CucmTasks)));

    async Task<List<TaskView>> Tasks(DbConnection c, ItamScope s, CancellationToken token)
    {
        var all = await Rows(c, null, "SELECT TOP 200 k.task_id,k.kind,k.subject,a.asset_id,a.asset_no,k.created_at_utc,k.done_at_utc,e.display_name,k.done_note FROM itam_data.task k JOIN itam_data.asset a ON a.asset_id=k.asset_id " +
            "LEFT JOIN itam_access.employee e ON e.employee_id=k.done_by ORDER BY CASE WHEN k.done_at_utc IS NULL THEN 0 ELSE 1 END,k.task_id DESC",
            r => new TaskView(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), Utc(r, 5), r.IsDBNull(6) ? null : Utc(r, 6), S(r, 7), S(r, 8), false), token);
        bool Mine(string kind) => kind == "RemoveFromAd" ? s.AdTasks : s.CucmTasks;
        return all.Where(x => Mine(x.kind) || s.ViewsAll).Select(x => x with { canDo = Mine(x.kind) && x.done is null }).ToList();
    }

    /// <summary>Confirm an infrastructure task done: System Administrator (AD and Endpoint Central), Voice Administrator (CUCM).</summary>
    public async Task TaskDone(ItamUser user, int taskId, ItamTaskDone x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var k = (await Rows(c, tx, "SELECT kind,asset_id,subject,done_at_utc FROM itam_data.task WITH (UPDLOCK) WHERE task_id=@i", r => (Kind: r.GetString(0), Asset: r.GetInt32(1), Subject: r.GetString(2), Done: !r.IsDBNull(3)), token, ("@i", taskId))).SingleOrDefault();
        if (k.Kind is null) throw new ArgumentException("The task was not found.");
        Require(k.Kind == "RemoveFromAd" ? s.AdTasks : s.CucmTasks, k.Kind == "RemoveFromAd" ? "Only the System Administrator confirms removal from AD and Endpoint Central." : "Only the Voice Administrator confirms removal from CUCM.");
        if (k.Done) throw new ArgumentException("This task is already done.");
        var note = Optional(x.Note, "Note", 500);
        await Exec(c, tx, "UPDATE itam_data.task SET done_by=@b,done_at_utc=SYSUTCDATETIME(),done_note=@n WHERE task_id=@i", token, ("@b", s.Id), ("@n", note), ("@i", taskId));
        await Event(c, tx, k.Asset, s.Id, "Task done", k.Subject + (note is null ? "" : ". " + note), token);
        await tx.CommitAsync(token);
    }

    // Documents --------------------------------------------------------------------------------------------------------------------------
    async Task<(DbConnection C, ItamScope S, DeliveryRow D)> DocAccess(ItamUser user, int deliveryId, CancellationToken token)
    {
        var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var d = await LoadDelivery(c, null, deliveryId, false, token);
        // IT of the asset; the employee who received it (My IT Assets); the employee's direct manager (My team).
        var own = d.EmployeeId == s.Id || await Scalar(c, null, "SELECT 1 FROM itam_access.employee WHERE employee_id=@e AND manager_id=@me", token, ("@e", d.EmployeeId), ("@me", s.Id)) is not null;
        if (!own && !s.CanView(d.Type, d.Region)) { await c.DisposeAsync(); throw new ItamAccessDenied("You cannot see this asset."); }
        return (c, s, d);
    }

    public async Task<ItamUpload> DeliveryNote(ItamUser user, int deliveryId, CancellationToken token)
    {
        var (c, _, d) = await DocAccess(user, deliveryId, token); await using var _c = c;
        return new($"Delivery note {d.No}.pdf", "application/pdf", NotePdf(NoteKind.Delivery, d, null, null));
    }

    public async Task<ItamUpload> ReceiptNote(ItamUser user, int deliveryId, CancellationToken token)
    {
        var (c, _, d) = await DocAccess(user, deliveryId, token); await using var _c = c;
        if (d.ReceivedAt is null) throw new ArgumentException("The asset is not received back yet.");
        var names = await Names(c, null, token);
        return new($"Receipt note {d.No}-R.pdf", "application/pdf", NotePdf(NoteKind.Receipt, d, names.GetValueOrDefault(d.ReceivedBy ?? Guid.Empty), null));
    }

    public async Task<ItamUpload> AssessmentNote(ItamUser user, int deliveryId, CancellationToken token)
    {
        var (c, _, d) = await DocAccess(user, deliveryId, token); await using var _c = c;
        var ta = (await Rows(c, null, "SELECT assessment_no,recommendation,review,assessed_by,assessed_at_utc FROM itam_data.assessment WHERE delivery_id=@d",
            r => (No: r.GetString(0), Rec: r.GetString(1), Review: r.GetString(2), By: r.GetGuid(3), At: Utc(r, 4)), token, ("@d", d.Id))).SingleOrDefault();
        if (ta.No is null) throw new ArgumentException("No technical assessment is recorded for this delivery.");
        var names = await Names(c, null, token);
        return new($"Technical assessment {ta.No}.pdf", "application/pdf", NotePdf(NoteKind.Assessment, d, names.GetValueOrDefault(ta.By), (ta.No, RecommendationLabel(ta.Rec), ta.Review, ta.At)));
    }

    /// <summary>The signed copy uploaded for a delivery, or the evidence of its technical assessment.</summary>
    public async Task<ItamUpload> DeliveryFile(ItamUser user, int deliveryId, string which, CancellationToken token)
    {
        var (c, _, d) = await DocAccess(user, deliveryId, token); await using var _c = c;
        long? id = which == "signed" ? d.SignedFile : await Scalar(c, null, "SELECT evidence_file_id FROM itam_data.assessment WHERE delivery_id=@d", token, ("@d", d.Id)) as long?;
        return id is long f ? await LoadFile(c, null, f, token) ?? throw new ArgumentException("The file was not found.") : throw new ArgumentException("No file is uploaded.");
    }

    enum NoteKind { Delivery, Receipt, Assessment }

    /// <summary>The bilingual note (layout of the SharePoint delivery note): title, date, document and Asset No., employee and
    /// product information as issued, then signatures. <paramref name="stamp"/> marks an unsigned note in the PO pack.</summary>
    byte[] NotePdf(NoteKind kind, DeliveryRow d, string? itPerson, (string No, string Rec, string Review, DateTime At)? ta, string? stamp = null)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var snap = JsonSerializer.Deserialize<NoteSnapshot>(d.Snapshot, Json)!;
        var doc = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W;
        const string muted = "0.35 0.35 0.35", ink = "0 0 0", soft = "0.93 0.93 0.93";
        var inv = CultureInfo.InvariantCulture;
        // Values sit in the middle, between the English and Arabic labels (owner, 22 October 2026).
        void Centre(string? text, double cx, double y, double size, double width, string color)
        {
            var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…";
            doc.Text(t, cx - pdf.Width(t, size) / 2, y, size, false, color);
        }
        var (titleEn, titleAr, docNo, date) = kind switch
        {
            NoteKind.Delivery => ("Delivery Note", "سند تسليم عهدة", d.No, d.On.ToString("dd/MM/yyyy", inv)),
            NoteKind.Receipt => ("Receipt Note", "سند استلام عهدة", d.No + "-R", d.ReceivedAt!.Value.AddHours(3).ToString("dd/MM/yyyy", inv)),
            _ => ("Technical Assessment", "تقييم فني", ta!.Value.No, ta.Value.At.AddHours(3).ToString("dd/MM/yyyy", inv))
        };
        doc.Logo(M, doc.Y + 10, 96); doc.Text("إدارة أصول تقنية المعلومات", W - M, doc.Y, 9, true, muted); doc.Text("IT Assets Management", W - M, doc.Y - 13, 9, true, muted); doc.Y -= 40;
        doc.Text(titleEn, M, doc.Y, 20, false, ink); doc.Text(titleAr, W - M, doc.Y, 18, true, ink); doc.Y -= 12;
        doc.Box(M, doc.Y, W - 2 * M, 2, "0 0 0"); doc.Y -= 18;
        if (stamp is not null) { doc.Box(M, doc.Y - 6, W - 2 * M, 22, "0.88 0.88 0.88"); doc.Text(stamp, M + 10, doc.Y, 11, false, "0 0 0"); doc.Text("لم يوقع بعد", W - M - 10, doc.Y, 11, true, "0 0 0"); doc.Y -= 28; }
        // Date, document number, Asset No.
        var cw = (W - 2 * M - 16) / 3;
        (string En, string Ar, string V)[] head = [("Date", "التاريخ", date), ("Document No.", "رقم المستند", docNo), ("Asset No.", "رقم المخزون", d.AssetNo)];
        for (var i = 0; i < 3; i++)
        {
            var x = M + i * (cw + 8);
            doc.Box(x, doc.Y - 30, cw, 44, soft); doc.Text(head[i].En, x + 8, doc.Y, 8.5, false, muted); doc.Text(head[i].Ar, x + cw - 8, doc.Y, 8.5, true, muted);
            Centre(head[i].V, x + cw / 2, doc.Y - 20, 12.5, cw - 16, ink);
        }
        doc.Y -= 52;
        void Section(string en, string ar, IEnumerable<NoteRow> rows)
        {
            doc.Box(M, doc.Y - 6, W - 2 * M, 20, "0.86 0.86 0.86"); doc.Text(en, M + 8, doc.Y, 10.5, false, "0 0 0"); doc.Text(ar, W - M - 8, doc.Y, 10.5, true, "0 0 0"); doc.Y -= 22;
            foreach (var r in rows)
            {
                doc.Text(r.En, M + 8, doc.Y, 9.5, false, muted); Centre(r.Value, W / 2, doc.Y, 10.5, 250, ink); doc.Text(r.Ar, W - M - 8, doc.Y, 9.5, true, muted);
                doc.Y -= 6; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 12;
            }
            doc.Y -= 8;
        }
        if (kind != NoteKind.Assessment || snap.Employee.Count > 0) Section("Employee Information", "بيانات الموظف", kind == NoteKind.Assessment ? snap.Employee.Where(r => r.En is "Employee Name" or "HR Code" or "Department" or "Branch Name") : snap.Employee);
        Section("Product Information", "معلومات المنتج", snap.Product);
        var extra = new List<NoteRow>();
        if (kind == NoteKind.Delivery) { extra.Add(new("Owner Type", "نوع العهدة", d.Owner)); extra.Add(new("Notes", "ملاحظات", d.Notes)); }
        if (kind == NoteKind.Receipt) { extra.Add(new("Delivery Note No.", "رقم سند التسليم", d.No)); extra.Add(new("Condition", "حالة الجهاز", d.Condition)); extra.Add(new("Received at", "مكان الاستلام", d.ReceiptLocationName)); extra.Add(new("Notes", "ملاحظات", d.ReceiptNotes)); }
        if (kind == NoteKind.Assessment) { extra.Add(new("Delivery Note No.", "رقم سند التسليم", d.No)); extra.Add(new("Recommendation", "التوصية", ta!.Value.Rec)); }
        if (extra.Any(r => !string.IsNullOrWhiteSpace(r.Value))) Section(kind == NoteKind.Delivery ? "Delivery Details" : kind == NoteKind.Receipt ? "Receipt Details" : "Assessment", kind == NoteKind.Delivery ? "تفاصيل التسليم" : kind == NoteKind.Receipt ? "تفاصيل الاستلام" : "التقييم", extra.Where(r => !string.IsNullOrWhiteSpace(r.Value)));
        if (kind == NoteKind.Assessment)
        {
            doc.Box(M, doc.Y - 6, W - 2 * M, 20, "0.86 0.86 0.86"); doc.Text("Technical Review", M + 8, doc.Y, 10.5, false, "0 0 0"); doc.Text("المراجعة الفنية", W - M - 8, doc.Y, 10.5, true, "0 0 0"); doc.Y -= 22;
            foreach (var line in Wrap(pdf, ta!.Value.Review, 10, W - 2 * M - 16)) { doc.Text(line, M + 8, doc.Y, 10, false, ink); doc.Y -= 14; if (doc.Y < M + 140) break; }
            doc.Y -= 8;
        }
        // Statement and signatures: the employee signs by hand himself (owner decision, 22 October 2026).
        var (stEn, stAr) = kind switch
        {
            NoteKind.Delivery => ("I received the device above in good condition and I am responsible for it.", "أقر باستلام الجهاز الموضح أعلاه بحالة جيدة وأتحمل مسؤوليته."),
            NoteKind.Receipt => ("IT received the device above back from the employee.", "استلمت إدارة تقنية المعلومات الجهاز الموضح أعلاه من الموظف."),
            _ => ("", "")
        };
        if (stEn.Length > 0) { doc.Text(stEn, M, doc.Y, 9.5, false, ink); doc.Y -= 14; doc.Text(stAr, W - M, doc.Y, 9.5, true, ink); doc.Y -= 24; }
        var sigY = Math.Min(doc.Y, M + 110);
        void Sign(double x, double w, string en, string ar, string? name)
        {
            doc.Text(en, x, sigY, 9.5, false, muted); doc.Text(ar, x + w, sigY, 9.5, true, muted);
            if (name is not null) doc.Fit(name, x, sigY - 18, 10, w, ink);
            doc.Line(x, sigY - 58, x + w, sigY - 58, "0.45 0.45 0.45"); doc.Text("Signature · التوقيع", x, sigY - 70, 8, false, muted);
        }
        var half = (W - 2 * M - 30) / 2;
        if (kind == NoteKind.Assessment) Sign(M, half, "IT Representative", "ممثل تقنية المعلومات", itPerson);
        else
        {
            Sign(M, half, "Employee Signature", "توقيع الموظف", snap.EmployeeName);
            Sign(M + half + 30, half, "IT Representative Signature", "توقيع ممثل تقنية المعلومات", kind == NoteKind.Receipt ? itPerson : snap.IssuedByName);
        }
        doc.Text($"IT Assets · {docNo}", M, M - 10, 7.5, false, muted); doc.Text("Printed from IT Assets", W - M, M - 10, 7.5, true, muted);
        return doc.Build();
    }

    static IEnumerable<string> Wrap(ItamPdf pdf, string text, double size, double width)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = line.Length == 0 ? word : line + " " + word;
                if (pdf.Width(next, size) > width && line.Length > 0) { yield return line; line = word; } else line = next;
            }
            yield return line;
        }
    }

    // Labels (form 35) -----------------------------------------------------------------------------------------------------------------
    /// <summary>What the label shows next to the Asset No.: service tag (computers), MAC address (IP phones), serial number
    /// (others), else the toner code or software name.</summary>
    static string LabelSn(HandAsset a) => a.Type switch { "PC" => a.Tag ?? a.Serial, "IP" => a.Mac, _ => a.Serial } ?? a.Attrs.GetValueOrDefault("TonerCode") ?? a.Attrs.GetValueOrDefault("Software") ?? "-";

    async Task<(HandAsset A, ItamScope S, DbConnection C)> LabelAccess(ItamUser user, int assetId, CancellationToken token)
    {
        var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var a = await LoadHandAsset(c, null, assetId, false, token);
        if (!s.CanPrintLabel(a.Region)) { await c.DisposeAsync(); throw new ItamAccessDenied("You cannot print labels for this asset."); }
        return (a, s, c);
    }

    async Task LabelPrinted(DbConnection c, ItamScope s, HandAsset a, string what, CancellationToken token)
    {
        await using var tx = await c.BeginTransactionAsync(token);
        await Event(c, tx, a.Id, s.Id, "Label", $"Label printed ({what})", token);
        await tx.CommitAsync(token);
    }

    /// <summary>The asset label as a PDF at real size (63.5 x 38.1 mm), printed on the Zebra through its Windows driver.</summary>
    public async Task<ItamUpload> LabelPdf(ItamUser user, int assetId, CancellationToken token)
    {
        var (a, s, c) = await LabelAccess(user, assetId, token); await using var _c = c;
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        const double mm = 72 / 25.4, Wd = 63.5 * mm, Ht = 38.1 * mm;
        var d = pdf.Start(Wd, Ht);
        using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode(LabelBaseUrl + a.No, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix; var qr = 24 * mm; var cell = qr / m.Count; double qx = 2 * mm, qy = Ht - 2.5 * mm - qr;
        for (var r = 0; r < m.Count; r++) for (var k = 0; k < m[r].Length; k++) if (m[r][k]) d.Box(qx + k * cell, qy + (m.Count - 1 - r) * cell, cell + 0.05, cell + 0.05, "0 0 0");
        d.Text(new Uri(LabelBaseUrl).Host, qx + qr / 2 - pdf.Width(new Uri(LabelBaseUrl).Host, 5) / 2, 2.2 * mm, 5, false, "0 0 0");
        double bx = 28 * mm, bw = Wd - bx - 2 * mm;
        d.Logo(bx + 0.5 * mm, Ht - 3 * mm, bw - 1 * mm);
        double by = 3.5 * mm, bh = 19 * mm, row = bh / 2, lw = 7 * mm;
        d.Line(bx, by, bx + bw, by, "0 0 0"); d.Line(bx, by + bh, bx + bw, by + bh, "0 0 0"); d.Line(bx, by + row, bx + bw, by + row, "0 0 0");
        d.Line(bx, by, bx, by + bh, "0 0 0"); d.Line(bx + bw, by, bx + bw, by + bh, "0 0 0"); d.Line(bx + lw, by, bx + lw, by + bh, "0 0 0");
        d.Text("AN.", bx + 1.2 * mm, by + row + 3.2 * mm, 8, false, "0 0 0"); d.Fit(a.No, bx + lw + 1.5 * mm, by + row + 2.8 * mm, 11.5, bw - lw - 3 * mm, "0 0 0");
        d.Text("SN.", bx + 1.2 * mm, by + 3.2 * mm, 8, false, "0 0 0"); d.Fit(LabelSn(a), bx + lw + 1.5 * mm, by + 3 * mm, 9.5, bw - lw - 3 * mm, "0 0 0");
        var bytes = d.Build();
        await LabelPrinted(c, s, a, "PDF", token);
        return new($"Label {a.No}.pdf", "application/pdf", bytes);
    }

    /// <summary>The asset label in ZPL for the Zebra (203 dpi, 63.5 x 38.1 mm; the logo stored once in the printer as
    /// E:ALTLOGO.GRF). Same layout as the PDF.</summary>
    public async Task<ItamUpload> LabelZpl(ItamUser user, int assetId, CancellationToken token)
    {
        var (a, s, c) = await LabelAccess(user, assetId, token); await using var _c = c;
        static string Z(string v) => new(v.Where(ch => ch is not ('^' or '~')).ToArray());
        var host = new Uri(LabelBaseUrl).Host + new Uri(LabelBaseUrl).AbsolutePath.TrimEnd('/').Replace("/a", "", StringComparison.Ordinal);
        var zpl = new StringBuilder()
            .Append("^XA^CI28^PW508^LL305^LH0,0\n")
            .Append($"^FO20,30^BQN,2,6^FDLA,{Z(LabelBaseUrl + a.No)}^FS\n")
            .Append($"^FO20,250^FB180,1,0,C^A0N,20,18^FD{Z(host)}^FS\n")
            .Append("^FO222,20^XGE:ALTLOGO.GRF,1,1^FS\n")
            .Append("^FO215,120^GB275,165,2^FS^FO215,202^GB275,0,2^FS^FO268,120^GB0,165,2^FS\n")
            .Append($"^FO222,146^A0N,30,28^FDAN.^FS^FO270,140^FB218,1,0,C^A0N,42,36^FD{Z(a.No)}^FS\n")
            .Append($"^FO222,228^A0N,30,28^FDSN.^FS^FO270,226^FB218,1,0,C^A0N,34,30^FD{Z(LabelSn(a))}^FS\n")
            .Append("^PQ1^XZ\n").ToString();
        await LabelPrinted(c, s, a, "Zebra ZPL", token);
        return new($"Label {a.No}.zpl", "text/plain", Encoding.UTF8.GetBytes(zpl));
    }
}
