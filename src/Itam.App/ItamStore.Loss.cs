using System.Data;
using System.Data.Common;

namespace Itam.App;

public sealed record ItamLossInput(string? AssetNo, DateOnly? DiscoveredOn, string? LastLocation, string? Circumstances);
public sealed record ItamLossAssign(Guid? InvestigatorId);
public sealed record ItamLossText(string? Text);
public sealed record ItamLossFindings(string? Findings, string? Recommendation);
public sealed record ItamLossDecision(string? Decision, string? Note, decimal? Amount);
public sealed record ItamLossRecovery(DateOnly? RecoveredOn, int? LocationId, string? Condition, string? Note);

/// <summary>Loss cases LC-n (plan v1.9, forms 22 to 25). Reporting a loss marks the asset Lost; it never receives the asset back
/// and never clears the employee. The IT Manager assigns an investigator; security actions are recorded by the System
/// Administrator, the Team Leader or the IT Manager; the investigator records the findings and a recommendation; the
/// decision (Not liable, or Settlement with the amount) is recorded with the signed decision by the IT Manager (or a backup
/// on leave), never by the reporter, the custodian or the investigator. Only that decision (and, for a settlement, its
/// evidence) clears the employee and closes the custody (closure reason Loss decision, no receipt note). The financial
/// write-off is separate: requested by the IT Assets Officer or the IT Manager, approved by the IT Manager; it never clears
/// the employee. A recovered asset is received by IT, then reviewed before it returns to stock.</summary>
public sealed partial class ItamStore
{
    public static readonly (string Code, string Label)[] LossStatuses = [("Reported", "Reported"), ("UnderInvestigation", "Under investigation"),
        ("AwaitingDecision", "Awaiting decision"), ("AwaitingAction", "Awaiting settlement"), ("ClosedByDecision", "Closed by decision"),
        ("RecoveredReview", "Recovered, review required"), ("Recovered", "Recovered")];
    static string LossStatusLabel(string s) => LossStatuses.FirstOrDefault(x => x.Code == s).Label ?? s;
    static string ResponsibilityLabel(string s) => s switch { "PendingDecision" => "Pending decision", "PendingRequiredAction" => "Pending settlement", "Cleared" => "Cleared by decision", _ => s };
    static string WriteOffLabel(string s) => s switch { "NotRequested" => "Not requested", "Pending" => "Waiting approval", _ => s };
    internal const string OpenLossStatuses = "N'Reported',N'UnderInvestigation',N'AwaitingDecision',N'AwaitingAction',N'RecoveredReview'";

    /// <summary>The IT Manager, or the backup of an IT Manager on leave today; never one of the excluded people.</summary>
    static async Task<bool> ManagerDecides(DbConnection c, DbTransaction? tx, ItamScope s, CancellationToken token, params Guid?[] excluded)
        => !excluded.Contains(s.Id) && (s.Manager || await Scalar(c, tx, "SELECT TOP 1 1 FROM itam_access.leave_backup b JOIN itam_access.role_assignment r ON r.employee_id=b.person_id AND r.role_code=N'ItManager' " +
            "WHERE b.backup_employee_id=@me AND b.cancelled_at_utc IS NULL AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to", token, ("@me", s.Id)) is not null);

    sealed record EolAsset(int Id, string No, string Type, string Status, int LocationId, string Region, string Location, string? Model, string? Serial,
        int? DeliveryId, Guid? Holder, string? HolderName, string? DeliveryNo, string? Computer);
    static async Task<EolAsset> LoadEolAsset(DbConnection c, DbTransaction tx, string? assetNo, int? assetId, CancellationToken token)
    {
        var no = assetId is null ? Text(assetNo, "Asset No.", 20).ToUpperInvariant() : null;
        return (await Rows(c, tx, "SELECT a.asset_id,a.asset_no,a.type_code,a.status,a.location_id,g.region_code,g.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag)," +
            "d.delivery_id,d.employee_id,e.display_name,d.delivery_no,d.computer_name FROM itam_data.asset a WITH (UPDLOCK) JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_data.delivery d ON d.asset_id=a.asset_id AND d.received_at_utc IS NULL LEFT JOIN itam_access.employee e ON e.employee_id=d.employee_id " +
            "WHERE (@n IS NULL OR a.asset_no=@n) AND (@i IS NULL OR a.asset_id=@i)",
            r => new EolAsset(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), S(r, 7)?.Trim(), S(r, 8),
                I(r, 9), r.IsDBNull(10) ? null : r.GetGuid(10), S(r, 11), S(r, 12), S(r, 13)), token, ("@n", no), ("@i", assetId))).SingleOrDefault()
            ?? throw new ArgumentException($"{no ?? "The asset"} is not in IT Assets.");
    }

    /// <summary>Why the asset cannot start another end-of-life action now (an open movement, maintenance order, disposal or loss case), or null.</summary>
    static async Task<string?> Busy(DbConnection c, DbTransaction tx, int assetId, CancellationToken token)
        => await Scalar(c, tx, "SELECT TOP 1 x FROM (" +
            "SELECT N'it is on '+m.movement_no AS x FROM itam_data.movement_line l JOIN itam_data.movement m ON m.movement_id=l.movement_id WHERE l.asset_id=@a AND l.state IN(N'Pending',N'Dispatched',N'Missing',N'Damaged',N'Incorrect') " +
            "UNION ALL SELECT N'it has maintenance order '+order_no FROM itam_data.maintenance_order WHERE asset_id=@a AND status IN(N'WaitingApproval',N'Approved',N'WithSupplier') " +
            "UNION ALL SELECT N'it has disposal '+disposal_no FROM itam_data.disposal WHERE asset_id=@a AND status IN(N'WaitingApproval',N'Approved') " +
            $"UNION ALL SELECT N'it has loss case '+case_no FROM itam_data.loss_case WHERE asset_id=@a AND status IN({OpenLossStatuses})) q", token, ("@a", assetId)) as string;

    /// <summary>Close an open custody without a receipt note (sale, loss decision): the delivery keeps its history.</summary>
    static async Task CloseCustody(DbConnection c, DbTransaction tx, int deliveryId, Guid actor, string reason, string note, CancellationToken token)
        => await Exec(c, tx, "UPDATE itam_data.delivery SET received_at_utc=SYSUTCDATETIME(),received_by=@b,closure_reason=@r,receipt_notes=@n WHERE delivery_id=@d AND received_at_utc IS NULL", token,
            ("@b", actor), ("@r", reason), ("@n", note), ("@d", deliveryId));

    // Report -------------------------------------------------------------------------------------------------------------------------------
    public async Task<object> ReportLoss(ItamUser user, ItamLossInput x, ItamUpload? evidence, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var a = await LoadEolAsset(c, tx, x.AssetNo, null, token);
        Require(a.Holder == s.Id || s.LossReport(a.Region), "You report the loss of assets you hold, or (IT) of the assets of your regions.");
        if (a.Status is not ("InUse" or "InStore")) throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}: only an asset In Use or In Store is reported lost here.");
        if (await Busy(c, tx, a.Id, token) is string busy) throw new ArgumentException($"{a.No} cannot be reported lost here: {busy}.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var on = x.DiscoveredOn is DateOnly d && d <= today ? d : throw new ArgumentException("The date the loss was discovered: today or earlier.");
        var last = Text(x.LastLocation, "Last known location", 300);
        var what = Text(x.Circumstances, "What happened", 2000);
        long? file = evidence is null ? null : await SaveFile(c, tx, evidence, CheckFile(evidence, pdfOnly: false), s.Id, token);
        var (id, no) = await NewLossCase(c, tx, a, s.Id, on, last, what, file, null, token);
        await tx.CommitAsync(token);
        return new { id, caseNo = no };
    }

    /// <summary>Open the case and mark the asset Lost (also used when a transfer line is confirmed lost).</summary>
    static async Task<(int Id, string No)> NewLossCase(DbConnection c, DbTransaction tx, EolAsset a, Guid actor, DateOnly on, string last, string what, long? file, int? movementId, CancellationToken token)
    {
        var no = $"LC-{await Next(c, tx, "LossCase", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.loss_case(case_no,asset_id,delivery_id,custodian_id,movement_id,previous_status,region_code,reported_by,discovered_on,last_location,circumstances,report_file_id,status) " +
            "OUTPUT inserted.case_id VALUES(@n,@a,@d,@h,@m,@p,@r,@b,@o,@l,@w,@f,N'Reported')", token, ("@n", no), ("@a", a.Id), ("@d", a.DeliveryId), ("@h", a.Holder), ("@m", movementId), ("@p", a.Status),
            ("@r", a.Region), ("@b", actor), ("@o", DateParam(on)), ("@l", last), ("@w", what), ("@f", file)));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'Lost' WHERE asset_id=@a", token, ("@a", a.Id));
        await Event(c, tx, a.Id, actor, "Loss reported", $"{no}: {what} (last known location: {last}); responsibility pending decision{(a.DeliveryId is null ? "" : "; the custody stays open")}", token);
        await Audit(c, tx, actor, "loss", no, "Report", null, new { asset = a.No, on, last, movementId }, null, token);
        return (id, no);
    }

    sealed record LossRow(int Id, string No, int AssetId, string AssetNo, string Region, string Status, Guid ReportedBy, Guid? Custodian, int? DeliveryId, Guid? Investigator,
        string Responsibility, string WriteOff, Guid? WriteOffBy, decimal? Amount, string PreviousStatus);
    static async Task<LossRow> LoadLoss(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
        => (await Rows(c, tx, $"SELECT l.case_id,l.case_no,l.asset_id,a.asset_no,l.region_code,l.status,l.reported_by,l.custodian_id,l.delivery_id,l.investigator_id,l.responsibility,l.writeoff,l.writeoff_requested_by,l.settlement_amount,l.previous_status " +
            $"FROM itam_data.loss_case l {(tx is null ? "" : "WITH (UPDLOCK)")} JOIN itam_data.asset a ON a.asset_id=l.asset_id WHERE l.case_id=@i",
            r => new LossRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6), G0(r, 7), I(r, 8), G0(r, 9),
                r.GetString(10), r.GetString(11), G0(r, 12), r.IsDBNull(13) ? null : r.GetDecimal(13), r.GetString(14)), token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The loss case was not found.");

    async Task LossStep(ItamUser user, int id, CancellationToken token, Func<DbConnection, DbTransaction, ItamScope, LossRow, Task> step)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var l = await LoadLoss(c, tx, id, token);
        await step(c, tx, s, l);
        await tx.CommitAsync(token);
    }

    public Task AssignLoss(ItamUser user, int id, ItamLossAssign x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        Require(s.LossAssign, "Only the IT Manager assigns the investigator.");
        if (l.Status is not ("Reported" or "UnderInvestigation")) throw new ArgumentException($"{l.No} is {LossStatusLabel(l.Status)}.");
        var who = x.InvestigatorId ?? throw new ArgumentException("Choose the investigator.");
        if (who == l.Custodian) throw new ArgumentException("The employee who held the asset cannot investigate their own loss.");
        var name = await Scalar(c, tx, "SELECT display_name FROM itam_access.employee WHERE employee_id=@e AND enabled=1", token, ("@e", who)) as string ?? throw new ArgumentException("Choose an active employee.");
        await Exec(c, tx, "UPDATE itam_data.loss_case SET investigator_id=@v,assigned_by=@b,assigned_at_utc=SYSUTCDATETIME(),status=N'UnderInvestigation' WHERE case_id=@i", token, ("@v", who), ("@b", s.Id), ("@i", id));
        await Event(c, tx, l.AssetId, s.Id, "Loss investigation", $"{l.No}: investigator {name}", token);
        await Audit(c, tx, s.Id, "loss", l.No, "Assign investigator", null, new { investigator = who }, null, token);
    });

    public Task LossSecurity(ItamUser user, int id, ItamLossText x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        Require(s.LossSecurity, "Only the System Administrator, the IT Helpdesk Team Leader and the IT Manager record security actions.");
        if (l.Status is "Recovered") throw new ArgumentException($"{l.No} is closed.");
        var text = Text(x.Text, "Security actions (accounts, device management, licenses, data)", 1000);
        await Exec(c, tx, "UPDATE itam_data.loss_case SET security_actions=CONCAT(security_actions,CASE WHEN security_actions IS NULL THEN N'' ELSE NCHAR(10) END,@t),security_by=@b,security_at_utc=SYSUTCDATETIME() WHERE case_id=@i", token,
            ("@t", $"{UtcNow.AddHours(3):dd/MM/yyyy HH:mm} {s.Name}: {text}"), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "loss", l.No, "Security action", null, new { text }, null, token);
    });

    public async Task LossFindings(ItamUser user, int id, ItamLossFindings x, ItamUpload? file, CancellationToken token)
    {
        var type = file is null ? null : CheckFile(file, pdfOnly: false);
        await LossStep(user, id, token, async (c, tx, s, l) =>
        {
            Require(s.Id == l.Investigator, "Only the assigned investigator records the findings.");
            if (l.Status != "UnderInvestigation") throw new ArgumentException($"{l.No} is {LossStatusLabel(l.Status)}.");
            var findings = Text(x.Findings, "Findings (statements, evidence, what was found)", 4000);
            var rec = Text(x.Recommendation, "Recommendation", 2000);
            long? f = file is null ? null : await SaveFile(c, tx, file, type!, s.Id, token);
            await Exec(c, tx, "UPDATE itam_data.loss_case SET findings=@f,recommendation=@r,findings_file_id=@fi,findings_by=@b,findings_at_utc=SYSUTCDATETIME(),status=N'AwaitingDecision' WHERE case_id=@i", token,
                ("@f", findings), ("@r", rec), ("@fi", f), ("@b", s.Id), ("@i", id));
            await Event(c, tx, l.AssetId, s.Id, "Loss investigation", $"{l.No}: findings recorded; awaiting decision (the recommendation does not clear the employee)", token);
            await Audit(c, tx, s.Id, "loss", l.No, "Findings", null, new { rec }, null, token);
        });
    }

    public async Task DecideLoss(ItamUser user, int id, ItamLossDecision x, ItamUpload? signed, CancellationToken token)
    {
        if (signed is null) throw new ArgumentException("Attach the signed decision (PDF, JPEG or PNG).");
        var type = CheckFile(signed, pdfOnly: false);
        await LossStep(user, id, token, async (c, tx, s, l) =>
        {
            Require(await ManagerDecides(c, tx, s, token, l.ReportedBy, l.Custodian, l.Investigator),
                "Only the IT Manager (or a backup on leave) decides a loss; never the reporter, the employee who held the asset or the investigator.");
            if (l.Status != "AwaitingDecision") throw new ArgumentException($"{l.No} is {LossStatusLabel(l.Status)}: the decision follows the investigation findings.");
            var decision = x.Decision is "NotLiable" or "Settlement" ? x.Decision : throw new ArgumentException("Choose the decision: Not liable, or Settlement.");
            var note = Text(x.Note, "Rationale", 2000);
            decimal? amount = decision == "Settlement" ? (x.Amount is decimal m && m > 0 ? m : throw new ArgumentException("The settlement amount (SAR).")) : null;
            var f = await SaveFile(c, tx, signed, type, s.Id, token);
            await Exec(c, tx, "UPDATE itam_data.loss_case SET decision=@d,decision_note=@n,decision_file_id=@f,decided_by=@b,decided_at_utc=SYSUTCDATETIME(),settlement_amount=@m," +
                "responsibility=@r,status=@s WHERE case_id=@i", token, ("@d", decision), ("@n", note), ("@f", f), ("@b", s.Id), ("@m", amount),
                ("@r", decision == "NotLiable" ? "Cleared" : "PendingRequiredAction"), ("@s", decision == "NotLiable" ? "ClosedByDecision" : "AwaitingAction"), ("@i", id));
            if (decision == "NotLiable" && l.DeliveryId is int dl) await CloseCustody(c, tx, dl, s.Id, "LossDecision", $"Closed by loss decision {l.No}", token);
            await Event(c, tx, l.AssetId, s.Id, "Loss decision", decision == "NotLiable" ? $"{l.No}: not liable; responsibility cleared and the custody closed by the decision (no receipt note)"
                : $"{l.No}: settlement of {Money(amount!.Value)} required; responsibility pending the settlement", token);
            await Audit(c, tx, s.Id, "loss", l.No, "Decision", null, new { decision, amount, note }, null, token);
        });
    }

    public async Task SettleLoss(ItamUser user, int id, ItamLossText x, ItamUpload? evidence, CancellationToken token)
    {
        if (evidence is null) throw new ArgumentException("Attach the settlement evidence (for example the receipt from Accounting).");
        var type = CheckFile(evidence, pdfOnly: false);
        await LossStep(user, id, token, async (c, tx, s, l) =>
        {
            Require(s.WriteOffRequest, "Only the IT Assets Officer or the IT Manager records the settlement.");
            Require(s.Id != l.Custodian, "The employee who held the asset cannot record their own settlement.");
            if (l.Status != "AwaitingAction") throw new ArgumentException($"{l.No} is {LossStatusLabel(l.Status)}.");
            var note = Text(x.Text, "Settlement details", 1000);
            var f = await SaveFile(c, tx, evidence, type, s.Id, token);
            await Exec(c, tx, "UPDATE itam_data.loss_case SET settlement_file_id=@f,settlement_note=@n,settled_by=@b,settled_at_utc=SYSUTCDATETIME(),responsibility=N'Cleared',status=N'ClosedByDecision' WHERE case_id=@i", token,
                ("@f", f), ("@n", note), ("@b", s.Id), ("@i", id));
            if (l.DeliveryId is int dl) await CloseCustody(c, tx, dl, s.Id, "LossDecision", $"Closed by loss decision {l.No} after the settlement", token);
            await Event(c, tx, l.AssetId, s.Id, "Loss decision", $"{l.No}: settlement recorded; responsibility cleared and the custody closed (no receipt note)", token);
            await Audit(c, tx, s.Id, "loss", l.No, "Settlement", null, new { note }, null, token);
        });
    }

    public Task RequestWriteOff(ItamUser user, int id, ItamLossText x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        Require(s.WriteOffRequest, "Only the IT Assets Officer or the IT Manager requests a write-off.");
        if (l.Status is "Recovered" or "RecoveredReview") throw new ArgumentException($"{l.No}: the asset was recovered.");
        if (l.WriteOff is "Pending" or "Approved") throw new ArgumentException($"{l.No}: the write-off is already {WriteOffLabel(l.WriteOff).ToLowerInvariant()}.");
        var reason = Text(x.Text, "Reason for the write-off", 1000);
        await Exec(c, tx, "UPDATE itam_data.loss_case SET writeoff=N'Pending',writeoff_reason=@r,writeoff_requested_by=@b,writeoff_requested_at_utc=SYSUTCDATETIME(),writeoff_by=NULL,writeoff_at_utc=NULL,writeoff_note=NULL WHERE case_id=@i", token,
            ("@r", reason), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "loss", l.No, "Request write-off", null, new { reason }, null, token);
    });

    public Task DecideWriteOff(ItamUser user, int id, ItamDecision x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        Require(await ManagerDecides(c, tx, s, token, l.WriteOffBy, l.Custodian), "Only the IT Manager (or a backup on leave) approves a write-off, never the person who asked for it.");
        if (l.WriteOff != "Pending") throw new ArgumentException($"{l.No}: no write-off waits for approval.");
        var note = x.Approve ? Optional(x.Reason, "Note", 1000) : Text(x.Reason, "Reason for the rejection", 1000);
        await Exec(c, tx, "UPDATE itam_data.loss_case SET writeoff=@w,writeoff_by=@b,writeoff_at_utc=SYSUTCDATETIME(),writeoff_note=@n WHERE case_id=@i", token,
            ("@w", x.Approve ? "Approved" : "Rejected"), ("@b", s.Id), ("@n", note), ("@i", id));
        await Event(c, tx, l.AssetId, s.Id, "Write-off", $"{l.No}: write-off {(x.Approve ? "approved" : "rejected")} (the employee's responsibility is decided separately)", token);
        await Audit(c, tx, s.Id, "loss", l.No, x.Approve ? "Approve write-off" : "Reject write-off", null, new { note }, null, token);
    });

    /// <summary>The asset was found and IT physically received it: a receipt on the open custody (if any), then a review before reuse.</summary>
    public Task RecoverLoss(ItamUser user, int id, ItamLossRecovery x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        if (l.Status is "Recovered" or "RecoveredReview") throw new ArgumentException($"{l.No}: the recovery is already recorded.");
        var loc = (await Rows(c, tx, "SELECT region_code,kind FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Region: r.GetString(0), Kind: r.GetString(1)), token, ("@l", x.LocationId ?? 0))).SingleOrDefault();
        if (loc.Region is null) throw new ArgumentException("Choose the IT warehouse or branch where IT received the asset.");
        Require(s.CanHandover(loc.Region), $"IT receives recovered assets into their regions only ({string.Join(", ", s.Regions)}).");
        var on = x.RecoveredOn is DateOnly d && d <= DateOnly.FromDateTime(UtcNow.AddHours(3)) ? d : throw new ArgumentException("The date IT received the asset: today or earlier.");
        var condition = ReturnConditions.Any(r => r.Code == x.Condition) ? x.Condition! : throw new ArgumentException("Choose the condition (Working, Damaged or Faulty).");
        var note = Text(x.Note, "Where and how it was found", 1000);
        if (l.DeliveryId is int dl && await Scalar(c, tx, "SELECT 1 FROM itam_data.delivery WHERE delivery_id=@d AND received_at_utc IS NULL", token, ("@d", dl)) is not null)
            await Exec(c, tx, "UPDATE itam_data.delivery SET received_at_utc=@t,received_by=@b,receipt_condition=@c,receipt_location_id=@l,receipt_notes=@n WHERE delivery_id=@d", token,
                ("@t", on.ToDateTime(new TimeOnly(9, 0)).AddHours(-3)), ("@b", s.Id), ("@c", condition), ("@l", x.LocationId), ("@n", $"Recovered ({l.No}): {note}"), ("@d", dl));
        await Exec(c, tx, "UPDATE itam_data.asset SET location_id=@l,stock_location=@k WHERE asset_id=@a", token, ("@l", x.LocationId), ("@k", loc.Kind), ("@a", l.AssetId));
        await Exec(c, tx, "UPDATE itam_data.loss_case SET recovered_on=@o,recovered_by=@b,recovered_at_utc=SYSUTCDATETIME(),recovery_note=@n,recovery_location_id=@l,status=N'RecoveredReview' WHERE case_id=@i", token,
            ("@o", DateParam(on)), ("@b", s.Id), ("@n", $"{condition}: {note}"), ("@l", x.LocationId), ("@i", id));
        await Event(c, tx, l.AssetId, s.Id, "Lost asset recovered", $"{l.No}: received by IT ({condition}); still Lost until the review", token);
        await Audit(c, tx, s.Id, "loss", l.No, "Recovery", null, new { on, condition, x.LocationId }, null, token);
    });

    public Task ReviewRecovery(ItamUser user, int id, ItamLossText x, CancellationToken token) => LossStep(user, id, token, async (c, tx, s, l) =>
    {
        Require(s.WriteOffRequest, "Only the IT Assets Officer or the IT Manager reviews a recovered asset.");
        if (l.Status != "RecoveredReview") throw new ArgumentException($"{l.No} is {LossStatusLabel(l.Status)}.");
        var note = Text(x.Text, "Review (condition, earlier settlement or write-off)", 1000);
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InStore',device_condition=N'Used' WHERE asset_id=@a", token, ("@a", l.AssetId));
        await Exec(c, tx, "UPDATE itam_data.loss_case SET reviewed_by=@b,reviewed_at_utc=SYSUTCDATETIME(),status=N'Recovered',recovery_note=CONCAT(recovery_note,NCHAR(10),N'Review: ',@n) WHERE case_id=@i", token,
            ("@b", s.Id), ("@n", note), ("@i", id));
        await Event(c, tx, l.AssetId, s.Id, "Lost asset recovered", $"{l.No}: reviewed; back In Store ({note})", token);
        await Audit(c, tx, s.Id, "loss", l.No, "Review recovery", null, new { note }, null, token);
    });

    // Reading ----------------------------------------------------------------------------------------------------------------------------
    const string LossSelect = "SELECT l.case_id,l.case_no,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),l.region_code,l.status,l.responsibility,l.writeoff," +
        "ce.display_name,rb.display_name,l.reported_at_utc,l.discovered_on,l.last_location,l.circumstances,iv.display_name,l.findings,l.recommendation,l.decision,l.decision_note,db.display_name,l.decided_at_utc," +
        "l.settlement_amount,l.settlement_note,l.security_actions,l.writeoff_reason,l.writeoff_note,l.recovered_on,l.recovery_note,m.movement_no,a.asset_id,l.reported_by,l.custodian_id,l.investigator_id,dl.delivery_no," +
        "CASE WHEN l.report_file_id IS NULL THEN 0 ELSE 1 END+CASE WHEN l.findings_file_id IS NULL THEN 0 ELSE 2 END+CASE WHEN l.decision_file_id IS NULL THEN 0 ELSE 4 END+CASE WHEN l.settlement_file_id IS NULL THEN 0 ELSE 8 END " +
        "FROM itam_data.loss_case l JOIN itam_data.asset a ON a.asset_id=l.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
        "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
        "LEFT JOIN itam_access.employee ce ON ce.employee_id=l.custodian_id LEFT JOIN itam_access.employee rb ON rb.employee_id=l.reported_by LEFT JOIN itam_access.employee iv ON iv.employee_id=l.investigator_id " +
        "LEFT JOIN itam_access.employee db ON db.employee_id=l.decided_by LEFT JOIN itam_data.movement m ON m.movement_id=l.movement_id LEFT JOIN itam_data.delivery dl ON dl.delivery_id=l.delivery_id ";
    static object MapLoss(DbDataReader r) => new
    {
        id = r.GetInt32(0), caseNo = r.GetString(1), assetNo = r.GetString(2), type = r.GetString(3), model = S(r, 4)?.Trim(), serial = S(r, 5), region = r.GetString(6),
        status = r.GetString(7), statusLabel = LossStatusLabel(r.GetString(7)), responsibility = ResponsibilityLabel(r.GetString(8)), responsibilityCode = r.GetString(8),
        writeOff = WriteOffLabel(r.GetString(9)), writeOffCode = r.GetString(9), custodian = S(r, 10), reportedBy = S(r, 11), reportedAt = Utc(r, 12), discoveredOn = Day(r, 13),
        lastLocation = r.GetString(14), circumstances = r.GetString(15), investigator = S(r, 16), findings = S(r, 17), recommendation = S(r, 18), decision = S(r, 19) switch { "NotLiable" => "Not liable", "Settlement" => "Settlement", _ => null },
        decisionNote = S(r, 20), decidedBy = S(r, 21), decidedAt = r.IsDBNull(22) ? (DateTime?)null : Utc(r, 22), amount = r.IsDBNull(23) ? (decimal?)null : r.GetDecimal(23), settlementNote = S(r, 24),
        security = S(r, 25), writeOffReason = S(r, 26), writeOffNote = S(r, 27), recoveredOn = Day(r, 28), recoveryNote = S(r, 29), movementNo = S(r, 30), assetId = r.GetInt32(31),
        reportedById = r.GetGuid(32), custodianId = G0(r, 33), investigatorId = G0(r, 34), deliveryNo = S(r, 35), files = r.GetInt32(36)
    };

    static bool SeesLoss(ItamScope s, string region, Guid reportedBy, Guid? custodian, Guid? investigator)
        => s.LossView(region) || s.Id == reportedBy || s.Id == custodian || s.Id == investigator;

    public async Task<object> LossCases(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var rows = await Rows(c, null, LossSelect + "ORDER BY l.case_id DESC", r => (Row: MapLoss(r), Region: r.GetString(6), By: r.GetGuid(32), Cu: G0(r, 33), Iv: G0(r, 34)), token);
        var decider = await ManagerDecides(c, null, s, token);
        return new { cases = rows.Where(x => SeesLoss(s, x.Region, x.By, x.Cu, x.Iv)).Select(x => x.Row), canReport = s.LossViewAny, decider, statuses = LossStatuses.Select(x => new { code = x.Code, label = x.Label }) };
    }

    public async Task<object> LossCase(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var l = await LoadLoss(c, null, id, token);
        Require(SeesLoss(s, l.Region, l.ReportedBy, l.Custodian, l.Investigator), "You cannot see this loss case.");
        var row = (await Rows(c, null, LossSelect + "WHERE l.case_id=@i", MapLoss, token, ("@i", id))).Single();
        // The employee who held the asset sees the report and the outcome, not the investigation details.
        var full = s.LossView(l.Region) || s.Id == l.Investigator;
        var open = l.Status is not ("ClosedByDecision" or "Recovered");
        return new
        {
            @case = row, full,
            can = new
            {
                assign = s.LossAssign && l.Status is "Reported" or "UnderInvestigation", security = s.LossSecurity && l.Status != "Recovered",
                findings = s.Id == l.Investigator && l.Status == "UnderInvestigation",
                decide = l.Status == "AwaitingDecision" && await ManagerDecides(c, null, s, token, l.ReportedBy, l.Custodian, l.Investigator),
                settle = s.WriteOffRequest && s.Id != l.Custodian && l.Status == "AwaitingAction",
                writeOff = s.WriteOffRequest && l.WriteOff is "NotRequested" or "Rejected" && l.Status is not ("Recovered" or "RecoveredReview"),
                decideWriteOff = l.WriteOff == "Pending" && await ManagerDecides(c, null, s, token, l.WriteOffBy, l.Custodian),
                recover = s.CanHandoverAny && l.Status is not ("Recovered" or "RecoveredReview"), review = s.WriteOffRequest && l.Status == "RecoveredReview", open
            }
        };
    }

    public async Task<ItamUpload> LossFile(ItamUser user, int id, string which, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var l = await LoadLoss(c, null, id, token);
        Require(SeesLoss(s, l.Region, l.ReportedBy, l.Custodian, l.Investigator), "You cannot see this loss case.");
        var col = which switch { "report" => "report_file_id", "findings" => "findings_file_id", "decision" => "decision_file_id", "settlement" => "settlement_file_id", _ => throw new ArgumentException("Unknown file.") };
        if (which == "findings") Require(s.LossView(l.Region) || s.Id == l.Investigator, "You cannot see the investigation file.");
        var f = (await Rows(c, null, $"SELECT f.file_name,f.media_type,f.content FROM itam_data.loss_case l JOIN itam_data.stored_file f ON f.file_id=l.{col} WHERE l.case_id=@i",
            r => new ItamUpload(r.GetString(0), r.GetString(1), (byte[])r[2]), token, ("@i", id))).SingleOrDefault();
        return f ?? throw new ArgumentException("No file is attached.");
    }

    internal static async Task<int> LossActionsCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        var n = 0;
        foreach (var l in await Rows(c, null, $"SELECT case_id,status,reported_by,custodian_id,investigator_id,writeoff,writeoff_requested_by FROM itam_data.loss_case WHERE status IN({OpenLossStatuses}) OR writeoff=N'Pending'",
            r => (Id: r.GetInt32(0), Status: r.GetString(1), By: r.GetGuid(2), Cu: G0(r, 3), Iv: G0(r, 4), Wo: r.GetString(5), WoBy: G0(r, 6)), token))
        {
            if (l.Status == "Reported" && s.LossAssign) n++;
            else if (l.Status == "UnderInvestigation" && l.Iv == s.Id) n++;
            else if (l.Status == "AwaitingDecision" && await ManagerDecides(c, null, s, token, l.By, l.Cu, l.Iv)) n++;
            else if (l.Wo == "Pending" && await ManagerDecides(c, null, s, token, l.WoBy, l.Cu)) n++;
        }
        return n;
    }
}
