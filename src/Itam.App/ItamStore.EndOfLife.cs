using System.Data;
using System.Data.Common;

namespace Itam.App;

public sealed record ItamDisposalInput(string? AssetNo, string? Reason, string? Condition);
public sealed record ItamDisposalCompletion(DateOnly? CompletedOn, string? Method, string? CompletedByName, string? Recipient, bool? DataWiped, bool? LicensesRemoved, string? Note);
public sealed record ItamSaleInput(string? AssetNo, Guid? BuyerId, decimal? Price, DateOnly? SoldOn, string? Notes);
public sealed record ItamResignInput(Guid? EmployeeId, DateOnly? LastWorkingDay, string? Note);

/// <summary>Disposals DS-n (forms 26 to 28), sales SL-n (form 21) and employee clearance CL-n for leavers (form 29, form 30 and the
/// Assets to collect page), plan v1.9.
/// Disposal: IT of the region (or the technical assessment's Dispose decision) asks; the asset is Pending disposal approval and
/// blocked; the IT Manager (or a backup on leave) approves or rejects, never their own request; approval alone never disposes;
/// the IT Assets Officer records the completion with its evidence (data wiped and licenses removed for computers), and only
/// then is the asset Disposed. Sale: any IT role, with the proof of payment, no approval; the buyer is the holder when the
/// asset is in someone's custody; the custody closes with reason Sale (no receipt note); the computer leaves AD and the IP
/// phone extension leaves CUCM. Clearance: the System Administrator marks the employee Resigned (every open delivery note
/// becomes Owner Type Resigned and appears on Assets to collect); the account stays active while assets are collected; the
/// clearance completes only when no custody and no unresolved loss remain; then the System Administrator disables the user
/// in AD and keeps it (task Waiting, then Done).</summary>
public sealed partial class ItamStore
{
    public static readonly (string Code, string Label)[] DisposalMethods = [("Recycling", "Recycling (e-waste company)"), ("Destruction", "Physical destruction"),
        ("ReturnToVendor", "Returned to the vendor"), ("Donation", "Donation"), ("Other", "Other")];
    static string DisposalStatusLabel(string s) => s switch { "WaitingApproval" => "Waiting approval", "Approved" => "Approved, awaiting completion", _ => s };
    /// <summary>Types that hold company data: the completion needs data wiping and license removal.</summary>
    static bool DataBearing(string type) => type == "PC";

    // Disposal ------------------------------------------------------------------------------------------------------------------------------
    public async Task<object> RequestDisposal(ItamUser user, ItamDisposalInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var a = await LoadEolAsset(c, tx, x.AssetNo, null, token);
        Require(s.DisposalRequest(a.Region), "Only IT of the asset's region asks for a disposal.");
        if (a.DeliveryId is not null) throw new ArgumentException($"{a.No} is in the custody of {a.HolderName}: receive it back first (receipt note and technical assessment).");
        if (a.Status is not ("InStore" or "PendingDisposal")) throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}: only an asset In Store is disposed.");
        if (await Busy(c, tx, a.Id, token) is string busy) throw new ArgumentException($"{a.No} cannot be disposed now: {busy}.");
        var assessment = await Scalar(c, tx, "SELECT TOP 1 ta.assessment_id FROM itam_data.assessment ta JOIN itam_data.delivery d ON d.delivery_id=ta.delivery_id WHERE d.asset_id=@a ORDER BY ta.assessment_id DESC", token, ("@a", a.Id)) as int?;
        var (id, no) = await NewDisposal(c, tx, a, assessment, Text(x.Reason, "Reason", 1000), Text(x.Condition, "Current condition", 500), s.Id, token);
        await MailDisposal(c, tx, id, "created", s.Id);
        await tx.CommitAsync(token);
        return new { id, disposalNo = no };
    }

    /// <summary>Open the disposal request (also from a technical assessment that decides Dispose); the asset is Pending disposal approval.</summary>
    static async Task<(int Id, string No)> NewDisposal(DbConnection c, DbTransaction tx, EolAsset a, int? assessmentId, string reason, string condition, Guid actor, CancellationToken token)
    {
        var no = $"DS-{await Next(c, tx, "Disposal", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.disposal(disposal_no,asset_id,assessment_id,reason,condition,previous_status,location_id,region_code,requested_by,status) " +
            "OUTPUT inserted.disposal_id VALUES(@n,@a,@t,@r,@c,N'InStore',@l,@g,@b,N'WaitingApproval')", token, ("@n", no), ("@a", a.Id), ("@t", assessmentId), ("@r", reason), ("@c", condition),
            ("@l", a.LocationId), ("@g", a.Region), ("@b", actor)));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'PendingDisposal' WHERE asset_id=@a", token, ("@a", a.Id));
        await Event(c, tx, a.Id, actor, "Disposal", $"{no} requested: {reason}; Pending disposal approval (no delivery, sale or transfer meanwhile)", token);
        await Audit(c, tx, actor, "disposal", no, "Request", null, new { asset = a.No, reason, condition, assessmentId }, null, token);
        return (id, no);
    }

    internal static async Task<int> DisposalFromAssessment(DbConnection c, DbTransaction tx, int assetId, int assessmentId, string assessmentNo, Guid actor, CancellationToken token)
        => (await NewDisposal(c, tx, await LoadEolAsset(c, tx, null, assetId, token), assessmentId, $"Technical assessment {assessmentNo}: dispose", "As assessed", actor, token)).Id;

    sealed record DisposalRow(int Id, string No, int AssetId, string AssetNo, string Type, string Status, Guid RequestedBy, string Previous, string Region);
    static async Task<DisposalRow> LoadDisposal(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
        => (await Rows(c, tx, $"SELECT x.disposal_id,x.disposal_no,x.asset_id,a.asset_no,a.type_code,x.status,x.requested_by,x.previous_status,x.region_code FROM itam_data.disposal x {(tx is null ? "" : "WITH (UPDLOCK)")} " +
            "JOIN itam_data.asset a ON a.asset_id=x.asset_id WHERE x.disposal_id=@i",
            r => new DisposalRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6), r.GetString(7), r.GetString(8)), token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The disposal was not found.");

    async Task DisposalStep(ItamUser user, int id, CancellationToken token, Func<DbConnection, DbTransaction, ItamScope, DisposalRow, Task> step)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        await step(c, tx, s, await LoadDisposal(c, tx, id, token));
        await tx.CommitAsync(token);
    }

    public Task DecideDisposal(ItamUser user, int id, ItamDecision x, CancellationToken token) => DisposalStep(user, id, token, async (c, tx, s, d) =>
    {
        Require(await ManagerDecides(c, tx, s, token, d.RequestedBy), "Only the IT Manager (or a backup on leave) decides a disposal, never the person who asked for it.");
        if (d.Status != "WaitingApproval") throw new ArgumentException($"{d.No} is {DisposalStatusLabel(d.Status)}.");
        var reason = x.Approve ? null : Text(x.Reason, "Reason for the rejection", 500);
        await Exec(c, tx, "UPDATE itam_data.disposal SET status=@s,decided_by=@b,decided_at_utc=SYSUTCDATETIME(),reject_reason=@r WHERE disposal_id=@i", token,
            ("@s", x.Approve ? "Approved" : "Rejected"), ("@b", s.Id), ("@r", reason), ("@i", id));
        if (!x.Approve) await Exec(c, tx, "UPDATE itam_data.asset SET status=@p WHERE asset_id=@a AND status=N'PendingDisposal'", token, ("@p", d.Previous), ("@a", d.AssetId));
        await Event(c, tx, d.AssetId, s.Id, "Disposal", x.Approve ? $"{d.No} approved; awaiting completion (the asset stays Pending disposal approval)" : $"{d.No} rejected: {reason}; back {ItamStatuses.Label(d.Previous)}", token);
        await Audit(c, tx, s.Id, "disposal", d.No, x.Approve ? "Approve" : "Reject", null, new { reason }, null, token);
        await MailDisposal(c, tx, d.Id, "decided", s.Id);
    });

    public async Task CompleteDisposal(ItamUser user, int id, ItamDisposalCompletion x, ItamUpload? evidence, CancellationToken token)
    {
        if (evidence is null) throw new ArgumentException("Attach the disposal evidence (handover or destruction certificate).");
        var type = CheckFile(evidence, pdfOnly: false);
        await DisposalStep(user, id, token, async (c, tx, s, d) =>
        {
            Require(s.DisposalComplete, "Only the IT Assets Officer or the IT Manager records the disposal completion.");
            if (d.Status != "Approved") throw new ArgumentException(d.Status == "WaitingApproval" ? $"{d.No} is not approved yet: approval comes before the completion." : $"{d.No} is {DisposalStatusLabel(d.Status)}.");
            var on = x.CompletedOn is DateOnly o && o <= DateOnly.FromDateTime(UtcNow.AddHours(3)) ? o : throw new ArgumentException("The disposal date: today or earlier.");
            var method = DisposalMethods.Any(m => m.Code == x.Method) ? x.Method! : throw new ArgumentException("Choose the disposal method.");
            var by = Text(x.CompletedByName, "Disposed by (person)", 200);
            var recipient = method is "Recycling" or "ReturnToVendor" or "Donation" ? Text(x.Recipient, "Recipient or vendor", 300) : Optional(x.Recipient, "Recipient or vendor", 300);
            if (DataBearing(d.Type) && (x.DataWiped != true || x.LicensesRemoved != true))
                throw new ArgumentException($"{d.AssetNo} holds company data: confirm the data was wiped and the company licenses removed.");
            var f = await SaveFile(c, tx, evidence, type, s.Id, token);
            await Exec(c, tx, "UPDATE itam_data.disposal SET status=N'Completed',completed_on=@o,method=@m,completed_by_name=@p,recipient=@r,evidence_file_id=@f,data_wiped=@w,licenses_removed=@l," +
                "completion_note=@n,completed_by=@b,completed_at_utc=SYSUTCDATETIME() WHERE disposal_id=@i", token, ("@o", DateParam(on)), ("@m", method), ("@p", by), ("@r", recipient), ("@f", f),
                ("@w", x.DataWiped == true), ("@l", x.LicensesRemoved == true), ("@n", Optional(x.Note, "Notes", 1000)), ("@b", s.Id), ("@i", id));
            await Exec(c, tx, "UPDATE itam_data.asset SET status=N'Disposed' WHERE asset_id=@a", token, ("@a", d.AssetId));
            await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'", token, ("@a", d.AssetId));
            await Event(c, tx, d.AssetId, s.Id, "Disposed", $"{d.No}: {DisposalMethods.First(m => m.Code == method).Label} on {on:dd/MM/yyyy} by {by}{(recipient is null ? "" : ", to " + recipient)}", token);
            await Audit(c, tx, s.Id, "disposal", d.No, "Complete", null, new { on, method, by, recipient, x.DataWiped, x.LicensesRemoved }, null, token);
            await MailDisposal(c, tx, d.Id, "completed", s.Id);
        });
    }

    public Task CancelDisposal(ItamUser user, int id, ItamCancelInput x, CancellationToken token) => DisposalStep(user, id, token, async (c, tx, s, d) =>
    {
        Require(s.Id == d.RequestedBy || s.DisposalComplete, "Only the person who asked, the IT Assets Officer or the IT Manager cancels a disposal.");
        if (d.Status is not ("WaitingApproval" or "Approved")) throw new ArgumentException($"{d.No} is {DisposalStatusLabel(d.Status)}.");
        var reason = Text(x.Reason, "Reason", 500);
        await Exec(c, tx, "UPDATE itam_data.disposal SET status=N'Cancelled',cancelled_reason=@r WHERE disposal_id=@i", token, ("@r", reason), ("@i", id));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=@p WHERE asset_id=@a AND status=N'PendingDisposal'", token, ("@p", d.Previous), ("@a", d.AssetId));
        await Event(c, tx, d.AssetId, s.Id, "Disposal", $"{d.No} cancelled: {reason}", token);
        await Audit(c, tx, s.Id, "disposal", d.No, "Cancel", null, new { reason }, null, token);
    });

    public async Task<object> Disposals(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.DisposalView, "You cannot see disposals.");
        var rows = await Rows(c, null, "SELECT x.disposal_id,x.disposal_no,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),g.name_en,x.region_code,x.reason,x.condition,x.status," +
            "rb.display_name,x.requested_at_utc,db.display_name,x.decided_at_utc,x.reject_reason,x.completed_on,x.method,x.completed_by_name,x.recipient,x.data_wiped,x.licenses_removed,ta.assessment_no,x.requested_by,a.type_code," +
            "CASE WHEN x.evidence_file_id IS NULL THEN 0 ELSE 1 END,x.cancelled_reason,a.asset_id FROM itam_data.disposal x JOIN itam_data.asset a ON a.asset_id=x.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "JOIN itam_ref.location g ON g.location_id=x.location_id LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_access.employee rb ON rb.employee_id=x.requested_by LEFT JOIN itam_access.employee db ON db.employee_id=x.decided_by LEFT JOIN itam_data.assessment ta ON ta.assessment_id=x.assessment_id " +
            "ORDER BY x.disposal_id DESC",
            r => (Region: r.GetString(7), By: r.GetGuid(23), Status: r.GetString(10), Row: new
            {
                id = r.GetInt32(0), no = r.GetString(1), assetNo = r.GetString(2), type = r.GetString(3), model = S(r, 4)?.Trim(), serial = S(r, 5), location = r.GetString(6), region = r.GetString(7),
                reason = r.GetString(8), condition = r.GetString(9), status = r.GetString(10), statusLabel = DisposalStatusLabel(r.GetString(10)), requestedBy = S(r, 11), requestedAt = Utc(r, 12),
                decidedBy = S(r, 13), decidedAt = r.IsDBNull(14) ? (DateTime?)null : Utc(r, 14), rejectReason = S(r, 15), completedOn = Day(r, 16),
                method = S(r, 17) is string m ? DisposalMethods.FirstOrDefault(x => x.Code == m).Label : null, completedBy = S(r, 18), recipient = S(r, 19),
                dataWiped = r.IsDBNull(20) ? (bool?)null : r.GetBoolean(20), licensesRemoved = r.IsDBNull(21) ? (bool?)null : r.GetBoolean(21), assessmentNo = S(r, 22),
                dataBearing = DataBearing(r.GetString(24)), evidence = r.GetInt32(25) == 1, cancelledReason = S(r, 26), assetId = r.GetInt32(27)
            }), token);
        var decider = await ManagerDecides(c, null, s, token);
        return new
        {
            disposals = rows.Where(x => s.ViewsAll || s.LossView(x.Region)).Select(x => new { x.Row, decide = decider && x.Status == "WaitingApproval" && x.By != s.Id,
                complete = s.DisposalComplete && x.Status == "Approved", cancel = (x.By == s.Id || s.DisposalComplete) && x.Status is "WaitingApproval" or "Approved" }),
            canRequest = s.CanHandoverAny, methods = DisposalMethods.Select(m => new { code = m.Code, label = m.Label })
        };
    }

    public async Task<ItamUpload> DisposalEvidence(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var d = await LoadDisposal(c, null, id, token);
        Require(s.ViewsAll || s.LossView(d.Region), "You cannot see this disposal.");
        return (await Rows(c, null, "SELECT f.file_name,f.media_type,f.content FROM itam_data.disposal x JOIN itam_data.stored_file f ON f.file_id=x.evidence_file_id WHERE x.disposal_id=@i",
            r => new ItamUpload(r.GetString(0), r.GetString(1), (byte[])r[2]), token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("No evidence is attached.");
    }

    internal static async Task<int> DisposalApprovalsCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        if (!await ManagerDecides(c, null, s, token)) return 0;
        return Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.disposal WHERE status=N'WaitingApproval' AND requested_by<>@me", token, ("@me", s.Id)));
    }

    // Sale -------------------------------------------------------------------------------------------------------------------------------------
    public async Task<object> RecordSale(ItamUser user, ItamSaleInput x, ItamUpload? proof, CancellationToken token)
    {
        if (proof is null) throw new ArgumentException("Attach the proof of payment (for example the receipt from Accounting).");
        var type = CheckFile(proof, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.SaleRecord, "Only IT records a sale.");
        var a = await LoadEolAsset(c, tx, x.AssetNo, null, token);
        if (a.Status is not ("InUse" or "InStore")) throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}: only an asset In Use or In Store is sold.");
        if (await Busy(c, tx, a.Id, token) is string busy) throw new ArgumentException($"{a.No} cannot be sold now: {busy}.");
        var buyer = x.BuyerId ?? throw new ArgumentException("Choose the buyer (the employee's username).");
        var buyerName = await Scalar(c, tx, "SELECT display_name FROM itam_access.employee WHERE employee_id=@e", token, ("@e", buyer)) as string ?? throw new ArgumentException("Choose the buyer.");
        if (a.Holder is Guid h && h != buyer) throw new ArgumentException($"{a.No} is in the custody of {a.HolderName}: only the holder buys it, or receive it back first.");
        var price = x.Price is decimal p && p >= 0 ? p : throw new ArgumentException("The sale price (SAR).");
        var on = x.SoldOn is DateOnly d && d <= DateOnly.FromDateTime(UtcNow.AddHours(3)) ? d : throw new ArgumentException("The sale date: today or earlier.");
        var notes = Optional(x.Notes, "Notes", 1000);
        var f = await SaveFile(c, tx, proof, type, s.Id, token);
        var no = $"SL-{await Next(c, tx, "Sale", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.sale(sale_no,asset_id,buyer_id,price,sold_on,proof_file_id,notes,delivery_id,previous_status,location_id,recorded_by) OUTPUT inserted.sale_id " +
            "VALUES(@n,@a,@e,@p,@o,@f,@t,@d,@s,@l,@b)", token, ("@n", no), ("@a", a.Id), ("@e", buyer), ("@p", price), ("@o", DateParam(on)), ("@f", f), ("@t", notes), ("@d", a.DeliveryId),
            ("@s", a.Status), ("@l", a.LocationId), ("@b", s.Id)));
        if (a.DeliveryId is int dl) await CloseCustody(c, tx, dl, s.Id, "Sale", $"Sold to the holder ({no})", token);
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'Sold' WHERE asset_id=@a", token, ("@a", a.Id));
        var computer = a.Computer ?? await Scalar(c, tx, "SELECT value FROM itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'", token, ("@a", a.Id)) as string;
        await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'", token, ("@a", a.Id));
        // Before the device leaves: the computer leaves AD and Endpoint Central (IT-EM-13); the IP phone extension leaves CUCM (IT-EM-23).
        if (a.Type == "PC")
            await Exec(c, tx, "INSERT itam_data.task(kind,asset_id,delivery_id,subject) VALUES(N'RemoveFromAd',@a,@d,@s)", token, ("@a", a.Id), ("@d", a.DeliveryId),
                ("@s", $"Sold ({no}): remove computer {computer ?? a.No} from Active Directory and Endpoint Central"));
        if (a.Type == "IP")
        {
            var ext = await Scalar(c, tx, "SELECT value FROM itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'Extension'", token, ("@a", a.Id)) as string;
            await Exec(c, tx, "INSERT itam_data.task(kind,asset_id,delivery_id,subject) VALUES(N'RemoveFromCucm',@a,@d,@s)", token, ("@a", a.Id), ("@d", a.DeliveryId),
                ("@s", $"Sold ({no}): remove {(ext is null ? "the extension" : "extension " + ext)} of IP phone {a.No} from CUCM"));
        }
        await Event(c, tx, a.Id, s.Id, "Sold", $"{no}: sold to {buyerName} for {Money(price)} on {on:dd/MM/yyyy}{(a.DeliveryId is null ? "" : "; custody closed by the sale (no receipt note)")}", token);
        await Audit(c, tx, s.Id, "sale", no, "Record", null, new { asset = a.No, buyer, price, on }, null, token);
        await MailSaleTasks(c, tx, a.Id, no, s.Id);
        await tx.CommitAsync(token);
        return new { id, saleNo = no };
    }

    public async Task<object> Sales(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.SalesView, "You cannot see sales.");
        var rows = await Rows(c, null, "SELECT x.sale_id,x.sale_no,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),e.display_name,e.account,x.price,x.sold_on,rb.display_name,x.recorded_at_utc,x.notes,d.delivery_no " +
            "FROM itam_data.sale x JOIN itam_data.asset a ON a.asset_id=x.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_access.employee e ON e.employee_id=x.buyer_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id LEFT JOIN itam_access.employee rb ON rb.employee_id=x.recorded_by " +
            "LEFT JOIN itam_data.delivery d ON d.delivery_id=x.delivery_id ORDER BY x.sold_on DESC,x.sale_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), assetNo = r.GetString(2), type = r.GetString(3), model = S(r, 4)?.Trim(), serial = S(r, 5), buyer = r.GetString(6), username = r.GetString(7),
                price = r.GetDecimal(8), soldOn = Day(r, 9), recordedBy = S(r, 10), recordedAt = Utc(r, 11), notes = S(r, 12), deliveryNo = S(r, 13) }, token);
        return new { sales = rows, total = rows.Sum(x => x.price), canRecord = s.SaleRecord };
    }

    public async Task<ItamUpload> SaleProof(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.SalesView, "You cannot see sales.");
        return (await Rows(c, null, "SELECT f.file_name,f.media_type,f.content FROM itam_data.sale x JOIN itam_data.stored_file f ON f.file_id=x.proof_file_id WHERE x.sale_id=@i",
            r => new ItamUpload(r.GetString(0), r.GetString(1), (byte[])r[2]), token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The sale was not found.");
    }

    // Leavers: clearance and Assets to collect ------------------------------------------------------------------------------------------------
    public async Task<object> MarkResigned(ItamUser user, ItamResignInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.MarkResigned, "Only the System Administrator marks an employee Resigned.");
        var who = x.EmployeeId ?? throw new ArgumentException("Choose the employee.");
        var name = await Scalar(c, tx, "SELECT display_name FROM itam_access.employee WHERE employee_id=@e", token, ("@e", who)) as string ?? throw new ArgumentException("Choose the employee.");
        if (who == s.Id) throw new ArgumentException("You cannot start your own clearance.");
        if (await Scalar(c, tx, "SELECT clearance_no FROM itam_data.clearance WHERE employee_id=@e AND status=N'InProgress'", token, ("@e", who)) is string open) throw new ArgumentException($"{name} already has clearance {open}.");
        var no = $"CL-{await Next(c, tx, "Clearance", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.clearance(clearance_no,employee_id,last_working_day,note,marked_by,status) OUTPUT inserted.clearance_id VALUES(@n,@e,@d,@t,@b,N'InProgress')", token,
            ("@n", no), ("@e", who), ("@d", x.LastWorkingDay is DateOnly d ? DateParam(d) : null), ("@t", Optional(x.Note, "Note", 1000)), ("@b", s.Id)));
        var open2 = await Rows(c, tx, "SELECT delivery_id,asset_id,delivery_no FROM itam_data.delivery WHERE employee_id=@e AND received_at_utc IS NULL", r => (D: r.GetInt32(0), A: r.GetInt32(1), No: r.GetString(2)), token, ("@e", who));
        await Exec(c, tx, "UPDATE itam_data.delivery SET owner_type_before=owner_type,owner_type=N'Resigned' WHERE employee_id=@e AND received_at_utc IS NULL AND owner_type<>N'Resigned'", token, ("@e", who));
        foreach (var o in open2) await Event(c, tx, o.A, s.Id, "Leaver", $"{no}: {name} resigned; {o.No} is Owner Type Resigned, to collect", token);
        await Audit(c, tx, s.Id, "clearance", no, "Mark resigned", null, new { employee = who, x.LastWorkingDay, assets = open2.Count }, null, token);
        await MailClearance(c, tx, id, "resigned", s.Id);
        await tx.CommitAsync(token);
        return new { id, clearanceNo = no, assets = open2.Count };
    }

    sealed record ClearanceRow(int Id, string No, Guid Employee, string Name, string Status, string Disable);
    static async Task<ClearanceRow> LoadClearance(DbConnection c, DbTransaction tx, int id, CancellationToken token)
        => (await Rows(c, tx, "SELECT k.clearance_id,k.clearance_no,k.employee_id,e.display_name,k.status,k.disable_status FROM itam_data.clearance k WITH (UPDLOCK) JOIN itam_access.employee e ON e.employee_id=k.employee_id WHERE k.clearance_id=@i",
            r => new ClearanceRow(r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5)), token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The clearance was not found.");

    async Task ClearanceStep(ItamUser user, int id, CancellationToken token, Func<DbConnection, DbTransaction, ItamScope, ClearanceRow, Task> step)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        await step(c, tx, s, await LoadClearance(c, tx, id, token));
        await tx.CommitAsync(token);
    }

    /// <summary>What still blocks the clearance: assets in custody, and loss cases whose responsibility is not cleared.</summary>
    static async Task<(int Custody, int Loss)> ClearanceBlockers(DbConnection c, DbTransaction? tx, Guid employee, CancellationToken token)
        => (Convert.ToInt32(await Scalar(c, tx, "SELECT COUNT(*) FROM itam_data.delivery WHERE employee_id=@e AND received_at_utc IS NULL", token, ("@e", employee))),
            Convert.ToInt32(await Scalar(c, tx, "SELECT COUNT(*) FROM itam_data.loss_case WHERE custodian_id=@e AND responsibility<>N'Cleared' AND status<>N'Recovered'", token, ("@e", employee))));

    public Task CompleteClearance(ItamUser user, int id, ItamLossText x, CancellationToken token) => ClearanceStep(user, id, token, async (c, tx, s, k) =>
    {
        Require(s.ClearanceComplete, "Only the System Administrator or the IT Manager completes a clearance.");
        if (k.Employee == s.Id) throw new ArgumentException("You cannot complete your own clearance.");
        if (k.Status != "InProgress") throw new ArgumentException($"{k.No} is {k.Status}.");
        var (custody, loss) = await ClearanceBlockers(c, tx, k.Employee, token);
        if (custody > 0 || loss > 0) throw new ArgumentException($"{k.No} cannot be completed: {(custody > 0 ? $"{custody} asset(s) still in {k.Name}'s custody (receive, sell or decide the loss)" : "")}" +
            $"{(custody > 0 && loss > 0 ? "; " : "")}{(loss > 0 ? $"{loss} loss case(s) without a cleared responsibility" : "")}.");
        var note = Text(x.Text, "Completion note (authority and evidence)", 1000);
        await Exec(c, tx, "UPDATE itam_data.clearance SET status=N'Completed',completed_by=@b,completed_at_utc=SYSUTCDATETIME(),completion_note=@n,disable_status=N'Waiting' WHERE clearance_id=@i", token,
            ("@b", s.Id), ("@n", note), ("@i", id));
        await Audit(c, tx, s.Id, "clearance", k.No, "Complete", null, new { note }, null, token);
        await MailClearance(c, tx, k.Id, "completed", s.Id);
    });

    public Task CancelClearance(ItamUser user, int id, ItamCancelInput x, CancellationToken token) => ClearanceStep(user, id, token, async (c, tx, s, k) =>
    {
        Require(s.MarkResigned, "Only the System Administrator cancels a clearance.");
        if (k.Status != "InProgress") throw new ArgumentException($"{k.No} is {k.Status}.");
        var reason = Text(x.Reason, "Reason (for example the resignation was withdrawn)", 500);
        await Exec(c, tx, "UPDATE itam_data.delivery SET owner_type=owner_type_before,owner_type_before=NULL WHERE employee_id=@e AND received_at_utc IS NULL AND owner_type=N'Resigned' AND owner_type_before IS NOT NULL", token, ("@e", k.Employee));
        await Exec(c, tx, "UPDATE itam_data.clearance SET status=N'Cancelled',cancelled_by=@b,cancelled_at_utc=SYSUTCDATETIME(),cancelled_reason=@r WHERE clearance_id=@i", token, ("@b", s.Id), ("@r", reason), ("@i", id));
        await Audit(c, tx, s.Id, "clearance", k.No, "Cancel", null, new { reason }, null, token);
    });

    /// <summary>After the clearance: the System Administrator disabled the user in AD and kept it (form 30).</summary>
    public Task UserDisabled(ItamUser user, int id, ItamLossText x, CancellationToken token) => ClearanceStep(user, id, token, async (c, tx, s, k) =>
    {
        Require(s.UserDisableTask, "Only the System Administrator confirms that the user is disabled in Active Directory.");
        if (k.Disable != "Waiting") throw new ArgumentException(k.Status == "Completed" ? $"{k.No}: already confirmed." : $"{k.No}: the clearance is not completed yet; the account stays active while assets are collected.");
        await Exec(c, tx, "UPDATE itam_data.clearance SET disable_status=N'Done',disabled_by=@b,disabled_at_utc=SYSUTCDATETIME(),disable_note=@n WHERE clearance_id=@i", token,
            ("@b", s.Id), ("@n", Optional(x.Text, "Note", 500)), ("@i", id));
        await Audit(c, tx, s.Id, "clearance", k.No, "User disabled in AD", null, null, null, token);
    });

    public async Task<object> Leavers(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.CollectView || s.MarkResigned, "You cannot see leavers.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        // Assets to collect: open delivery notes with Owner Type Resigned, in the person's regions.
        var collect = (await Rows(c, null, "SELECT d.delivery_id,d.delivery_no,d.delivered_on,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),d.computer_name," +
            "(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'Extension'),g.region_code,g.name_en,e.employee_id,e.display_name,e.account,e.job_title,e.department,e.branch,mg.display_name," +
            "k.clearance_no,k.marked_at_utc,mb.display_name,k.last_working_day,a.status,a.asset_id FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id JOIN itam_access.employee e ON e.employee_id=d.employee_id LEFT JOIN itam_access.employee mg ON mg.employee_id=e.manager_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_data.clearance k ON k.employee_id=d.employee_id AND k.status=N'InProgress' LEFT JOIN itam_access.employee mb ON mb.employee_id=k.marked_by " +
            "WHERE d.received_at_utc IS NULL AND d.owner_type=N'Resigned' ORDER BY e.display_name,a.asset_no",
            r => (Region: r.GetString(9), Row: new
            {
                deliveryId = r.GetInt32(0), deliveryNo = r.GetString(1), deliveredOn = Day(r, 2), assetNo = r.GetString(3), type = r.GetString(4), model = S(r, 5)?.Trim(), serial = S(r, 6),
                computerName = S(r, 7), extension = S(r, 8), region = r.GetString(9), keptAt = r.GetString(10), employeeId = r.GetGuid(11), employee = r.GetString(12), username = r.GetString(13),
                jobTitle = S(r, 14), department = S(r, 15), branch = S(r, 16), manager = S(r, 17), clearanceNo = S(r, 18), markedAt = r.IsDBNull(19) ? (DateTime?)null : Utc(r, 19), markedBy = S(r, 20),
                lastWorkingDay = Day(r, 21), daysPending = r.IsDBNull(19) ? (int?)null : (int)(UtcNow - Utc(r, 19)).TotalDays, pastLastDay = !r.IsDBNull(21) && Day(r, 21) < today,
                lost = r.GetString(22) == "Lost", assetId = r.GetInt32(23)
            }), token)).Where(x => s.CollectRegion(x.Region) || s.MarkResigned).Select(x => x.Row).ToList();
        var clearances = new List<object>();
        foreach (var k in await Rows(c, null, "SELECT k.clearance_id,k.clearance_no,e.employee_id,e.display_name,e.account,e.department,e.branch,e.region,k.last_working_day,k.status,mb.display_name,k.marked_at_utc," +
            "cb.display_name,k.completed_at_utc,k.completion_note,k.disable_status,db.display_name,k.disabled_at_utc,k.cancelled_reason,e.enabled FROM itam_data.clearance k JOIN itam_access.employee e ON e.employee_id=k.employee_id " +
            "LEFT JOIN itam_access.employee mb ON mb.employee_id=k.marked_by LEFT JOIN itam_access.employee cb ON cb.employee_id=k.completed_by LEFT JOIN itam_access.employee db ON db.employee_id=k.disabled_by ORDER BY k.clearance_id DESC",
            r => (Id: r.GetInt32(0), No: r.GetString(1), Emp: r.GetGuid(2), Name: r.GetString(3), Account: r.GetString(4), Dept: S(r, 5), Branch: S(r, 6), Region: S(r, 7), Last: Day(r, 8), Status: r.GetString(9),
                MarkedBy: S(r, 10), MarkedAt: Utc(r, 11), CompletedBy: S(r, 12), CompletedAt: r.IsDBNull(13) ? (DateTime?)null : Utc(r, 13), Note: S(r, 14), Disable: r.GetString(15), DisabledBy: S(r, 16),
                DisabledAt: r.IsDBNull(17) ? (DateTime?)null : Utc(r, 17), Cancelled: S(r, 18), Enabled: r.GetBoolean(19)), token))
        {
            var (custody, loss) = await ClearanceBlockers(c, null, k.Emp, token);
            clearances.Add(new
            {
                id = k.Id, no = k.No, employeeId = k.Emp, employee = k.Name, username = k.Account, department = k.Dept, branch = k.Branch, region = k.Region, lastWorkingDay = k.Last, status = k.Status,
                markedBy = k.MarkedBy, markedAt = k.MarkedAt, completedBy = k.CompletedBy, completedAt = k.CompletedAt, completionNote = k.Note, disable = k.Disable, disabledBy = k.DisabledBy, disabledAt = k.DisabledAt,
                cancelledReason = k.Cancelled, adEnabled = k.Enabled, custody, loss,
                can = new { complete = s.ClearanceComplete && k.Emp != s.Id && k.Status == "InProgress" && custody == 0 && loss == 0, cancel = s.MarkResigned && k.Status == "InProgress", disabled = s.UserDisableTask && k.Disable == "Waiting" }
            });
        }
        // Lost assets of leavers: kept apart from the physical collection (investigation and clearance queue).
        var lost = await Rows(c, null, "SELECT l.case_id,l.case_no,a.asset_no,e.display_name,l.status,l.responsibility FROM itam_data.loss_case l JOIN itam_data.asset a ON a.asset_id=l.asset_id " +
            "JOIN itam_access.employee e ON e.employee_id=l.custodian_id JOIN itam_data.clearance k ON k.employee_id=l.custodian_id AND k.status=N'InProgress' WHERE l.responsibility<>N'Cleared' AND l.status<>N'Recovered'",
            r => new { id = r.GetInt32(0), caseNo = r.GetString(1), assetNo = r.GetString(2), employee = r.GetString(3), status = LossStatusLabel(r.GetString(4)), responsibility = ResponsibilityLabel(r.GetString(5)) }, token);
        return new
        {
            collect, clearances, lost, canResign = s.MarkResigned,
            perRegion = collect.GroupBy(x => x.region).Select(g => new { region = g.Key, assets = g.Count(), employees = g.Select(x => x.employeeId).Distinct().Count() })
        };
    }

    internal static async Task<int> CollectCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        if (!s.CollectView) return 0;
        var regions = await Rows(c, null, "SELECT g.region_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "WHERE d.received_at_utc IS NULL AND d.owner_type=N'Resigned' AND a.status<>N'Lost'", r => r.GetString(0), token);
        return regions.Count(s.CollectRegion);
    }
}
