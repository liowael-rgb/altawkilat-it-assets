using System.Data;
using System.Data.Common;

namespace Itam.App;

public sealed record ItamMaintenanceOrderInput(string? AssetNo, int? SupplierId, string? QuotationNo, decimal? Cost, string? Details, DateOnly? ExpectedReturn);
public sealed record ItamMaintenanceDispatch(DateOnly? DispatchedOn);
public sealed record ItamMaintenanceReturn(DateOnly? ReturnedOn, int? LocationId, string? Outcome, string? RepairDetails, decimal? FinalCost, string? InvoiceNo);
public sealed record ItamMaintenanceExpected(DateOnly? ExpectedReturn);
public sealed record ItamItRepair(string? Details);

/// <summary>Maintenance orders MO-n (form 07 Maintenance Order, plan v1.9): an asset repaired by an outside supplier. The IT
/// Assets Officer creates the order with the supplier, quotation number and file, cost, maintenance details and expected
/// return; the IT Manager, or the IT Manager's backup on leave, approves or rejects it (a rejection needs a reason; nobody
/// approves their own order). Only an approved order is sent: the actual dispatch date puts the asset In Repair. On return
/// the order is closed with the outcome, repair details, final cost, invoice number and end date, and the asset goes back
/// to its holder (In Use) or In Store at the return location. An unsuccessful repair does not dispose of the asset: a
/// technical assessment on its delivery record does. Repairs IT does itself are recorded on the asset history.</summary>
public sealed partial class ItamStore
{
    static string OrderStatusLabel(string s) => s switch { "WaitingApproval" => "Waiting approval", "Approved" => "Approved, to send", "WithSupplier" => "With the supplier", _ => s };
    static bool MaintenanceManage(ItamScope s) => s.MaintenanceManage;
    static bool MaintenanceView(ItamScope s) => s.MaintenanceView;

    /// <summary>True when the person may decide the order: an IT Manager, or the backup of an IT Manager on leave today; never
    /// the person who created it.</summary>
    static async Task<bool> MaintenanceApprover(DbConnection c, DbTransaction? tx, ItamScope s, Guid createdBy, CancellationToken token)
        => s.Id != createdBy && (s.MaintenanceApproverRole || await Scalar(c, tx, "SELECT TOP 1 1 FROM itam_access.leave_backup b JOIN itam_access.role_assignment r ON r.employee_id=b.person_id AND r.role_code IN(N'ItManager',N'TeamLeader',N'SystemAdministrator') " +
            "WHERE b.backup_employee_id=@me AND b.cancelled_at_utc IS NULL AND b.person_id<>@c AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to", token, ("@me", s.Id), ("@c", createdBy)) is not null);

    /// <summary>The asset by its Asset No. (typed or scanned from the label), with the product fields the order shows read-only.</summary>
    public async Task<object> MaintenanceAsset(ItamUser user, string? assetNo, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(MaintenanceManage(s), "You cannot create maintenance orders.");
        var no = Text(assetNo, "Asset No.", 20).ToUpperInvariant();
        var a = (await Rows(c, null, "SELECT a.asset_id,a.asset_no,t.name_en,m.name_en,mo.name_en,a.service_tag,a.serial_no,a.mac_address,a.status,g.name_en FROM itam_data.asset a JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id WHERE a.asset_no=@n",
            r => (Id: r.GetInt32(0), No: r.GetString(1), Type: r.GetString(2), Maker: S(r, 3), Model: S(r, 4), Tag: S(r, 5), Serial: S(r, 6), Mac: S(r, 7), Status: r.GetString(8), At: r.GetString(9)), token, ("@n", no))).SingleOrDefault();
        if (a.No is null) throw new ArgumentException($"{no} is not in IT Assets.");
        var attrs = (await Rows(c, null, "SELECT attr_code,value FROM itam_data.asset_attribute WHERE asset_id=@a", r => (r.GetString(0), r.GetString(1)), token, ("@a", a.Id))).ToDictionary(x => x.Item1, x => x.Item2);
        var open = await Scalar(c, null, "SELECT order_no FROM itam_data.maintenance_order WHERE asset_id=@a AND status IN(N'WaitingApproval',N'Approved',N'WithSupplier')", token, ("@a", a.Id)) as string;
        return new { id = a.Id, assetNo = a.No, productType = a.Type, manufacturer = a.Maker, model = a.Model, computerType = attrs.GetValueOrDefault("ComputerType"), serviceTag = a.Tag,
            processor = attrs.GetValueOrDefault("Processor"), capacity = attrs.GetValueOrDefault("CapacityGb"), memory = attrs.GetValueOrDefault("MemoryGb"), serial = a.Serial, mac = a.Mac,
            status = ItamStatuses.Label(a.Status), keptAt = a.At, openOrder = open };
    }

    public async Task<object> CreateMaintenanceOrder(ItamUser user, ItamMaintenanceOrderInput x, ItamUpload? quotation, CancellationToken token)
    {
        if (quotation is null) throw new ArgumentException("Attach the supplier's quotation (PDF, JPEG or PNG).");
        var type = CheckFile(quotation, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(MaintenanceManage(s), "You cannot create maintenance orders.");
        var no = Text(x.AssetNo, "Asset No.", 20).ToUpperInvariant();
        var a = (await Rows(c, tx, "SELECT asset_id,status FROM itam_data.asset WITH (UPDLOCK) WHERE asset_no=@n", r => (Id: r.GetInt32(0), Status: r.GetString(1)), token, ("@n", no))).SingleOrDefault();
        if (a.Status is null) throw new ArgumentException($"{no} is not in IT Assets.");
        if (a.Status is not ("InUse" or "InStore")) throw new ArgumentException($"{no} is {ItamStatuses.Label(a.Status)}: only an asset In Use or In Store is sent for repair.");
        if (await Scalar(c, tx, "SELECT order_no FROM itam_data.maintenance_order WHERE asset_id=@a AND status IN(N'WaitingApproval',N'Approved',N'WithSupplier')", token, ("@a", a.Id)) is string open)
            throw new ArgumentException($"{no} already has maintenance order {open}.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s AND active=1", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
        var quote = Text(x.QuotationNo, "Quotation No.", 60);
        var cost = x.Cost is decimal k && k >= 0 ? k : throw new ArgumentException("Cost: the amount on the quotation (SAR).");
        var details = Text(x.Details, "Maintenance details", 1000);
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var expected = x.ExpectedReturn is DateOnly e && e >= today ? e : throw new ArgumentException("Expected supplier return: today or later.");
        var file = await SaveFile(c, tx, quotation, type, s.Id, token);
        var orderNo = $"MO-{await Next(c, tx, "MaintenanceOrder", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.maintenance_order(order_no,asset_id,supplier_id,quotation_no,quotation_file_id,cost,details,expected_return,status,created_by) OUTPUT inserted.order_id " +
            "VALUES(@n,@a,@s,@q,@f,@c,@d,@e,N'WaitingApproval',@b)", token, ("@n", orderNo), ("@a", a.Id), ("@s", x.SupplierId), ("@q", quote), ("@f", file), ("@c", cost), ("@d", details), ("@e", DateParam(expected)), ("@b", s.Id)));
        await Event(c, tx, a.Id, s.Id, "Maintenance order", $"{orderNo} created: {details} (quotation {quote}, {Money(cost)}); waiting for the IT Manager's approval", token);
        await Audit(c, tx, s.Id, "maintenance", orderNo, "Create", null, new { asset = no, x.SupplierId, quote, cost, expected }, null, token);
        await MailMaintenance(c, tx, id, "created", s.Id);
        await tx.CommitAsync(token);
        return new { id, orderNo };
    }

    sealed record OrderRow(int Id, string No, int AssetId, string AssetNo, string Status, Guid CreatedBy, DateOnly? Dispatched, string? Previous, decimal Cost);
    static async Task<OrderRow> LoadOrder(DbConnection c, DbTransaction tx, int id, CancellationToken token)
        => (await Rows(c, tx, "SELECT o.order_id,o.order_no,o.asset_id,a.asset_no,o.status,o.created_by,o.dispatched_on,o.previous_status,o.cost FROM itam_data.maintenance_order o WITH (UPDLOCK) JOIN itam_data.asset a ON a.asset_id=o.asset_id WHERE o.order_id=@i",
            r => new OrderRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetGuid(5), Day(r, 6), S(r, 7), r.GetDecimal(8)), token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The maintenance order was not found.");

    public async Task DecideMaintenance(ItamUser user, int id, ItamDecision x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var o = await LoadOrder(c, tx, id, token);
        Require(await MaintenanceApprover(c, tx, s, o.CreatedBy, token), "Only the IT Helpdesk Team Leader, the System Administrator or the IT Manager (or their backup during leave) decides maintenance orders; nobody approves their own order.");
        if (o.Status != "WaitingApproval") throw new ArgumentException($"{o.No} is {OrderStatusLabel(o.Status)}.");
        var reason = x.Approve ? null : Text(x.Reason, "Reason for the rejection", 500);
        await Exec(c, tx, "UPDATE itam_data.maintenance_order SET status=@st,decided_by=@b,decided_at_utc=SYSUTCDATETIME(),rejection_reason=@r WHERE order_id=@i", token,
            ("@st", x.Approve ? "Approved" : "Rejected"), ("@b", s.Id), ("@r", reason), ("@i", id));
        await Event(c, tx, o.AssetId, s.Id, "Maintenance order", x.Approve ? $"{o.No} approved" : $"{o.No} rejected: {reason}", token);
        await Audit(c, tx, s.Id, "maintenance", o.No, x.Approve ? "Approve" : "Reject", null, new { reason }, null, token);
        await MailMaintenance(c, tx, id, "decided", s.Id);
        await tx.CommitAsync(token);
    }

    /// <summary>The asset actually leaves for the supplier (approval alone does not send it): In Repair from that date.</summary>
    public async Task DispatchMaintenance(ItamUser user, int id, ItamMaintenanceDispatch x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(MaintenanceManage(s), "You cannot record the dispatch to the supplier.");
        var o = await LoadOrder(c, tx, id, token);
        if (o.Status != "Approved") throw new ArgumentException($"{o.No} is {OrderStatusLabel(o.Status)}: only an approved order is sent.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.DispatchedOn ?? today;
        if (on > today) throw new ArgumentException("The dispatch date cannot be in the future.");
        var status = await Scalar(c, tx, "SELECT status FROM itam_data.asset WITH (UPDLOCK) WHERE asset_id=@a", token, ("@a", o.AssetId)) as string;
        if (status is not ("InUse" or "InStore")) throw new ArgumentException($"{o.AssetNo} is {ItamStatuses.Label(status ?? "")}.");
        await Exec(c, tx, "UPDATE itam_data.maintenance_order SET status=N'WithSupplier',dispatched_on=@d,dispatched_by=@b,previous_status=@p WHERE order_id=@i", token, ("@d", DateParam(on)), ("@b", s.Id), ("@p", status), ("@i", id));
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InRepair' WHERE asset_id=@a", token, ("@a", o.AssetId));
        await Event(c, tx, o.AssetId, s.Id, "Maintenance order", $"{o.No}: sent to the supplier on {on:dd/MM/yyyy} (In Repair)", token);
        await Audit(c, tx, s.Id, "maintenance", o.No, "Dispatch", null, new { on }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>The asset is back: close the order with the outcome, repair details, final cost, invoice and end date. With a
    /// holder it is In Use again; otherwise In Store at the return location.</summary>
    public async Task ReturnMaintenance(ItamUser user, int id, ItamMaintenanceReturn x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(MaintenanceManage(s), "You cannot record the return from the supplier.");
        var o = await LoadOrder(c, tx, id, token);
        if (o.Status != "WithSupplier") throw new ArgumentException($"{o.No} is {OrderStatusLabel(o.Status)}: only an asset with the supplier comes back.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.ReturnedOn ?? today;
        if (on > today || on < o.Dispatched) throw new ArgumentException("The end date is between the dispatch date and today.");
        var outcome = x.Outcome is "Repaired" or "NotRepairable" ? x.Outcome : throw new ArgumentException("Choose the repair outcome.");
        var details = Text(x.RepairDetails, "Repair details", 1000);
        var cost = x.FinalCost is decimal k && k >= 0 ? k : o.Cost;
        var invoiceNo = Optional(x.InvoiceNo, "Invoice No.", 60);
        if (outcome == "Repaired" && invoiceNo is null) throw new ArgumentException("Invoice No.: required to close a repaired order.");
        var supplier = Convert.ToInt32(await Scalar(c, tx, "SELECT supplier_id FROM itam_data.maintenance_order WHERE order_id=@i", token, ("@i", id)));
        var invoiceId = invoiceNo is null ? null : await Scalar(c, tx, "SELECT invoice_id FROM itam_data.invoice WHERE supplier_id=@s AND invoice_no=@n", token, ("@s", supplier), ("@n", invoiceNo)) as int?;
        var held = await Scalar(c, tx, "SELECT 1 FROM itam_data.delivery WHERE asset_id=@a AND received_at_utc IS NULL", token, ("@a", o.AssetId)) is not null;
        int? location = null; string? kind = null;
        if (!held)
        {
            var loc = (await Rows(c, tx, "SELECT location_id,kind FROM itam_ref.location WHERE location_id=@l AND active=1", r => (Id: r.GetInt32(0), Kind: r.GetString(1)), token, ("@l", x.LocationId ?? 0))).SingleOrDefault();
            if (loc.Kind is null) throw new ArgumentException("Choose where the asset is kept now (branch or IT warehouse).");
            (location, kind) = (loc.Id, loc.Kind);
        }
        await Exec(c, tx, "UPDATE itam_data.maintenance_order SET status=N'Closed',returned_on=@d,return_location_id=@l,outcome=@o,repair_details=@r,final_cost=@c,invoice_no=@n,invoice_id=@inv,closed_by=@b WHERE order_id=@i", token,
            ("@d", DateParam(on)), ("@l", location), ("@o", outcome), ("@r", details), ("@c", cost), ("@n", invoiceNo), ("@inv", invoiceId), ("@b", s.Id), ("@i", id));
        if (held) await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InUse' WHERE asset_id=@a", token, ("@a", o.AssetId));
        else await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InStore',location_id=@l,stock_location=@k WHERE asset_id=@a", token, ("@l", location), ("@k", kind), ("@a", o.AssetId));
        await Event(c, tx, o.AssetId, s.Id, "Maintenance order", $"{o.No} closed: {(outcome == "Repaired" ? "repaired" : "not repairable")}, {details} ({Money(cost)}{(invoiceNo is null ? "" : ", invoice " + invoiceNo)}); {(held ? "back with its holder (In Use)" : "In Store")}" +
            (outcome == "NotRepairable" ? ". Record a technical assessment for disposal." : ""), token);
        await Audit(c, tx, s.Id, "maintenance", o.No, "Close", null, new { on, outcome, cost, invoiceNo, location }, null, token);
        await MailMaintenance(c, tx, id, "returned", s.Id);
        await tx.CommitAsync(token);
    }

    public async Task CancelMaintenance(ItamUser user, int id, ItamCancelInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(MaintenanceManage(s), "You cannot cancel maintenance orders.");
        var o = await LoadOrder(c, tx, id, token);
        if (o.Status is not ("WaitingApproval" or "Approved")) throw new ArgumentException($"{o.No} is {OrderStatusLabel(o.Status)}: only an order not yet sent is cancelled.");
        var reason = Text(x.Reason, "Reason", 500);
        await Exec(c, tx, "UPDATE itam_data.maintenance_order SET status=N'Cancelled',cancelled_reason=@r WHERE order_id=@i", token, ("@r", reason), ("@i", id));
        await Event(c, tx, o.AssetId, s.Id, "Maintenance order", $"{o.No} cancelled: {reason}", token);
        await Audit(c, tx, s.Id, "maintenance", o.No, "Cancel", null, new { reason }, null, token);
        await tx.CommitAsync(token);
    }

    public async Task ExpectedMaintenance(ItamUser user, int id, ItamMaintenanceExpected x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(MaintenanceManage(s), "You cannot change the expected return.");
        var o = await LoadOrder(c, tx, id, token);
        if (o.Status is "Closed" or "Rejected" or "Cancelled") throw new ArgumentException($"{o.No} is {OrderStatusLabel(o.Status)}.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var e = x.ExpectedReturn is DateOnly d && d >= today ? d : throw new ArgumentException("Expected supplier return: today or later.");
        await Exec(c, tx, "UPDATE itam_data.maintenance_order SET expected_return=@e WHERE order_id=@i", token, ("@e", DateParam(e)), ("@i", id));
        await Audit(c, tx, s.Id, "maintenance", o.No, "Expected return", null, new { e }, null, token);
        await tx.CommitAsync(token);
    }

    public async Task<ItamUpload> MaintenanceQuotation(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var o = (await Rows(c, null, "SELECT quotation_file_id,created_by FROM itam_data.maintenance_order WHERE order_id=@i", r => (File: r.GetInt64(0), By: r.GetGuid(1)), token, ("@i", id))).SingleOrDefault();
        if (o.File == 0) throw new ArgumentException("The maintenance order was not found.");
        Require(MaintenanceView(s) || await MaintenanceApprover(c, null, s, o.By, token), "You cannot see this quotation.");
        return await LoadFile(c, null, o.File, token) ?? throw new ArgumentException("The file was not found.");
    }

    const string OrderSelect = "SELECT o.order_id,o.order_no,a.asset_id,a.asset_no,t.name_en,CONCAT(m.name_en,' ',mo.name_en),su.name_en,o.quotation_no,o.cost,o.details,o.expected_return,o.status," +
        "cb.display_name,o.created_at_utc,db.display_name,o.decided_at_utc,o.rejection_reason,o.dispatched_on,o.returned_on,rl.name_en,o.outcome,o.repair_details,o.final_cost,o.invoice_no,o.cancelled_reason,o.created_by " +
        "FROM itam_data.maintenance_order o JOIN itam_data.asset a ON a.asset_id=o.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id " +
        "LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id LEFT JOIN itam_access.employee cb ON cb.employee_id=o.created_by " +
        "LEFT JOIN itam_access.employee db ON db.employee_id=o.decided_by LEFT JOIN itam_ref.location rl ON rl.location_id=o.return_location_id ";
    object MapOrder(DbDataReader r)
    {
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var status = r.GetString(11); var expected = Day(r, 10)!.Value;
        return new { id = r.GetInt32(0), no = r.GetString(1), assetId = r.GetInt32(2), assetNo = r.GetString(3), productType = r.GetString(4), product = S(r, 5)?.Trim(), supplier = r.GetString(6),
            quotationNo = r.GetString(7), cost = r.GetDecimal(8), details = r.GetString(9), expectedReturn = expected, status, statusLabel = OrderStatusLabel(status),
            createdBy = S(r, 12), createdAt = Utc(r, 13), decidedBy = S(r, 14), decidedAt = r.IsDBNull(15) ? (DateTime?)null : Utc(r, 15), rejectionReason = S(r, 16), dispatchedOn = Day(r, 17), returnedOn = Day(r, 18),
            returnLocation = S(r, 19), outcome = r.IsDBNull(20) ? null : r.GetString(20) == "Repaired" ? "Repaired" : "Not repairable", repairDetails = S(r, 21), finalCost = r.IsDBNull(22) ? (decimal?)null : r.GetDecimal(22),
            invoiceNo = S(r, 23), cancelledReason = S(r, 24), overdue = status == "WithSupplier" && expected < today, daysOverdue = status == "WithSupplier" && expected < today ? today.DayNumber - expected.DayNumber : 0, createdById = r.GetGuid(25) };
    }

    /// <summary>The Maintenance page: every order (newest first), the orders waiting for my approval, and what I may do.</summary>
    public async Task<object> MaintenanceOrders(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var orders = MaintenanceView(s) ? await Rows(c, null, OrderSelect + "ORDER BY o.order_id DESC", MapOrder, token) : [];
        var waiting = await Rows(c, null, OrderSelect + "WHERE o.status=N'WaitingApproval' ORDER BY o.order_id", r => (Row: MapOrder(r), By: r.GetGuid(25)), token);
        var approvals = new List<object>();
        foreach (var w in waiting) if (await MaintenanceApprover(c, null, s, w.By, token)) approvals.Add(w.Row);
        Require(MaintenanceView(s) || approvals.Count > 0, "You cannot see maintenance orders.");
        return new { orders, approvals, canManage = MaintenanceManage(s) };
    }

    internal static async Task<int> MaintenanceApprovalsCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        var count = 0;
        foreach (var by in await Rows(c, null, "SELECT created_by FROM itam_data.maintenance_order WHERE status=N'WaitingApproval'", r => r.GetGuid(0), token))
            if (await MaintenanceApprover(c, null, s, by, token)) count++;
        return count;
    }

    /// <summary>A repair IT did itself (not a maintenance order): recorded on the asset history.</summary>
    public async Task ItRepair(ItamUser user, int assetId, ItamItRepair x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var a = (await Rows(c, tx, "SELECT a.asset_no,a.type_code,g.region_code FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@a",
            r => (No: r.GetString(0), Type: r.GetString(1), Region: r.GetString(2)), token, ("@a", assetId))).SingleOrDefault();
        if (a.No is null) throw new ArgumentException("The asset was not found.");
        Require(s.ItRepair(a.Region), "IT of the asset's region records repairs done by IT.");
        var details = Text(x.Details, "Repair done", 1000);
        await Event(c, tx, assetId, s.Id, "IT repair", details, token);
        await Audit(c, tx, s.Id, "asset", a.No, "IT repair", null, new { details }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>The asset's history in three parts: deliveries, receipts (returns) and maintenance (orders and IT repairs).</summary>
    async Task<object> AssetHistory(DbConnection c, ItamScope s, int assetId, string region, CancellationToken token)
    {
        var deliveries = await Rows(c, null, "SELECT d.delivery_id,d.delivery_no,e.display_name,e.account,e.sap_id,d.delivered_on,d.owner_type,ib.display_name,d.signed_at_utc,d.received_at_utc,rb.display_name,d.receipt_condition,rl.name_en,ta.assessment_no,ta.recommendation " +
            "FROM itam_data.delivery d JOIN itam_access.employee e ON e.employee_id=d.employee_id LEFT JOIN itam_access.employee ib ON ib.employee_id=d.issued_by LEFT JOIN itam_access.employee rb ON rb.employee_id=d.received_by " +
            "LEFT JOIN itam_ref.location rl ON rl.location_id=d.receipt_location_id LEFT JOIN itam_data.assessment ta ON ta.delivery_id=d.delivery_id WHERE d.asset_id=@a ORDER BY d.delivery_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), employee = r.GetString(2), username = r.GetString(3), sapId = S(r, 4), deliveredOn = Day(r, 5), ownerType = S(r, 6), issuedBy = S(r, 7),
                signedAt = r.IsDBNull(8) ? (DateTime?)null : Utc(r, 8), receivedAt = r.IsDBNull(9) ? (DateTime?)null : Utc(r, 9), receivedBy = S(r, 10), condition = S(r, 11), receivedInto = S(r, 12),
                receiptNo = r.IsDBNull(9) ? null : r.GetString(1) + "-R", assessmentNo = S(r, 13), recommendation = r.IsDBNull(14) ? null : RecommendationLabel(r.GetString(14)) }, token, ("@a", assetId));
        var orders = await Rows(c, null, OrderSelect + "WHERE o.asset_id=@a ORDER BY o.order_id DESC", MapOrder, token, ("@a", assetId));
        var itRepairs = await Rows(c, null, "SELECT v.at_utc,e.display_name,v.detail FROM itam_data.asset_event v LEFT JOIN itam_access.employee e ON e.employee_id=v.actor_id WHERE v.asset_id=@a AND v.kind=N'IT repair' ORDER BY v.event_id DESC",
            r => new { at = Utc(r, 0), by = S(r, 1), details = S(r, 2) }, token, ("@a", assetId));
        // Ink requests of this printer (owner, 23 October 2026), newest first.
        var ink = await Rows(c, null, "SELECT q.request_no,q.requested_at_utc,e.display_name,q.toner,q.quantity,q.status,o.order_no,dv.delivered_on,q.reject_reason,q.cancelled_reason " +
            "FROM itam_data.ink_request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.supplier_order o ON o.order_id=q.order_id " +
            "LEFT JOIN itam_data.ink_delivery dv ON dv.request_id=q.request_id WHERE q.printer_asset_id=@a ORDER BY q.request_id DESC",
            r => new { no = r.GetString(0), at = Utc(r, 1), employee = r.GetString(2), toner = r.GetString(3), quantity = r.GetInt32(4), status = r.GetString(5), statusLabel = InkStatusLabel(r.GetString(5)),
                order = S(r, 6), deliveredOn = Day(r, 7), why = S(r, 8) ?? S(r, 9) }, token, ("@a", assetId));
        return new { deliveries, orders, itRepairs, ink, canItRepair = s.ItRepair(region), canOrder = MaintenanceManage(s) };
    }
}
