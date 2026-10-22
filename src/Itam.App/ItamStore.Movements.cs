using System.Data;
using System.Data.Common;
using System.Globalization;
using QRCoder;

namespace Itam.App;

public sealed record ItamMovementInput(string? Kind, Guid? EmployeeId, int[]? AssetIds, string? Purpose, string? PurposeDetails, string? PassType, int? FromLocationId,
    int? ToLocationId, string? Destination, DateOnly? ExitDate, DateOnly? ExpectedDate, string? CarriedBy, string? CarrierName, string? CarrierMobile,
    Guid? SenderId, Guid? ReceiverId, int? MaintenanceOrderId);
public sealed record ItamMovementDecision(bool Approve, string? Reason, DateOnly? ValidUntil);
public sealed record ItamMovementLines(int[]? LineIds);
public sealed record ItamReceiptLine(int LineId, string? Outcome, string? Note);
public sealed record ItamMovementReceipt(List<ItamReceiptLine>? Lines);
public sealed record ItamMovementResolve(int LineId, string? Resolution, string? Note);

/// <summary>Stock movements (plan v1.9 step 4): branch-to-branch transfers and other building exits on a gate pass GP-n
/// (approved by the Team Leader, System Administrator or IT Manager), and IT warehouse-to-warehouse transfers WT-n (no approval; issued pass). Security checks the printed
/// pass against the labels; the named sender (or IT) records the actual dispatch; the named receiver records each asset;
/// discrepancies are resolved per asset by an approver (or backup) with evidence. Stock moves only on actual dispatch and
/// receipt; dispatch validity never completes a dispatched movement.</summary>
public sealed partial class ItamStore
{
    public int PassValidityDays { get; init; } = 3;
    public static readonly string[] MovementPurposes = ["Move to another branch", "Customer or company site", "Exhibition or event", "Work from home", "Repair outside (maintenance order)", "Return to the supplier", "Other"];
    static string MovementStatusLabel(string s) => s switch
    {
        "Submitted" => "Submitted", "PartiallyDispatched" => "Partially dispatched", "InTransit" => "In transit", "Out" => "Out of the building", "PartiallyReceived" => "Partially received",
        "ReceiptDisputed" => "Discrepancy pending", "AwaitingCustody" => "Awaiting the signed delivery note", "ClosedWithExceptions" => "Closed with exceptions", _ => s
    };
    static readonly string[] OpenLineStates = ["Pending", "Dispatched", "Missing", "Damaged", "Incorrect"];

    sealed record MoveRow(int Id, string No, string Kind, string PassNo, string Status, Guid RequestedBy, Guid? Employee, Guid? Sender, Guid? Receiver, int From, int? To,
        string FromRegion, string? ToRegion, DateOnly ExitDate, DateOnly ValidUntil, DateOnly? Expected, string PassType);
    static async Task<MoveRow> LoadMove(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
        => (await Rows(c, tx, $"SELECT m.movement_id,m.movement_no,m.kind,m.pass_no,m.status,m.requested_by,m.employee_id,m.sender_id,m.receiver_id,m.from_location_id,m.to_location_id,f.region_code,t.region_code,m.exit_date,m.valid_until,m.expected_date,m.pass_type " +
            $"FROM itam_data.movement m {(tx is null ? "" : "WITH (UPDLOCK) ")}JOIN itam_ref.location f ON f.location_id=m.from_location_id LEFT JOIN itam_ref.location t ON t.location_id=m.to_location_id WHERE m.movement_id=@i",
            r => new MoveRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetGuid(5), G0(r, 6), G0(r, 7), G0(r, 8), r.GetInt32(9), I(r, 10), r.GetString(11), S(r, 12),
                Day(r, 13)!.Value, Day(r, 14)!.Value, Day(r, 15), r.GetString(16)), token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The movement was not found.");
    static Guid? G0(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    /// <summary>The IT Helpdesk Team Leader, System Administrator or IT Manager, or their backup during leave; never the person who asked.</summary>
    static async Task<bool> MovementApprover(DbConnection c, DbTransaction? tx, ItamScope s, Guid requestedBy, CancellationToken token)
        => s.Id != requestedBy && (s.MovementApproverRole || await Scalar(c, tx, "SELECT TOP 1 1 FROM itam_access.leave_backup b JOIN itam_access.role_assignment r ON r.employee_id=b.person_id AND r.role_code IN(N'ItManager',N'TeamLeader',N'SystemAdministrator') " +
            "WHERE b.backup_employee_id=@me AND b.cancelled_at_utc IS NULL AND b.person_id<>@q AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to", token, ("@me", s.Id), ("@q", requestedBy)) is not null);
    bool MovementIt(ItamScope s, MoveRow m) => s.CanHandover(m.FromRegion) || (m.ToRegion is string t && s.CanHandover(t));

    // Create ----------------------------------------------------------------------------------------------------------------------------
    public async Task<object> CreateMovement(ItamUser user, ItamMovementInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var kind = x.Kind is "Branch" or "Exit" or "Warehouse" ? x.Kind : throw new ArgumentException("Choose the movement: branch transfer, other exit or warehouse transfer.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var from = (await Rows(c, tx, "SELECT location_id,kind,region_code,name_en FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Id: r.GetInt32(0), Kind: r.GetString(1), Region: r.GetString(2), Name: r.GetString(3)), token, ("@l", x.FromLocationId ?? 0))).SingleOrDefault();
        if (from.Name is null) throw new ArgumentException(kind == "Warehouse" ? "Choose the source IT warehouse." : "Choose the branch the assets leave from.");
        var ids = (x.AssetIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Choose at least one asset.");
        if (ids.Length > 200) throw new ArgumentException("At most 200 assets on one movement; split it into two.");
        var exit = x.ExitDate ?? today; if (exit < today) throw new ArgumentException("The exit (dispatch) date: today or later.");
        string purpose, passType; int? to = null; string? toRegion = null, destination = null; Guid? employee = null, sender, receiver = null; DateOnly? expected = x.ExpectedDate;
        if (kind == "Warehouse")
        {
            Require(s.CanHandover(from.Region), "Only IT of the source region (or IT with all regions) creates a warehouse transfer.");
            if (from.Kind != "ITWarehouse") throw new ArgumentException("The source must be an IT warehouse.");
            var dest = (await Rows(c, tx, "SELECT location_id,kind,region_code FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Id: r.GetInt32(0), Kind: r.GetString(1), Region: r.GetString(2)), token, ("@l", x.ToLocationId ?? 0))).SingleOrDefault();
            if (dest.Kind != "ITWarehouse") throw new ArgumentException("The destination must be an IT warehouse.");
            if (dest.Id == from.Id) throw new ArgumentException("The source and destination warehouses must differ.");
            (to, toRegion) = (dest.Id, dest.Region);
            var rs = await Scope(c, tx, new ItamUser(x.ReceiverId ?? Guid.Empty, ""), token);
            if (!rs.CanHandover(dest.Region)) throw new ArgumentException("The destination receiver must be IT of the destination region.");
            receiver = x.ReceiverId; sender = s.Id; purpose = Text(x.Purpose, "Purpose", 60); passType = "NonReturnable";
            if (expected is null || expected < exit) throw new ArgumentException("The expected arrival: on or after the dispatch date.");
        }
        else
        {
            purpose = MovementPurposes.Contains(x.Purpose) ? x.Purpose! : throw new ArgumentException("Choose the purpose.");
            if (kind == "Branch" != (purpose == "Move to another branch")) throw new ArgumentException("A branch transfer has the purpose Move to another branch.");
            employee = x.EmployeeId ?? s.Id;
            if (kind == "Branch")
            {
                var dest = (await Rows(c, tx, "SELECT location_id,region_code FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Id: r.GetInt32(0), Region: r.GetString(1)), token, ("@l", x.ToLocationId ?? 0))).SingleOrDefault();
                if (dest.Region is null) throw new ArgumentException("Choose the destination branch.");
                if (dest.Id == from.Id) throw new ArgumentException("The destination branch must differ from the source.");
                (to, toRegion) = (dest.Id, dest.Region);
                if (await Scalar(c, tx, "SELECT 1 FROM itam_access.employee WHERE employee_id=@e AND enabled=1", token, ("@e", x.ReceiverId ?? Guid.Empty)) is null) throw new ArgumentException("Choose the named receiving employee (active).");
                receiver = x.ReceiverId; passType = "NonReturnable";
                if (expected is null || expected < exit) throw new ArgumentException("The expected receipt date: on or after the exit date.");
            }
            else
            {
                destination = Text(x.Destination, "Destination", 300);
                passType = x.PassType is "Returnable" or "NonReturnable" ? x.PassType : throw new ArgumentException("Choose the pass type.");
                if (passType == "NonReturnable") Require(s.CanHandoverAny, "Only IT staff issue non-returnable passes.");
                if (passType == "Returnable" && (expected is null || expected < exit)) throw new ArgumentException("The expected return date: on or after the exit date.");
                if (passType == "NonReturnable") expected = null;
            }
            sender = x.SenderId ?? employee;
            if (await Scalar(c, tx, "SELECT 1 FROM itam_access.employee WHERE employee_id=@e AND enabled=1", token, ("@e", sender ?? Guid.Empty)) is null) throw new ArgumentException("Choose the named sender (active).");
        }
        var details = Optional(x.PurposeDetails, "Purpose details", 1000);
        if (purpose == "Other" && details is null) throw new ArgumentException("Describe the purpose.");
        var carriedBy = x.CarriedBy is "Employee" or "Other" ? x.CarriedBy : "Employee";
        string? carrier = null, mobile = null;
        if (carriedBy == "Other" || kind == "Warehouse")
        {
            carrier = Text(x.CarrierName, "Carrier name", 200); mobile = Text(x.CarrierMobile, "Carrier mobile", 30);
            if (!System.Text.RegularExpressions.Regex.IsMatch(mobile, "^05[0-9]{8}$")) throw new ArgumentException("Carrier mobile: 05XXXXXXXX.");
        }
        int? mo = null;
        if (x.MaintenanceOrderId is int moId)
            mo = await Scalar(c, tx, "SELECT order_id FROM itam_data.maintenance_order WHERE order_id=@o", token, ("@o", moId)) as int? ?? throw new ArgumentException("The maintenance order was not found.");
        // The assets: who may move them, and that each is free to move.
        var lines = new List<(int Id, string No, string Status, Guid? Holder)>();
        foreach (var id in ids)
        {
            var a = (await Rows(c, tx, "SELECT a.asset_no,a.status,a.location_id,g.region_code,a.type_code,(SELECT TOP 1 employee_id FROM itam_data.delivery d WHERE d.asset_id=a.asset_id AND d.received_at_utc IS NULL) " +
                "FROM itam_data.asset a WITH (UPDLOCK) JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@a",
                r => (No: r.GetString(0), Status: r.GetString(1), Location: r.GetInt32(2), Region: r.GetString(3), Type: r.GetString(4), Holder: G0(r, 5)), token, ("@a", id))).SingleOrDefault();
            if (a.No is null) throw new ArgumentException("An asset was not found.");
            if (kind == "Warehouse")
            {
                if (a.Status != "InStore" || a.Location != from.Id) throw new ArgumentException($"{a.No} is not In Store at {from.Name}.");
                if (await Scalar(c, tx, "SELECT request_no FROM itam_data.request WHERE reserved_asset_id=@a AND status=N'StockAssigned'", token, ("@a", id)) is string pr) throw new ArgumentException($"{a.No} is reserved for {pr}.");
            }
            else
            {
                if (a.Status is not ("InUse" or "InStore")) throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}: it cannot leave on a gate pass.");
                var own = a.Holder == s.Id || (employee != s.Id && a.Holder == employee && s.CanHandover(a.Region));
                Require(own || s.CanHandover(a.Region), $"{a.No}: you can ask for assets in your custody, or as IT for assets of your regions.");
                if (employee == s.Id && !s.CanHandover(a.Region) && a.Holder != s.Id) throw new ArgumentException($"{a.No} is not in your custody.");
            }
            if (await Scalar(c, tx, "SELECT TOP 1 m.movement_no FROM itam_data.movement_line l JOIN itam_data.movement m ON m.movement_id=l.movement_id WHERE l.asset_id=@a AND l.state IN(N'Pending',N'Dispatched',N'Missing',N'Damaged',N'Incorrect')", token, ("@a", id)) is string open)
                throw new ArgumentException($"{a.No} is already on {open}.");
            lines.Add((id, a.No, a.Status, a.Holder));
        }
        var passNo = $"GP-{await Next(c, tx, "GatePass", token)}";
        var no = kind == "Warehouse" ? $"WT-{await Next(c, tx, "Transfer", token)}" : passNo;
        var status = kind == "Warehouse" ? "Submitted" : "Submitted";
        var mid = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.movement(movement_no,kind,pass_no,requested_by,employee_id,purpose,purpose_details,pass_type,from_location_id,to_location_id,destination,exit_date,valid_until,expected_date," +
            "carried_by,carrier_name,carrier_mobile,sender_id,receiver_id,maintenance_order_id,status) OUTPUT inserted.movement_id VALUES(@n,@k,@p,@b,@e,@pu,@pd,@pt,@f,@t,@d,@x,@v,@ex,@cb,@cn,@cm,@s,@r,@mo,@st)", token,
            ("@n", no), ("@k", kind), ("@p", passNo), ("@b", s.Id), ("@e", employee), ("@pu", purpose), ("@pd", details), ("@pt", passType), ("@f", from.Id), ("@t", to), ("@d", destination),
            ("@x", DateParam(exit)), ("@v", DateParam(exit.AddDays(PassValidityDays))), ("@ex", expected is DateOnly ed ? DateParam(ed) : null), ("@cb", carriedBy), ("@cn", carrier), ("@cm", mobile),
            ("@s", sender), ("@r", receiver), ("@mo", mo), ("@st", status)));
        foreach (var l in lines)
        {
            await Exec(c, tx, "INSERT itam_data.movement_line(movement_id,asset_id,previous_status,holder_id,state) VALUES(@m,@a,@s,@h,N'Pending')", token, ("@m", mid), ("@a", l.Id), ("@s", l.Status), ("@h", l.Holder));
            await Event(c, tx, l.Id, s.Id, kind == "Warehouse" ? "Warehouse transfer" : "Gate pass", kind == "Warehouse" ? $"{no} submitted (pass {passNo}): reserved for the transfer" : $"{no}: {purpose}, waiting for approval", token);
        }
        await Audit(c, tx, s.Id, "movement", no, "Create", null, new { kind, purpose, assets = lines.Select(l => l.No), from = from.Id, to, receiver, exit, expected }, null, token);
        await tx.CommitAsync(token);
        return new { id = mid, movementNo = no, passNo };
    }

    // Approve (branch and exit passes) ------------------------------------------------------------------------------------------------
    public async Task DecideMovement(ItamUser user, int id, ItamMovementDecision x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        if (m.Kind == "Warehouse") throw new ArgumentException("Warehouse transfers need no approval.");
        Require(await MovementApprover(c, tx, s, m.RequestedBy, token), "Only the IT Helpdesk Team Leader, the System Administrator or the IT Manager (or a backup during leave) decides gate passes; nobody approves their own.");
        if (m.Status != "Submitted") throw new ArgumentException($"{m.No} is {MovementStatusLabel(m.Status)}.");
        string? reason = null; var valid = m.ValidUntil;
        if (x.Approve) { if (x.ValidUntil is DateOnly v) { if (v < m.ExitDate || v > m.ValidUntil) throw new ArgumentException("Valid until: between the exit date and the default validity."); valid = v; } }
        else reason = Text(x.Reason, "Reason for the rejection", 500);
        await Exec(c, tx, "UPDATE itam_data.movement SET status=@st,decided_by=@b,decided_at_utc=SYSUTCDATETIME(),reject_reason=@r,valid_until=@v WHERE movement_id=@i", token,
            ("@st", x.Approve ? "Approved" : "Rejected"), ("@b", s.Id), ("@r", reason), ("@v", DateParam(valid)), ("@i", id));
        if (!x.Approve) await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Cancelled' WHERE movement_id=@i", token, ("@i", id));
        await Audit(c, tx, s.Id, "movement", m.No, x.Approve ? "Approve" : "Reject", null, new { reason, valid }, null, token);
        await tx.CommitAsync(token);
    }

    // Dispatch ------------------------------------------------------------------------------------------------------------------------
    public async Task DispatchMovement(ItamUser user, int id, ItamMovementLines x, CancellationToken token)
    {
        await ExpireMovements(token);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        if (m.Kind == "Warehouse")
        {
            Require(s.CanHandover(m.FromRegion), "Only IT of the source region records the dispatch.");
            if (m.Status is not ("Submitted" or "PartiallyDispatched")) throw new ArgumentException($"{m.No} is {MovementStatusLabel(m.Status)}.");
        }
        else
        {
            Require(s.Id == m.Sender || s.CanHandover(m.FromRegion), "Only the named sender, or IT of the source region, records the dispatch.");
            if (m.Status != "Approved") throw new ArgumentException($"{m.No} is {MovementStatusLabel(m.Status)}: only an approved pass is used.");
        }
        if (today < m.ExitDate || today > m.ValidUntil) throw new ArgumentException($"The pass is valid for dispatch from {m.ExitDate:dd/MM/yyyy} to {m.ValidUntil:dd/MM/yyyy}.");
        var pending = await Rows(c, tx, "SELECT line_id,asset_id FROM itam_data.movement_line WHERE movement_id=@i AND state=N'Pending'", r => (Line: r.GetInt32(0), Asset: r.GetInt32(1)), token, ("@i", id));
        var pick = m.Kind == "Warehouse" && x.LineIds is { Length: > 0 } ? pending.Where(p => x.LineIds.Contains(p.Line)).ToList() : pending;
        if (pick.Count == 0) throw new ArgumentException("Nothing left to dispatch.");
        foreach (var p in pick)
        {
            var exitOnly = m.Kind == "Exit";
            await Exec(c, tx, "UPDATE itam_data.movement_line SET state=@st,dispatched_at_utc=SYSUTCDATETIME() WHERE line_id=@l", token, ("@st", exitOnly && m.PassType == "NonReturnable" ? "Received" : "Dispatched"), ("@l", p.Line));
            if (!exitOnly) await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InTransit' WHERE asset_id=@a", token, ("@a", p.Asset));
            await Event(c, tx, p.Asset, s.Id, m.Kind == "Warehouse" ? "Warehouse transfer" : "Gate pass", exitOnly ? $"{m.No}: left the building" : $"{m.No}: dispatched (In Transit)", token);
        }
        var left = pending.Count - pick.Count;
        var status = m.Kind == "Exit" ? (m.PassType == "Returnable" ? "Out" : "Completed") : left > 0 ? "PartiallyDispatched" : "InTransit";
        await Exec(c, tx, $"UPDATE itam_data.movement SET status=@st,dispatched_at_utc=COALESCE(dispatched_at_utc,SYSUTCDATETIME()),dispatched_by=COALESCE(dispatched_by,@b){(status == "Completed" ? ",completed_at_utc=SYSUTCDATETIME()" : "")} WHERE movement_id=@i", token,
            ("@st", status), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "movement", m.No, "Dispatch", null, new { assets = pick.Count, left }, null, token);
        await tx.CommitAsync(token);
    }

    // Receipt (named receiver), per asset -----------------------------------------------------------------------------------------------
    public async Task ReceiveMovement(ItamUser user, int id, ItamMovementReceipt x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        if (m.Kind == "Exit") throw new ArgumentException("An exit pass has no destination receipt; confirm the return instead.");
        Require(s.Id == m.Receiver, "Only the named receiver confirms the receipt.");
        var items = x.Lines ?? [];
        if (items.Count == 0) throw new ArgumentException("Choose the assets received, or report what is missing or damaged.");
        foreach (var it in items)
        {
            var l = (await Rows(c, tx, "SELECT asset_id,state FROM itam_data.movement_line WITH (UPDLOCK) WHERE line_id=@l AND movement_id=@m", r => (Asset: r.GetInt32(0), State: r.GetString(1)), token, ("@l", it.LineId), ("@m", id))).SingleOrDefault();
            if (l.State != "Dispatched") throw new ArgumentException("Only dispatched assets are received or reported.");
            var outcome = it.Outcome is "Received" or "Missing" or "Damaged" or "Incorrect" ? it.Outcome : throw new ArgumentException("Choose received, missing, damaged or incorrect.");
            var note = Optional(it.Note, "Note", 500);
            if (outcome != "Received" && note is null) throw new ArgumentException("Describe what is missing, damaged or incorrect.");
            if (outcome == "Received") await Arrive(c, tx, s, m, it.LineId, "Received", note, token);
            else
            {
                await Exec(c, tx, "UPDATE itam_data.movement_line SET state=@st,condition_note=@n,issue_reported_by=@b,issue_reported_at_utc=SYSUTCDATETIME() WHERE line_id=@l", token, ("@st", outcome), ("@n", note), ("@b", s.Id), ("@l", it.LineId));
                await Event(c, tx, l.Asset, s.Id, "Transfer discrepancy", $"{m.No}: {outcome.ToLowerInvariant()} at the destination: {note}", token);
            }
        }
        await Recompute(c, tx, id, token);
        await Audit(c, tx, s.Id, "movement", m.No, "Receipt", null, items.Select(i => new { i.LineId, i.Outcome }), null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>An asset accepted at the destination: a warehouse asset is In Store there; on a branch transfer the asset's branch
    /// changes; when the receiver is not the person who had it, the old custody closes and the asset is In Store there, for IT to deliver.</summary>
    async Task Arrive(DbConnection c, DbTransaction tx, ItamScope s, MoveRow m, int lineId, string state, string? note, CancellationToken token)
    {
        var l = (await Rows(c, tx, "SELECT asset_id,previous_status,holder_id FROM itam_data.movement_line WHERE line_id=@l", r => (Asset: r.GetInt32(0), Previous: r.GetString(1), Holder: G0(r, 2)), token, ("@l", lineId))).Single();
        var kind = await Scalar(c, tx, "SELECT kind FROM itam_ref.location WHERE location_id=@l", token, ("@l", m.To)) as string ?? "Branch";
        int? newDelivery = null; var handOver = false;
        var open = (await Rows(c, tx, "SELECT delivery_id,employee_id,owner_type,computer_name FROM itam_data.delivery WHERE asset_id=@a AND received_at_utc IS NULL", r => (Id: r.GetInt32(0), Employee: r.GetGuid(1), Owner: r.GetString(2), Computer: S(r, 3)), token, ("@a", l.Asset))).SingleOrDefault();
        if (m.Kind == "Warehouse" || open.Id == 0)
            await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InStore',location_id=@t,stock_location=@k WHERE asset_id=@a", token, ("@t", m.To), ("@k", kind), ("@a", l.Asset));
        else if (open.Employee == m.Receiver)
            await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InUse',location_id=@t WHERE asset_id=@a", token, ("@t", m.To), ("@a", l.Asset));
        else
        {
            // Custody changes: the outgoing custody closes on this transfer (no receipt note: IT did not take it back) and the
            // asset is In Store at the destination. A delivery starts only from In Store (owner, 22 October 2026): IT of the
            // destination then delivers it to the receiver with a new delivery note, signed on paper.
            await Exec(c, tx, "UPDATE itam_data.delivery SET received_at_utc=SYSUTCDATETIME(),received_by=@b,receipt_location_id=@t,receipt_notes=@n,closed_by_movement_id=@m,closure_reason=N'Transfer' WHERE delivery_id=@d", token,
                ("@b", s.Id), ("@t", m.To), ("@n", $"Custody passed on {m.No}"), ("@m", m.Id), ("@d", open.Id));
            await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InStore',location_id=@t,stock_location=@k WHERE asset_id=@a", token, ("@t", m.To), ("@k", kind), ("@a", l.Asset));
            await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a AND attr_code=N'ComputerName'", token, ("@a", l.Asset));
            handOver = true;
        }
        await Exec(c, tx, "UPDATE itam_data.movement_line SET state=@st,received_at_utc=SYSUTCDATETIME(),received_by=@b,condition_note=COALESCE(@n,condition_note),new_delivery_id=@d WHERE line_id=@l", token,
            ("@st", state), ("@b", s.Id), ("@n", note), ("@d", newDelivery), ("@l", lineId));
        await Event(c, tx, l.Asset, s.Id, m.Kind == "Warehouse" ? "Warehouse transfer" : "Gate pass", $"{m.No}: received at the destination{(handOver ? "; the previous custody closed on the transfer; In Store, to deliver to the receiver" : "")}", token);
    }

    /// <summary>The movement status from its lines: outstanding (pending or dispatched), disputed, custody notes not yet signed.</summary>
    static async Task Recompute(DbConnection c, DbTransaction tx, int id, CancellationToken token)
    {
        var lines = await Rows(c, tx, "SELECT l.state,l.resolution,CASE WHEN d.delivery_id IS NOT NULL AND d.signed_file_id IS NULL THEN 1 ELSE 0 END FROM itam_data.movement_line l LEFT JOIN itam_data.delivery d ON d.delivery_id=l.new_delivery_id WHERE l.movement_id=@i",
            r => (State: r.GetString(0), Resolution: S(r, 1), Unsigned: r.GetInt32(2) == 1), token, ("@i", id));
        var m = (await Rows(c, tx, "SELECT kind,status FROM itam_data.movement WHERE movement_id=@i", r => (Kind: r.GetString(0), Status: r.GetString(1)), token, ("@i", id))).Single();
        if (m.Status is "Submitted" or "Approved" or "Rejected" or "Cancelled" or "Expired" or "Out") return;
        var live = lines.Where(l => l.State != "Cancelled").ToList();
        string status;
        if (live.Any(l => l.State is "Missing" or "Damaged" or "Incorrect")) status = "ReceiptDisputed";
        else if (live.Any(l => l.State == "Pending")) status = "PartiallyDispatched";
        else if (live.Any(l => l.State == "Dispatched")) status = live.Any(l => l.State != "Dispatched") ? "PartiallyReceived" : "InTransit";
        else if (live.Any(l => l.Unsigned)) status = "AwaitingCustody";
        else status = live.Any(l => l.Resolution is not null and not "ArrivedLater") ? "ClosedWithExceptions" : "Completed";
        await Exec(c, tx, $"UPDATE itam_data.movement SET status=@s{(status is "Completed" or "ClosedWithExceptions" ? ",completed_at_utc=COALESCE(completed_at_utc,SYSUTCDATETIME())" : "")} WHERE movement_id=@i", token, ("@s", status), ("@i", id));
    }

    // Discrepancy resolution (approvers or backup), per asset, with evidence ---------------------------------------------------------
    public async Task ResolveMovement(ItamUser user, int id, ItamMovementResolve x, ItamUpload? evidence, CancellationToken token)
    {
        if (evidence is null) throw new ArgumentException("Attach the evidence of the resolution.");
        var type = CheckFile(evidence, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        Require(await MovementApprover(c, tx, s, Guid.Empty, token), "Only the IT Helpdesk Team Leader, the System Administrator or the IT Manager (or a backup during leave) resolves discrepancies.");
        var l = (await Rows(c, tx, "SELECT asset_id,state,previous_status FROM itam_data.movement_line WITH (UPDLOCK) WHERE line_id=@l AND movement_id=@m", r => (Asset: r.GetInt32(0), State: r.GetString(1), Previous: r.GetString(2)), token, ("@l", x.LineId), ("@m", id))).SingleOrDefault();
        if (l.State is not ("Missing" or "Damaged" or "Incorrect" or "Dispatched")) throw new ArgumentException("Only a missing, damaged, incorrect or overdue asset is resolved.");
        var res = x.Resolution is "ArrivedLater" or "NeverDispatched" or "ReturnedToSource" or "ConfirmedLost" or "AcceptedAtDestination" ? x.Resolution : throw new ArgumentException("Choose the resolution.");
        var note = Text(x.Note, "Investigation findings", 1000);
        var file = await SaveFile(c, tx, evidence, type, s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.movement_line SET resolution=@r,resolution_note=@n,resolved_by=@b,resolved_at_utc=SYSUTCDATETIME(),evidence_file_id=@f WHERE line_id=@l", token,
            ("@r", res), ("@n", note), ("@b", s.Id), ("@f", file), ("@l", x.LineId));
        switch (res)
        {
            case "ArrivedLater": case "AcceptedAtDestination": await Arrive(c, tx, s, m, x.LineId, "Received", null, token); break;
            case "NeverDispatched": case "ReturnedToSource":
                await Exec(c, tx, "UPDATE itam_data.asset SET status=@p WHERE asset_id=@a", token, ("@p", l.Previous), ("@a", l.Asset));
                await Exec(c, tx, "UPDATE itam_data.movement_line SET state=@s WHERE line_id=@l", token, ("@s", res == "NeverDispatched" ? "NeverDispatched" : "Returned"), ("@l", x.LineId)); break;
            case "ConfirmedLost":
                // The asset is Lost and a loss case opens: the transfer can close while the case (responsibility, write-off) stays open.
                await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Lost' WHERE line_id=@l", token, ("@l", x.LineId));
                await NewLossCase(c, tx, await LoadEolAsset(c, tx, null, l.Asset, token), s.Id, DateOnly.FromDateTime(UtcNow.AddHours(3)), $"In transit on {m.No}", $"Confirmed lost on {m.No}: {note}", file, m.Id, token); break;
        }
        await Event(c, tx, l.Asset, s.Id, "Transfer discrepancy", $"{m.No}: resolved ({res}): {note}", token);
        await Recompute(c, tx, id, token);
        await Audit(c, tx, s.Id, "movement", m.No, "Resolve", null, new { x.LineId, res }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>A returnable exit: the assets came back (the employee, the requester or IT of the source region confirms).</summary>
    public async Task ReturnMovement(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        if (m.Kind != "Exit" || m.Status != "Out") throw new ArgumentException($"{m.No} is not out on a returnable pass.");
        Require(s.Id == m.Employee || s.Id == m.RequestedBy || s.Id == m.Sender || s.CanHandover(m.FromRegion), "Only the employee, the requester or IT confirms the return.");
        await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Returned',received_at_utc=SYSUTCDATETIME(),received_by=@b WHERE movement_id=@i AND state=N'Dispatched'", token, ("@b", s.Id), ("@i", id));
        await Exec(c, tx, "UPDATE itam_data.movement SET status=N'Completed',completed_at_utc=SYSUTCDATETIME() WHERE movement_id=@i", token, ("@i", id));
        foreach (var a in await Rows(c, tx, "SELECT asset_id FROM itam_data.movement_line WHERE movement_id=@i", r => r.GetInt32(0), token, ("@i", id)))
            await Event(c, tx, a, s.Id, "Gate pass", $"{m.No}: back in the building", token);
        await Audit(c, tx, s.Id, "movement", m.No, "Return", null, null, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>Cancel before dispatch (all assets released); a warehouse transfer partly dispatched cancels its undispatched assets only.</summary>
    public async Task CancelMovement(ItamUser user, int id, ItamCancelInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = await LoadMove(c, tx, id, token);
        Require(s.Id == m.RequestedBy || s.CanHandover(m.FromRegion), "Only the requester or IT of the source region cancels.");
        var reason = Text(x.Reason, "Reason", 500);
        if (m.Status is "Submitted" or "Approved")
        {
            await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Cancelled' WHERE movement_id=@i AND state=N'Pending'", token, ("@i", id));
            await Exec(c, tx, "UPDATE itam_data.movement SET status=N'Cancelled',cancelled_reason=@r WHERE movement_id=@i", token, ("@r", reason), ("@i", id));
        }
        else if (m.Status == "PartiallyDispatched")
        {
            await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Cancelled' WHERE movement_id=@i AND state=N'Pending'", token, ("@i", id));
            await Exec(c, tx, "UPDATE itam_data.movement SET cancelled_reason=@r WHERE movement_id=@i", token, ("@r", reason), ("@i", id));
            await Recompute(c, tx, id, token);
        }
        else throw new ArgumentException($"{m.No} is {MovementStatusLabel(m.Status)}: after dispatch the movement stays open until each asset is received or resolved.");
        await Audit(c, tx, s.Id, "movement", m.No, "Cancel", null, new { reason }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>An approved pass not dispatched by its valid-until date expires (the assets are released); it must be reissued.</summary>
    public async Task<int> ExpireMovements(CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var due = await Rows(c, tx, "SELECT movement_id,movement_no FROM itam_data.movement WITH (UPDLOCK) WHERE status IN(N'Approved',N'Submitted') AND valid_until<@d AND dispatched_at_utc IS NULL", r => (Id: r.GetInt32(0), No: r.GetString(1)), token, ("@d", DateParam(today)));
        foreach (var d in due)
        {
            await Exec(c, tx, "UPDATE itam_data.movement_line SET state=N'Cancelled' WHERE movement_id=@i AND state=N'Pending'", token, ("@i", d.Id));
            await Exec(c, tx, "UPDATE itam_data.movement SET status=N'Expired' WHERE movement_id=@i", token, ("@i", d.Id));
            await Audit(c, tx, Guid.Empty, "movement", d.No, "Expired", null, null, "Not dispatched within the validity", token);
        }
        await tx.CommitAsync(token);
        return due.Count;
    }

    // Reading -----------------------------------------------------------------------------------------------------------------------------
    const string MoveSelect = "SELECT m.movement_id,m.movement_no,m.kind,m.pass_no,m.pass_version,m.status,m.purpose,m.purpose_details,m.pass_type,f.name_en,t.name_en,m.destination,m.exit_date,m.valid_until,m.expected_date," +
        "m.carried_by,m.carrier_name,m.carrier_mobile,rq.display_name,em.display_name,sn.display_name,rc.display_name,db.display_name,m.decided_at_utc,m.reject_reason,m.cancelled_reason,m.dispatched_at_utc,m.completed_at_utc," +
        "m.requested_at_utc,m.requested_by,m.employee_id,m.sender_id,m.receiver_id,f.region_code,t.region_code,(SELECT COUNT(*) FROM itam_data.movement_line l WHERE l.movement_id=m.movement_id AND l.state<>N'Cancelled'),mo.order_no " +
        "FROM itam_data.movement m JOIN itam_ref.location f ON f.location_id=m.from_location_id LEFT JOIN itam_ref.location t ON t.location_id=m.to_location_id LEFT JOIN itam_access.employee rq ON rq.employee_id=m.requested_by " +
        "LEFT JOIN itam_access.employee em ON em.employee_id=m.employee_id LEFT JOIN itam_access.employee sn ON sn.employee_id=m.sender_id LEFT JOIN itam_access.employee rc ON rc.employee_id=m.receiver_id " +
        "LEFT JOIN itam_access.employee db ON db.employee_id=m.decided_by LEFT JOIN itam_data.maintenance_order mo ON mo.order_id=m.maintenance_order_id ";
    object MapMove(DbDataReader r)
    {
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var status = r.GetString(5); var expected = Day(r, 14);
        DateTime? T(int i) => r.IsDBNull(i) ? null : Utc(r, i);
        return new
        {
            id = r.GetInt32(0), no = r.GetString(1), kind = r.GetString(2), kindLabel = r.GetString(2) switch { "Branch" => "Branch transfer", "Warehouse" => "Warehouse transfer", _ => "Exit pass" },
            passNo = r.GetString(3), passVersion = r.GetInt32(4), status, statusLabel = MovementStatusLabel(status), purpose = r.GetString(6), purposeDetails = S(r, 7), passType = r.GetString(8),
            from = r.GetString(9), to = S(r, 10), destination = S(r, 11), exitDate = Day(r, 12), validUntil = Day(r, 13), expectedDate = expected, carriedBy = r.GetString(15), carrierName = S(r, 16), carrierMobile = S(r, 17),
            requestedBy = S(r, 18), employee = S(r, 19), sender = S(r, 20), receiver = S(r, 21), decidedBy = S(r, 22), decidedAt = T(23), rejectReason = S(r, 24), cancelledReason = S(r, 25),
            dispatchedAt = T(26), completedAt = T(27), requestedAt = Utc(r, 28), requestedById = r.GetGuid(29), employeeId = G0(r, 30), senderId = G0(r, 31), receiverId = G0(r, 32), fromRegion = r.GetString(33), toRegion = S(r, 34),
            assets = r.GetInt32(35), maintenanceOrder = S(r, 36),
            overdue = status is "InTransit" or "PartiallyReceived" or "ReceiptDisputed" or "Out" or "PartiallyDispatched" && expected is DateOnly e && e < today
        };
    }
    async Task<bool> SeesMovement(DbConnection c, ItamScope s, MoveRow m, CancellationToken token)
        => s.Id == m.RequestedBy || s.Id == m.Employee || s.Id == m.Sender || s.Id == m.Receiver || MovementIt(s, m) || s.ViewsAll || await MovementApprover(c, null, s, m.RequestedBy, token);

    public async Task<object> Movements(ItamUser user, CancellationToken token)
    {
        await ExpireMovements(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var list = new List<object>();
        foreach (var (row, id) in await Rows(c, null, MoveSelect + "ORDER BY m.movement_id DESC", r => (Row: MapMove(r), Id: r.GetInt32(0)), token))
            if (await SeesMovement(c, s, await LoadMove(c, null, id, token), token)) list.Add(row);
        var approver = s.MovementApproverRole || await MovementApprover(c, null, s, Guid.Empty, token);
        return new { movements = list, me = s.Id, isApprover = approver, canCreateIt = s.CanHandoverAny, validityDays = PassValidityDays, purposes = MovementPurposes };
    }

    public async Task<object> Movement(ItamUser user, int id, CancellationToken token)
    {
        await ExpireMovements(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var m = await LoadMove(c, null, id, token);
        Require(await SeesMovement(c, s, m, token), "You cannot see this movement.");
        if (m.Status == "AwaitingCustody") { await using var tx = await c.BeginTransactionAsync(token); await Recompute(c, tx, id, token); await tx.CommitAsync(token); m = await LoadMove(c, null, id, token); }
        var row = (await Rows(c, null, MoveSelect + "WHERE m.movement_id=@i", MapMove, token, ("@i", id))).Single();
        var lines = await Rows(c, null, "SELECT l.line_id,a.asset_id,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),l.state,l.dispatched_at_utc,l.received_at_utc,rb.display_name,l.condition_note," +
            "l.resolution,l.resolution_note,xb.display_name,l.resolved_at_utc,nd.delivery_no,nd.delivery_id,CASE WHEN l.evidence_file_id IS NULL THEN 0 ELSE 1 END,hd.display_name " +
            "FROM itam_data.movement_line l JOIN itam_data.asset a ON a.asset_id=l.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id " +
            "LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id LEFT JOIN itam_access.employee rb ON rb.employee_id=l.received_by LEFT JOIN itam_access.employee xb ON xb.employee_id=l.resolved_by " +
            "LEFT JOIN itam_data.delivery nd ON nd.delivery_id=l.new_delivery_id LEFT JOIN itam_access.employee hd ON hd.employee_id=l.holder_id WHERE l.movement_id=@i ORDER BY a.asset_no",
            r => new { id = r.GetInt32(0), assetId = r.GetInt32(1), assetNo = r.GetString(2), type = r.GetString(3), model = S(r, 4)?.Trim(), serial = S(r, 5), state = r.GetString(6),
                dispatchedAt = r.IsDBNull(7) ? (DateTime?)null : Utc(r, 7), receivedAt = r.IsDBNull(8) ? (DateTime?)null : Utc(r, 8), receivedBy = S(r, 9), note = S(r, 10), resolution = S(r, 11), resolutionNote = S(r, 12),
                resolvedBy = S(r, 13), resolvedAt = r.IsDBNull(14) ? (DateTime?)null : Utc(r, 14), newDeliveryNo = S(r, 15), newDeliveryId = I(r, 16), hasEvidence = r.GetInt32(17) == 1, holder = S(r, 18) }, token, ("@i", id));
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var it = MovementIt(s, m); var approver = await MovementApprover(c, null, s, m.RequestedBy, token);
        var inValidity = today >= m.ExitDate && today <= m.ValidUntil;
        return new
        {
            movement = row, lines, employeeAd = await EmployeeAd(c, null, m.Employee ?? m.RequestedBy, token),
            can = new
            {
                decide = m.Kind != "Warehouse" && m.Status == "Submitted" && approver,
                dispatch = inValidity && (m.Kind == "Warehouse" ? s.CanHandover(m.FromRegion) && m.Status is "Submitted" or "PartiallyDispatched" : m.Status == "Approved" && (s.Id == m.Sender || s.CanHandover(m.FromRegion))),
                receive = m.Kind != "Exit" && s.Id == m.Receiver && lines.Any(l => l.state == "Dispatched"),
                resolve = await MovementApprover(c, null, s, Guid.Empty, token) && lines.Any(l => l.state is "Missing" or "Damaged" or "Incorrect" or "Dispatched"),
                returned = m.Kind == "Exit" && m.Status == "Out" && (s.Id == m.Employee || s.Id == m.RequestedBy || s.Id == m.Sender || s.CanHandover(m.FromRegion)),
                cancel = (s.Id == m.RequestedBy || s.CanHandover(m.FromRegion)) && m.Status is "Submitted" or "Approved" or "PartiallyDispatched",
                pass = m.Kind == "Warehouse" ? m.Status is not ("Cancelled") : m.Status is not ("Submitted" or "Rejected" or "Cancelled" or "Expired")
            }
        };
    }

    public async Task<ItamUpload> MovementEvidence(ItamUser user, int id, int lineId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var m = await LoadMove(c, null, id, token);
        Require(await SeesMovement(c, s, m, token), "You cannot see this movement.");
        var f = await Scalar(c, null, "SELECT evidence_file_id FROM itam_data.movement_line WHERE line_id=@l AND movement_id=@m", token, ("@l", lineId), ("@m", id)) as long? ?? throw new ArgumentException("No evidence is attached.");
        return await LoadFile(c, null, f, token) ?? throw new ArgumentException("The file was not found.");
    }

    /// <summary>Assets the person may put on a movement: their own custody (employees), or the assets of their regions (IT).</summary>
    public async Task<object> MovementAssets(ItamUser user, string? q, int? warehouseId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var t = (q ?? "").Trim(); var like = "%" + t.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        var rows = await Rows(c, null, "SELECT TOP 300 a.asset_id,a.asset_no,ty.name_en,CONCAT(m.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),a.status,g.name_en,g.region_code,a.location_id," +
            "(SELECT TOP 1 employee_id FROM itam_data.delivery d WHERE d.asset_id=a.asset_id AND d.received_at_utc IS NULL),(SELECT TOP 1 e.display_name FROM itam_data.delivery d JOIN itam_access.employee e ON e.employee_id=d.employee_id WHERE d.asset_id=a.asset_id AND d.received_at_utc IS NULL) " +
            "FROM itam_data.asset a JOIN itam_ref.asset_type ty ON ty.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "WHERE a.status IN(N'InUse',N'InStore') AND (@w IS NULL OR (a.location_id=@w AND a.status=N'InStore')) AND (@q=N'' OR a.asset_no LIKE @lk OR a.serial_key LIKE @lk OR a.service_tag_key LIKE @lk) " +
            "AND NOT EXISTS(SELECT 1 FROM itam_data.movement_line l WHERE l.asset_id=a.asset_id AND l.state IN(N'Pending',N'Dispatched',N'Missing',N'Damaged',N'Incorrect')) ORDER BY a.asset_no",
            r => (Id: r.GetInt32(0), No: r.GetString(1), Type: r.GetString(2), Model: S(r, 3)?.Trim(), Serial: S(r, 4), Status: r.GetString(5), At: r.GetString(6), Region: r.GetString(7), Holder: G0(r, 9), HolderName: S(r, 10)),
            token, ("@w", warehouseId), ("@q", t), ("@lk", like));
        return rows.Where(a => a.Holder == s.Id || s.CanHandover(a.Region)).Select(a => new { id = a.Id, assetNo = a.No, type = a.Type, model = a.Model, serial = a.Serial, status = ItamStatuses.Label(a.Status), keptAt = a.At, region = a.Region, holder = a.HolderName, mine = a.Holder == s.Id });
    }

    // The gate pass (and warehouse transfer note) PDF -----------------------------------------------------------------------------------
    public async Task<ItamUpload> GatePassPdf(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var mr = await LoadMove(c, null, id, token);
        Require(await SeesMovement(c, s, mr, token), "You cannot see this movement.");
        if (mr.Kind != "Warehouse" && mr.Status is "Submitted" or "Rejected" or "Cancelled" or "Expired") throw new ArgumentException("The gate pass prints once it is approved.");
        dynamic m = (await Rows(c, null, MoveSelect + "WHERE m.movement_id=@i", MapMove, token, ("@i", id))).Single();
        var emp = mr.Employee is Guid eid ? (await Rows(c, null, "SELECT display_name,hr_code,job_title,department,branch,company FROM itam_access.employee WHERE employee_id=@e",
            r => (Name: r.GetString(0), Hr: S(r, 1), Title: S(r, 2), Dept: S(r, 3), Branch: S(r, 4), Company: S(r, 5)), token, ("@e", eid))).SingleOrDefault() : default;
        var lines = await Rows(c, null, "SELECT a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),l.state,l.received_at_utc FROM itam_data.movement_line l JOIN itam_data.asset a ON a.asset_id=l.asset_id " +
            "JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id WHERE l.movement_id=@i AND l.state<>N'Cancelled' ORDER BY a.asset_no",
            r => (No: r.GetString(0), Type: r.GetString(1), Model: S(r, 2)?.Trim(), Serial: S(r, 3), State: r.GetString(4), At: r.IsDBNull(5) ? (DateTime?)null : Utc(r, 5)), token, ("@i", id));
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var inv = CultureInfo.InvariantCulture; string D(DateOnly? d) => d?.ToString("dd/MM/yyyy", inv) ?? "";
        var doc = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W; const string muted = "0.35 0.35 0.35", ink = "0 0 0", soft = "0.93 0.93 0.93", band = "0.86 0.86 0.86";
        void Centre(string? text, double cx, double y, double size, double width) { var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…"; doc.Text(t, cx - pdf.Width(t, size) / 2, y, size, false, ink); }
        void Section(string en, string ar) { doc.Box(M, doc.Y - 6, W - 2 * M, 20, band); doc.Text(en, M + 8, doc.Y, 10.5, false, ink, strong: true); doc.Text(ar, W - M - 8, doc.Y, 10.5, true, ink); doc.Y -= 22; }
        void Row(string en, string ar, string? v) { if (string.IsNullOrWhiteSpace(v)) return; doc.Text(en, M + 8, doc.Y, 9, false, muted); Centre(v, W / 2, doc.Y, 10, 260); doc.Text(ar, W - M - 8, doc.Y, 9, true, muted); doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11; }
        var warehouse = mr.Kind == "Warehouse";
        doc.Logo(M, doc.Y + 10, 96); doc.Text("إدارة أصول تقنية المعلومات", W - M, doc.Y, 9, true, muted); doc.Text("IT Assets Management", W - M, doc.Y - 13, 9, true, muted); doc.Y -= 40;
        doc.Text(warehouse ? "Gate Pass · Warehouse Transfer" : "Gate Pass", M, doc.Y, 18, false, ink, strong: true); doc.Text("تصريح خروج", W - M, doc.Y, 16, true, ink); doc.Y -= 10; doc.Line(M, doc.Y, W - M, doc.Y, ink); doc.Y -= 16;
        var cw = (W - 2 * M - 16) / 3;
        (string, string, string)[][] heads = [[("Gate Pass No.", "رقم التصريح", mr.PassNo + (warehouse ? $" · {m.no}" : "")), ("Issue Date", "تاريخ الإصدار", ((DateTime?)(m.decidedAt ?? m.requestedAt))?.AddHours(3).ToString("dd/MM/yyyy", inv) ?? ""), ("Pass Type", "نوع التصريح", mr.PassType == "Returnable" ? "Returnable" : "Non-returnable")],
            [("Exit Date", "تاريخ الخروج", D(mr.ExitDate)), ("Valid Until", "صالح حتى", D(mr.ValidUntil)), (mr.Kind == "Exit" ? "Expected Return" : "Expected Receipt", mr.Kind == "Exit" ? "تاريخ العودة" : "تاريخ الاستلام المتوقع", D(mr.Expected))]];
        foreach (var hr in heads)
        { for (var i = 0; i < 3; i++) { var x = M + i * (cw + 8); doc.Box(x, doc.Y - 26, cw, 38, soft); doc.Text(hr[i].Item1, x + 7, doc.Y, 8, false, muted); doc.Text(hr[i].Item2, x + cw - 7, doc.Y, 8, true, muted); Centre(hr[i].Item3, x + cw / 2, doc.Y - 18, 11.5, cw - 14); } doc.Y -= 44; }
        doc.Text("Security is kindly requested to allow the assets listed below to leave the company premises.", M, doc.Y, 8.5, false, ink); doc.Y -= 11;
        doc.Text("This pass is valid only from the exit date to the valid-until date shown.", M, doc.Y, 8.5, false, ink); doc.Y -= 12;
        doc.Text("يرجى من الأمن السماح بخروج الأصول المذكورة أدناه من مقر الشركة. هذا التصريح صالح فقط من تاريخ الخروج حتى تاريخ انتهاء الصلاحية المذكور.", W - M, doc.Y, 8.5, true, ink); doc.Y -= 16;
        if (emp.Name is not null)
        { Section("Employee Information", "بيانات الموظف"); Row("Employee Name", "اسم الموظف", emp.Name); Row("HR Number", "الرقم الوظيفي", emp.Hr); Row("Job Title", "المسمى الوظيفي", emp.Title); Row("Department", "اسم القسم", emp.Dept); Row("Branch Name", "اسم الفرع", emp.Branch); }
        Section("Exit Information", "بيانات الخروج");
        Row("Purpose", "الغرض", (string)m.purpose + (m.purposeDetails is string pd ? " · " + pd : "")); Row(warehouse ? "From IT Warehouse" : "From Branch", "من", (string)m.from);
        Row("Destination", "الوجهة", (string?)m.to ?? (string?)m.destination); Row("Sender", "المرسل", (string?)m.sender); Row("Receiver", "المستلم", (string?)m.receiver);
        Row("Carried By", "حامل التصريح", (string)m.carriedBy == "Other" ? (string?)m.carrierName : (string?)m.employee ?? (string?)m.sender); Row("Carrier Mobile", "جوال حامل التصريح", (string?)m.carrierMobile);
        Row(warehouse ? "Issued By" : "Approved By", warehouse ? "أصدره" : "اعتمده", warehouse ? $"{m.requestedBy} · {((DateTime)m.requestedAt).AddHours(3).ToString("dd/MM/yyyy HH:mm", inv)}" : $"{m.decidedBy} · {((DateTime?)m.decidedAt)?.AddHours(3).ToString("dd/MM/yyyy HH:mm", inv)}");
        Section("Assets", "الأصول");
        double[] col = [M, M + 95, M + 200, M + 360, W - M];
        doc.Text("Asset No.", col[0] + 6, doc.Y, 8.5, false, muted, strong: true); doc.Text("Type", col[1] + 6, doc.Y, 8.5, false, muted, strong: true); doc.Text("Manufacturer and model", col[2] + 6, doc.Y, 8.5, false, muted, strong: true);
        doc.Text(mr.Status is "InTransit" or "PartiallyReceived" or "ReceiptDisputed" or "Completed" or "ClosedWithExceptions" or "AwaitingCustody" && !(mr.Kind == "Exit") ? "Serial · received" : "Serial / service tag", col[3] + 6, doc.Y, 8.5, false, muted, strong: true);
        doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y, ink); doc.Y -= 12;
        foreach (var l in lines)
        {
            if (doc.Y < M + 120) doc.NewPage();
            doc.Text(l.No, col[0] + 6, doc.Y, 9.5, false, ink, strong: true); doc.Fit(l.Type, col[1] + 6, doc.Y, 9.5, col[2] - col[1] - 10, ink); doc.Fit(l.Model, col[2] + 6, doc.Y, 9.5, col[3] - col[2] - 10, ink);
            doc.Fit((l.Serial ?? "") + (l.At is DateTime at ? $" · {at.AddHours(3):dd/MM HH:mm}" : l.State is "Missing" or "Damaged" or "Incorrect" or "Lost" ? " · " + l.State.ToLowerInvariant() : ""), col[3] + 6, doc.Y, 9, col[4] - col[3] - 10, ink);
            doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11;
        }
        var sigY = Math.Min(doc.Y - 8, M + 110); var half = (W - 2 * M - 120) / 2;
        foreach (var (en, ar, i) in new[] { ("Sender signature", "توقيع المرسل", 0), ("Security (exit check)", "الأمن", 1) })
        { var x = M + i * (half + 20); doc.Text(en, x, sigY, 9, false, muted); doc.Text(ar, x + half, sigY - 12, 9, true, muted); doc.Line(x, sigY - 50, x + half, sigY - 50, "0.45 0.45 0.45"); }
        using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode($"{BaseUrl}/g/{mr.PassNo}", QRCodeGenerator.ECCLevel.M);
        var mx = data.ModuleMatrix; var qr = 74.0; var cell = qr / mx.Count; double qx = W - M - qr, qy = sigY - 66;
        for (var rr = 0; rr < mx.Count; rr++) for (var k = 0; k < mx[rr].Length; k++) if (mx[rr][k]) doc.Box(qx + k * cell, qy + (mx.Count - 1 - rr) * cell, cell + 0.05, cell + 0.05, "0 0 0");
        doc.Text("Optional check", qx + qr / 2 - pdf.Width("Optional check", 7) / 2, qy - 9, 7, false, muted);
        doc.Text($"Printed {DateTime.UtcNow.AddHours(3).ToString("dd/MM/yyyy HH:mm", inv)} by {s.Name} · IT Assets · {mr.PassNo} version {m.passVersion}", M, M - 10, 7.5, false, muted);
        return new($"Gate pass {mr.PassNo}.pdf", "application/pdf", doc.Build());
    }

    internal static async Task<int> MovementApprovalsCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        var n = 0;
        foreach (var by in await Rows(c, null, "SELECT requested_by FROM itam_data.movement WHERE status=N'Submitted' AND kind<>N'Warehouse'", r => r.GetGuid(0), token))
            if (await MovementApprover(c, null, s, by, token)) n++;
        return n;
    }

    public async Task<object> MovementByNo(ItamUser user, string? no, CancellationToken token)
    {
        await using var c = await Open(token);
        await Scope(c, null, user, token);
        var n = (no ?? "").Trim().ToUpperInvariant();
        var id = await Scalar(c, null, "SELECT TOP 1 movement_id FROM itam_data.movement WHERE movement_no=@n OR pass_no=@n", token, ("@n", n)) as int? ?? throw new ArgumentException("The movement was not found.");
        return new { id };
    }
}
