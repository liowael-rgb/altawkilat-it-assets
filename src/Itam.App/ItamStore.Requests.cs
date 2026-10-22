using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Itam.App;

public sealed record ItamRequestInput(Guid? EmployeeId, string? Type, int? ReplacedAssetId, string? Description);
public sealed record ItamRequestReview(string? Source, int? ReservedAssetId, Guid? AssignTo, string? RejectReason);
public sealed record ItamCostLine(string? Item, string? Specification, int? Quantity, decimal? UnitCost);
public sealed record ItamCostInput(int? SupplierId, string? Shop, List<ItamCostLine>? Lines);
public sealed record ItamRequestOrdered(int? PoId);
public sealed record ItamCashPurchase(string? Shop, DateOnly? PurchaseDate, decimal? Amount);
public sealed record ItamRequestFulfil(int? DeliveryId);
public sealed record ItamRequestDate(DateOnly? On);

/// <summary>IT procurement requests PR-n (plan v1.9 step 3, decisions of 20 October 2026). The employee (or the manager for a
/// team member) asks for a device; the direct manager, or the backup on leave, approves the request. The IT Assets Officer
/// chooses the source: From stock (reserve an In Store device and assign an IT Support Specialist, who delivers it), New
/// device (cost table with supplier and quotation, the manager approves the cost, the purchase request goes to Accounting,
/// ordered on a PO, delivered) or IT accessory (a specialist of the employee's region prices it, the manager approves the
/// cost, bought with petty cash with the receipt; no Asset No.). Nobody approves their own request. No ServiceDesk ticket.</summary>
public sealed partial class ItamStore
{
    static string RequestStatusLabel(string s) => s switch
    {
        "WaitingRequestApproval" => "Waiting manager approval", "InReview" => "With the IT Assets Officer", "StockAssigned" => "From stock: to deliver",
        "PricingInRegion" => "Pricing in region", "WaitingCostApproval" => "Waiting cost approval", "Approved" => "Cost approved: to order", "Ordered" => "Ordered",
        "CashPurchase" => "Cash purchase in region", _ => s
    };
    static string SourceLabel(string? s) => s switch { "Stock" => "From stock", "NewDevice" => "New device", "Accessory" => "IT accessory: buy in the region", _ => "" };

    sealed record ReqRow(int Id, string No, Guid Employee, Guid RequestedBy, string Type, int? Replaced, string Status, Guid? Manager, string? Source, Guid? AssignedTo,
        int? Reserved, DateTime RequestedAt, string? Region);
    static async Task<ReqRow> LoadRequest(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
        => (await Rows(c, tx, $"SELECT request_id,request_no,employee_id,requested_by,request_type,replaced_asset_id,status,manager_id,source,assigned_to,reserved_asset_id,requested_at_utc,region_code FROM itam_data.request {(tx is null ? "" : "WITH (UPDLOCK) ")}WHERE request_id=@i",
            r => new ReqRow(r.GetInt32(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), I(r, 5), r.GetString(6), r.IsDBNull(7) ? null : r.GetGuid(7), S(r, 8),
                r.IsDBNull(9) ? null : r.GetGuid(9), I(r, 10), Utc(r, 11), S(r, 12)), token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The request was not found.");

    /// <summary>The employee's direct manager (or the manager's backup during leave), or the IT Manager (or the IT Manager's backup
    /// during leave) for every request (owner, 22 October 2026); never for their own request or one they raised.</summary>
    static async Task<bool> ManagerOf(DbConnection c, DbTransaction? tx, ItamScope s, ReqRow r, CancellationToken token)
        => s.Id != r.Employee && s.Id != r.RequestedBy && (s.Manager || r.Manager is Guid m && await ActsFor(c, tx, s.Id, m, token)
           || await Scalar(c, tx, "SELECT TOP 1 1 FROM itam_access.leave_backup b JOIN itam_access.role_assignment ra ON ra.employee_id=b.person_id AND ra.role_code=N'ItManager' " +
               "WHERE b.backup_employee_id=@me AND b.cancelled_at_utc IS NULL AND b.person_id NOT IN(@e,@q) AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to",
               token, ("@me", s.Id), ("@e", r.Employee), ("@q", r.RequestedBy)) is not null);

    // Create and the request approval ------------------------------------------------------------------------------------------------------
    public async Task<object> CreateRequest(ItamUser user, ItamRequestInput x, ItamUpload? assessment, CancellationToken token)
    {
        var fileType = assessment is null ? null : CheckFile(assessment, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var who = x.EmployeeId ?? s.Id;
        var e = (await Rows(c, tx, "SELECT display_name,manager_id,enabled,department,region FROM itam_access.employee WHERE employee_id=@e",
            r => (Name: r.GetString(0), Manager: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), Enabled: r.GetBoolean(2), Dept: S(r, 3), Region: S(r, 4)), token, ("@e", who))).SingleOrDefault();
        if (e.Name is null) throw new ArgumentException("Choose the employee.");
        if (who != s.Id) Require(e.Manager == s.Id, "You ask for yourself, or as the direct manager for a member of your team.");
        if (!e.Enabled) throw new ArgumentException($"{e.Name} is disabled in Active Directory.");
        if (e.Manager is null) throw new ArgumentException($"{e.Name} has no direct manager in Active Directory to approve the request.");
        var type = x.Type is "New" or "Replacement" ? x.Type : throw new ArgumentException("Choose the request type: New or Replacement.");
        var description = Text(x.Description, "What is needed and why", 2000);
        string? taNo = null; long? taFile = null;
        if (type == "Replacement")
        {
            var held = await Scalar(c, tx, "SELECT d.delivery_id FROM itam_data.delivery d WHERE d.asset_id=@a AND d.employee_id=@e AND d.received_at_utc IS NULL", token, ("@a", x.ReplacedAssetId ?? 0), ("@e", who)) as int?
                ?? throw new ArgumentException("Choose the device being replaced, from the devices in the employee's custody.");
            taNo = await Scalar(c, tx, "SELECT TOP 1 ta.assessment_no FROM itam_data.assessment ta JOIN itam_data.delivery d ON d.delivery_id=ta.delivery_id WHERE d.asset_id=@a ORDER BY ta.assessed_at_utc DESC", token, ("@a", x.ReplacedAssetId)) as string;
            if (taNo is null && assessment is null) throw new ArgumentException("Attach the technical assessment of the device being replaced (none is recorded in IT Assets).");
            if (assessment is not null) taFile = await SaveFile(c, tx, assessment, fileType!, s.Id, token);
        }
        var region = await Scalar(c, tx, "SELECT TOP 1 region_code FROM itam_ref.region WHERE (region_code=@r OR name_en=@r) AND active=1", token, ("@r", e.Region ?? "")) as string;
        var no = $"PR-{await Next(c, tx, "Request", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.request(request_no,employee_id,requested_by,request_type,replaced_asset_id,assessment_file_id,assessment_no,description,region_code,department,status,manager_id) " +
            "OUTPUT inserted.request_id VALUES(@n,@e,@b,@t,@a,@f,@ta,@d,@r,@dp,N'WaitingRequestApproval',@m)", token,
            ("@n", no), ("@e", who), ("@b", s.Id), ("@t", type), ("@a", type == "Replacement" ? x.ReplacedAssetId : null), ("@f", taFile), ("@ta", taNo), ("@d", description), ("@r", region), ("@dp", e.Dept), ("@m", e.Manager)));
        await Audit(c, tx, s.Id, "request", no, "Create", null, new { employee = who, type, x.ReplacedAssetId, taNo }, null, token);
        await MailRequest(c, tx, id, "created", s.Id);
        await tx.CommitAsync(token);
        return new { id, requestNo = no };
    }

    /// <summary>The direct manager (or the backup on leave) approves the request before IT starts, or rejects it with a reason.</summary>
    public async Task DecideRequest(ItamUser user, int id, ItamDecision x, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        Require(await ManagerOf(c, tx, s, r, token), "Only the employee's direct manager or the IT Manager (or their backup during leave) decides this request.");
        if (r.Status != "WaitingRequestApproval") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        var reason = x.Approve ? null : Text(x.Reason, "Reason for the rejection", 500);
        await Exec(c, tx, x.Approve ? "UPDATE itam_data.request SET status=N'InReview',approved_by=@b,approved_at_utc=SYSUTCDATETIME() WHERE request_id=@i"
            : "UPDATE itam_data.request SET status=N'Rejected',rejected_by=@b,reject_reason=@r WHERE request_id=@i", token, ("@b", s.Id), ("@r", reason), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, x.Approve ? "Approve request" : "Reject request", null, new { reason }, null, token);
        await MailRequest(c, tx, id, x.Approve ? "approved" : "rejected", s.Id);
        await tx.CommitAsync(token);
    }

    // IT Assets Officer: the source ---------------------------------------------------------------------------------------------------------
    public async Task ReviewRequest(ItamUser user, int id, ItamRequestReview x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.Officer, "Only the IT Assets Officer reviews requests.");
        var r = await LoadRequest(c, tx, id, token);
        if (r.Status != "InReview") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        if (!string.IsNullOrWhiteSpace(x.RejectReason))
        {
            var reason = Text(x.RejectReason, "Reason", 500);
            await Exec(c, tx, "UPDATE itam_data.request SET status=N'Rejected',rejected_by=@b,reject_reason=@r WHERE request_id=@i", token, ("@b", s.Id), ("@r", reason), ("@i", id));
            await Audit(c, tx, s.Id, "request", r.No, "Reject (IT)", null, new { reason }, null, token);
            await MailRequest(c, tx, id, "rejected", s.Id);
            await tx.CommitAsync(token); return;
        }
        var source = x.Source is "Stock" or "NewDevice" or "Accessory" ? x.Source : throw new ArgumentException("Choose the source: from stock, new device or IT accessory.");
        string status = "InReview"; int? reserved = null; Guid? assigned = null;
        if (source is "Stock" or "Accessory")
        {
            var sp = (await Rows(c, tx, "SELECT e.display_name FROM itam_access.employee e WHERE e.employee_id=@e AND e.enabled=1 AND EXISTS(SELECT 1 FROM itam_access.role_assignment ra WHERE ra.employee_id=e.employee_id AND ra.role_code=N'SupportSpecialist')",
                q => q.GetString(0), token, ("@e", x.AssignTo ?? Guid.Empty))).SingleOrDefault() ?? throw new ArgumentException("Choose the IT Support Specialist.");
            assigned = x.AssignTo;
        }
        if (source == "Stock")
        {
            var a = (await Rows(c, tx, "SELECT asset_no,status FROM itam_data.asset WITH (UPDLOCK) WHERE asset_id=@a", q => (No: q.GetString(0), Status: q.GetString(1)), token, ("@a", x.ReservedAssetId ?? 0))).SingleOrDefault();
            if (a.No is null || a.Status != "InStore") throw new ArgumentException("Choose a device In Store.");
            if (await Scalar(c, tx, "SELECT request_no FROM itam_data.request WHERE reserved_asset_id=@a AND status=N'StockAssigned'", token, ("@a", x.ReservedAssetId)) is string other)
                throw new ArgumentException($"{a.No} is already reserved for {other}.");
            reserved = x.ReservedAssetId; status = "StockAssigned";
        }
        if (source == "Accessory") status = "PricingInRegion";
        await Exec(c, tx, "UPDATE itam_data.request SET source=@s,status=@st,reserved_asset_id=@a,assigned_to=@t,reviewed_by=@b,reviewed_at_utc=SYSUTCDATETIME() WHERE request_id=@i", token,
            ("@s", source), ("@st", status), ("@a", reserved), ("@t", assigned), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Source", null, new { source, reserved, assigned }, null, token);
        if (status is "StockAssigned" or "PricingInRegion") await MailRequest(c, tx, id, status == "StockAssigned" ? "stock" : "pricing", s.Id);
        await tx.CommitAsync(token);
    }

    /// <summary>The cost table: by the IT Assets Officer for a new device (supplier and quotation required), or by the assigned
    /// specialist for an IT accessory (shop required, quotation or price photo optional). Sent to the manager for approval.</summary>
    public async Task SubmitCost(ItamUser user, int id, ItamCostInput x, ItamUpload? quotation, CancellationToken token)
    {
        var qType = quotation is null ? null : CheckFile(quotation, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        if (r.Source == "NewDevice" && r.Status == "InReview") Require(s.Officer, "Only the IT Assets Officer prices a new device.");
        else if (r.Source == "Accessory" && r.Status == "PricingInRegion") Require(s.Id == r.AssignedTo || s.Officer, "Only the assigned IT Support Specialist prices the accessory.");
        else throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}: no cost table is due.");
        var lines = (x.Lines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Item)).ToList();
        if (lines.Count == 0) throw new ArgumentException("Add at least one line to the cost table.");
        int? supplier = null; string? shop = null;
        if (r.Source == "NewDevice")
        {
            if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s AND active=1", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
            if (quotation is null) throw new ArgumentException("Attach the supplier's quotation.");
            supplier = x.SupplierId;
        }
        else shop = Text(x.Shop, "Shop", 200);
        await Exec(c, tx, "DELETE itam_data.request_line WHERE request_id=@i", token, ("@i", id));
        var n = 0;
        foreach (var l in lines)
        {
            var qty = l.Quantity is >= 1 and <= 1000 ? l.Quantity.Value : throw new ArgumentException($"Line {n + 1}: quantity 1 to 1000.");
            var unit = l.UnitCost is decimal u && u >= 0 ? u : throw new ArgumentException($"Line {n + 1}: unit cost before VAT.");
            await Exec(c, tx, "INSERT itam_data.request_line(request_id,line_no,item,specification,quantity,unit_cost) VALUES(@r,@n,@it,@sp,@q,@u)", token,
                ("@r", id), ("@n", ++n), ("@it", Text(l.Item, $"Line {n}: item", 200)), ("@sp", Optional(l.Specification, $"Line {n}: specification", 300)), ("@q", qty), ("@u", unit));
        }
        var q = quotation is null ? null : (long?)await SaveFile(c, tx, quotation, qType!, s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.request SET status=N'WaitingCostApproval',supplier_id=@s,shop=@sh,quotation_file_id=COALESCE(@q,quotation_file_id),priced_by=@b,priced_at_utc=SYSUTCDATETIME() WHERE request_id=@i", token,
            ("@s", supplier), ("@sh", shop), ("@q", q), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Cost table", null, new { supplier, shop, lines = lines.Count }, null, token);
        await MailRequest(c, tx, id, r.Source == "Accessory" ? "cost-accessory" : "cost-new", s.Id);
        await tx.CommitAsync(token);
    }

    public async Task DecideCost(ItamUser user, int id, ItamDecision x, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        Require(await ManagerOf(c, tx, s, r, token), "Only the employee's direct manager or the IT Manager (or their backup during leave) approves the cost.");
        if (r.Status != "WaitingCostApproval") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        var reason = x.Approve ? null : Text(x.Reason, "Reason for the rejection", 500);
        var next = x.Approve ? r.Source == "Accessory" ? "CashPurchase" : "Approved" : "Rejected";
        await Exec(c, tx, x.Approve ? "UPDATE itam_data.request SET status=@st,cost_approved_by=@b,cost_approved_at_utc=SYSUTCDATETIME() WHERE request_id=@i"
            : "UPDATE itam_data.request SET status=@st,rejected_by=@b,reject_reason=@r WHERE request_id=@i", token, ("@st", next), ("@b", s.Id), ("@r", reason), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, x.Approve ? "Approve cost" : "Reject cost", null, new { reason }, null, token);
        await MailRequest(c, tx, id, x.Approve ? "cost-approved" : "cost-rejected", s.Id);
        await tx.CommitAsync(token);
    }

    public async Task MarkOrdered(ItamUser user, int id, ItamRequestOrdered x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.Officer, "Only the IT Assets Officer records the order.");
        var r = await LoadRequest(c, tx, id, token);
        if (r.Status != "Approved") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}: only an approved new device is ordered.");
        if (x.PoId is int po && await Scalar(c, tx, "SELECT 1 FROM itam_data.po WHERE po_id=@p", token, ("@p", po)) is null) throw new ArgumentException("The PO was not found.");
        await Exec(c, tx, "UPDATE itam_data.request SET status=N'Ordered',po_id=@p,ordered_at_utc=SYSUTCDATETIME() WHERE request_id=@i", token, ("@p", x.PoId), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Ordered", null, new { x.PoId }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>IT accessory bought with petty cash: shop, date, amount paid including VAT (not above the approved total) and the
    /// receipt; the request is fulfilled. No asset is registered.</summary>
    public async Task RecordCashPurchase(ItamUser user, int id, ItamCashPurchase x, ItamUpload? receipt, CancellationToken token)
    {
        if (receipt is null) throw new ArgumentException("Attach the receipt.");
        var type = CheckFile(receipt, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        Require(s.Id == r.AssignedTo || s.Officer, "Only the assigned IT Support Specialist records the purchase.");
        if (r.Status != "CashPurchase") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var on = x.PurchaseDate is DateOnly d && d <= today ? d : throw new ArgumentException("The purchase date: today or earlier.");
        var (_, _, grand) = await RequestTotals(c, tx, id, token);
        var amount = x.Amount is decimal a && a > 0 ? a : throw new ArgumentException("The amount paid including VAT.");
        if (amount > grand) throw new ArgumentException($"The amount paid cannot be above the approved total ({Money(grand)}).");
        var f = await SaveFile(c, tx, receipt, type, s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.request SET status=N'Fulfilled',shop=@sh,purchase_date=@d,paid_amount=@a,receipt_file_id=@f,fulfilled_at_utc=SYSUTCDATETIME(),fulfilled_by=@b WHERE request_id=@i", token,
            ("@sh", Text(x.Shop, "Shop", 200)), ("@d", DateParam(on)), ("@a", amount), ("@f", f), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Cash purchase", null, new { amount, on }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>A device request is fulfilled by the delivery note of the employee (the reserved device, or the new device
    /// received on its PO): the IT Assets Officer or the assigned specialist links it.</summary>
    public async Task FulfilRequest(ItamUser user, int id, ItamRequestFulfil x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        Require(s.Officer || s.Id == r.AssignedTo, "Only the IT Assets Officer or the assigned IT Support Specialist links the delivery.");
        if (r.Status is not ("StockAssigned" or "Ordered")) throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        var d = (await Rows(c, tx, "SELECT d.delivery_no,d.asset_id,d.employee_id,d.issued_at_utc FROM itam_data.delivery d WHERE d.delivery_id=@d",
            q => (No: q.GetString(0), Asset: q.GetInt32(1), Employee: q.GetGuid(2), At: Utc(q, 3)), token, ("@d", x.DeliveryId ?? 0))).SingleOrDefault();
        if (d.No is null || d.Employee != r.Employee) throw new ArgumentException("Choose the delivery note of the employee who asked.");
        if (d.At < r.RequestedAt) throw new ArgumentException($"{d.No} was issued before the request.");
        if (r.Status == "StockAssigned" && d.Asset != r.Reserved) throw new ArgumentException("Deliver the reserved device.");
        if (await Scalar(c, tx, "SELECT request_no FROM itam_data.request WHERE delivery_id=@d", token, ("@d", x.DeliveryId)) is string other) throw new ArgumentException($"{d.No} already fulfils {other}.");
        await Exec(c, tx, "UPDATE itam_data.request SET status=N'Fulfilled',delivery_id=@d,fulfilled_at_utc=SYSUTCDATETIME(),fulfilled_by=@b WHERE request_id=@i", token, ("@d", x.DeliveryId), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Fulfilled", null, new { delivery = d.No }, null, token);
        await MailRequest(c, tx, id, "fulfilled", s.Id);
        await tx.CommitAsync(token);
    }

    public async Task CancelRequest(ItamUser user, int id, ItamCancelInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = await LoadRequest(c, tx, id, token);
        var own = (s.Id == r.RequestedBy || s.Id == r.Employee) && r.Status == "WaitingRequestApproval";
        Require(own || (s.Officer && r.Status is not ("Fulfilled" or "Rejected" or "Cancelled")), "You cancel your own request while it waits for approval; the IT Assets Officer cancels later.");
        if (r.Status is "Fulfilled" or "Rejected" or "Cancelled") throw new ArgumentException($"{r.No} is {RequestStatusLabel(r.Status)}.");
        await Exec(c, tx, "UPDATE itam_data.request SET status=N'Cancelled',cancelled_reason=@r WHERE request_id=@i", token, ("@r", Optional(x.Reason, "Reason", 500)), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Cancel", null, new { x.Reason }, null, token);
        await tx.CommitAsync(token);
    }

    public async Task RequestAccounting(ItamUser user, int id, ItamRequestDate x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.Officer, "Only the IT Assets Officer records when the document went to Accounting.");
        var r = await LoadRequest(c, tx, id, token);
        if (r.Source is not ("NewDevice" or "Accessory") || r.Status is "WaitingRequestApproval" or "InReview" or "PricingInRegion" or "WaitingCostApproval" or "Rejected" or "Cancelled")
            throw new ArgumentException("Only an approved purchase request goes to Accounting.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.On ?? today;
        if (on > today) throw new ArgumentException("The date cannot be in the future.");
        await Exec(c, tx, "UPDATE itam_data.request SET accounting_sent_on=@d,accounting_sent_by=@b WHERE request_id=@i", token, ("@d", DateParam(on)), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "request", r.No, "Sent to Accounting", null, new { on }, null, token);
        await tx.CommitAsync(token);
    }

    static async Task<(decimal Net, decimal Vat, decimal Grand)> RequestTotals(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
    {
        var net = Convert.ToDecimal(await Scalar(c, tx, "SELECT ISNULL(SUM(quantity*unit_cost),0) FROM itam_data.request_line WHERE request_id=@i", token, ("@i", id)));
        var vat = decimal.Round(net * Vat, 2); return (net, vat, net + vat);
    }

    // Reading -----------------------------------------------------------------------------------------------------------------------------
    const string RequestSelect = "SELECT q.request_id,q.request_no,q.employee_id,e.display_name,e.account,q.request_type,ra.asset_no,q.description,q.status,q.source,q.requested_at_utc,rb.display_name," +
        "q.region_code,q.department,mg.display_name,ab.display_name,q.approved_at_utc,q.reject_reason,xb.display_name,q.assigned_to,ag.display_name,rv.asset_no,su.name_en,q.shop,pb.display_name,q.priced_at_utc," +
        "cb.display_name,q.cost_approved_at_utc,p.po_number,q.ordered_at_utc,q.paid_amount,q.purchase_date,dl.delivery_no,q.fulfilled_at_utc,q.accounting_sent_on,q.assessment_no,q.cancelled_reason,q.requested_by,q.manager_id," +
        "CASE WHEN q.assessment_file_id IS NULL THEN 0 ELSE 1 END,CASE WHEN q.quotation_file_id IS NULL THEN 0 ELSE 1 END,CASE WHEN q.receipt_file_id IS NULL THEN 0 ELSE 1 END,q.reserved_asset_id,q.delivery_id," +
        "(SELECT ISNULL(SUM(l.quantity*l.unit_cost),0) FROM itam_data.request_line l WHERE l.request_id=q.request_id) " +
        "FROM itam_data.request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.asset ra ON ra.asset_id=q.replaced_asset_id LEFT JOIN itam_access.employee rb ON rb.employee_id=q.requested_by " +
        "LEFT JOIN itam_access.employee mg ON mg.employee_id=q.manager_id LEFT JOIN itam_access.employee ab ON ab.employee_id=q.approved_by LEFT JOIN itam_access.employee xb ON xb.employee_id=q.rejected_by " +
        "LEFT JOIN itam_access.employee ag ON ag.employee_id=q.assigned_to LEFT JOIN itam_data.asset rv ON rv.asset_id=q.reserved_asset_id LEFT JOIN itam_ref.supplier su ON su.supplier_id=q.supplier_id " +
        "LEFT JOIN itam_access.employee pb ON pb.employee_id=q.priced_by LEFT JOIN itam_access.employee cb ON cb.employee_id=q.cost_approved_by LEFT JOIN itam_data.po p ON p.po_id=q.po_id " +
        "LEFT JOIN itam_data.delivery dl ON dl.delivery_id=q.delivery_id ";
    static object MapRequest(DbDataReader r)
    {
        var net = r.GetDecimal(44); var vat = decimal.Round(net * Vat, 2);
        DateTime? T(int i) => r.IsDBNull(i) ? null : Utc(r, i);
        return new
        {
            id = r.GetInt32(0), no = r.GetString(1), employeeId = r.GetGuid(2), employee = r.GetString(3), username = r.GetString(4), type = r.GetString(5), replaced = S(r, 6), description = r.GetString(7),
            status = r.GetString(8), statusLabel = RequestStatusLabel(r.GetString(8)), source = S(r, 9), sourceLabel = SourceLabel(S(r, 9)), requestedAt = Utc(r, 10), requestedBy = S(r, 11), region = S(r, 12), department = S(r, 13),
            manager = S(r, 14), approvedBy = S(r, 15), approvedAt = T(16), rejectReason = S(r, 17), rejectedBy = S(r, 18), assignedToId = r.IsDBNull(19) ? (Guid?)null : r.GetGuid(19), assignedTo = S(r, 20),
            reserved = S(r, 21), supplier = S(r, 22), shop = S(r, 23), pricedBy = S(r, 24), pricedAt = T(25), costApprovedBy = S(r, 26), costApprovedAt = T(27), po = S(r, 28), orderedAt = T(29),
            paidAmount = r.IsDBNull(30) ? (decimal?)null : r.GetDecimal(30), purchaseDate = Day(r, 31), deliveryNo = S(r, 32), fulfilledAt = T(33), accountingSentOn = Day(r, 34), assessmentNo = S(r, 35),
            cancelledReason = S(r, 36), requestedById = r.GetGuid(37), managerId = r.IsDBNull(38) ? (Guid?)null : r.GetGuid(38), hasAssessmentFile = r.GetInt32(39) == 1, hasQuotation = r.GetInt32(40) == 1,
            hasReceipt = r.GetInt32(41) == 1, reservedId = I(r, 42), deliveryId = I(r, 43), net, vat, grand = net + vat,
            waitingSince = r.GetString(8) == "WaitingRequestApproval" ? Utc(r, 10) : r.GetString(8) == "WaitingCostApproval" && !r.IsDBNull(25) ? Utc(r, 25) : (DateTime?)null
        };
    }

    /// <summary>Who may see a request: the employee, the requester, the manager (or backup), the assigned specialist and IT.</summary>
    async Task<bool> SeesRequest(DbConnection c, ItamScope s, ReqRow r, CancellationToken token)
        => s.Id == r.Employee || s.Id == r.RequestedBy || s.Id == r.AssignedTo || s.Officer || s.ViewsAll || (s.Specialist && r.Region is string g && s.Regions.Contains(g))
           || r.Manager is Guid m && await ActsFor(c, null, s.Id, m, token);

    public async Task<object> Request(ItamUser user, int id, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var r = await LoadRequest(c, null, id, token);
        Require(await SeesRequest(c, s, r, token), "You cannot see this request.");
        var q = (await Rows(c, null, RequestSelect + "WHERE q.request_id=@i", MapRequest, token, ("@i", id))).Single();
        var lines = await Rows(c, null, "SELECT line_no,item,specification,quantity,unit_cost FROM itam_data.request_line WHERE request_id=@i ORDER BY line_no",
            x => new { no = x.GetInt32(0), item = x.GetString(1), specification = S(x, 2), quantity = x.GetInt32(3), unitCost = x.GetDecimal(4), total = x.GetInt32(3) * x.GetDecimal(4) }, token, ("@i", id));
        var manager = await ManagerOf(c, null, s, r, token);
        var assigned = s.Id == r.AssignedTo;
        // What the officer needs: devices In Store (not reserved), specialists, POs; and the employee's deliveries since the request.
        var stock = s.Officer && r.Status == "InReview" ? await Rows(c, null, "SELECT TOP 300 a.asset_id,a.asset_no,t.name_en,CONCAT(m.name_en,' ',mo.name_en),a.serial_no,a.service_tag,g.name_en,g.region_code FROM itam_data.asset a " +
            "JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "WHERE a.status=N'InStore' AND a.type_code NOT IN(N'SOF',N'TON') AND NOT EXISTS(SELECT 1 FROM itam_data.request x WHERE x.reserved_asset_id=a.asset_id AND x.status=N'StockAssigned') ORDER BY a.asset_no",
            x => new { id = x.GetInt32(0), assetNo = x.GetString(1), type = x.GetString(2), model = S(x, 3)?.Trim(), serial = S(x, 4) ?? S(x, 5), keptAt = x.GetString(6), region = x.GetString(7) }, token) : [];
        var specialists = s.Officer && r.Status == "InReview" ? await Rows(c, null, "SELECT e.employee_id,e.display_name,STRING_AGG(ra.region_code,', ') FROM itam_access.role_assignment ra JOIN itam_access.employee e ON e.employee_id=ra.employee_id " +
            "WHERE ra.role_code=N'SupportSpecialist' AND e.enabled=1 GROUP BY e.employee_id,e.display_name ORDER BY e.display_name", x => new { id = x.GetGuid(0), name = x.GetString(1), regions = S(x, 2) }, token) : [];
        var deliveries = (s.Officer || assigned) && r.Status is "StockAssigned" or "Ordered" ? await Rows(c, null, "SELECT d.delivery_id,d.delivery_no,a.asset_no,d.delivered_on FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id " +
            "WHERE d.employee_id=@e AND d.issued_at_utc>=@at AND NOT EXISTS(SELECT 1 FROM itam_data.request x WHERE x.delivery_id=d.delivery_id) ORDER BY d.delivery_id DESC",
            x => new { id = x.GetInt32(0), no = x.GetString(1), assetNo = x.GetString(2), deliveredOn = Day(x, 3) }, token, ("@e", r.Employee), ("@at", r.RequestedAt)) : [];
        var pos = s.Officer && r.Status == "Approved" ? await Rows(c, null, "SELECT TOP 100 po_id,po_number FROM itam_data.po ORDER BY po_date DESC", x => new { id = x.GetInt32(0), no = x.GetString(1) }, token) : [];
        var st = r.Status;
        return new
        {
            request = q, lines, stock, specialists, deliveries, pos, approvalDays = ApprovalDays, employeeAd = await EmployeeAd(c, null, r.Employee, token),
            can = new
            {
                decideRequest = manager && st == "WaitingRequestApproval", decideCost = manager && st == "WaitingCostApproval",
                review = s.Officer && st == "InReview" && r.Source is null, priceNewDevice = s.Officer && st == "InReview" && r.Source == "NewDevice",
                priceAccessory = (assigned || s.Officer) && st == "PricingInRegion", ordered = s.Officer && st == "Approved", cash = (assigned || s.Officer) && st == "CashPurchase",
                fulfil = (s.Officer || assigned) && st is "StockAssigned" or "Ordered",
                cancel = ((s.Id == r.RequestedBy || s.Id == r.Employee) && st == "WaitingRequestApproval") || (s.Officer && st is not ("Fulfilled" or "Rejected" or "Cancelled")),
                accounting = s.Officer && r.Source is "NewDevice" or "Accessory" && st is "Approved" or "Ordered" or "CashPurchase" or "Fulfilled",
                document = r.Source is "NewDevice" or "Accessory" && st is "Approved" or "Ordered" or "CashPurchase" or "Fulfilled" or "WaitingCostApproval"
            }
        };
    }

    public async Task<object> Requests(ItamUser user, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.Officer || s.ViewsAll || s.Specialist, "You cannot see the requests list.");
        var all = await Rows(c, null, RequestSelect + "ORDER BY q.request_id DESC", MapRequest, token);
        var visible = s.Officer || s.ViewsAll ? all : all.Where(x => ((dynamic)x).assignedToId == s.Id || (((dynamic)x).region is string g && s.Regions.Contains(g))).ToList();
        return new { requests = visible, isOfficer = s.Officer, approvalDays = ApprovalDays };
    }

    public async Task<ItamUpload> RequestFile(ItamUser user, int id, string which, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var r = await LoadRequest(c, null, id, token);
        Require(await SeesRequest(c, s, r, token), "You cannot see this request.");
        var col = which switch { "assessment" => "assessment_file_id", "quotation" => "quotation_file_id", "receipt" => "receipt_file_id", _ => throw new ArgumentException("Unknown file.") };
        var f = await Scalar(c, null, $"SELECT {col} FROM itam_data.request WHERE request_id=@i", token, ("@i", id)) as long? ?? throw new ArgumentException("No file is attached.");
        return await LoadFile(c, null, f, token) ?? throw new ArgumentException("The file was not found.");
    }

    /// <summary>My IT Assets: my assets and documents, my requests, what waits for my decision, and my team (managers).</summary>
    public async Task<object> MyItAssets(ItamUser user, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        async Task<List<object>> Held(Guid who) => await Rows(c, null, "SELECT d.delivery_id,a.asset_no,t.name_en,CONCAT(m.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),d.computer_name,d.delivered_on,d.delivery_no," +
            "CASE WHEN d.signed_file_id IS NULL THEN 0 ELSE 1 END,a.asset_id FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id WHERE d.employee_id=@e AND d.received_at_utc IS NULL ORDER BY d.delivered_on DESC",
            r => (object)new { deliveryId = r.GetInt32(0), assetNo = r.GetString(1), type = r.GetString(2), model = S(r, 3)?.Trim(), serial = S(r, 4), computerName = S(r, 5), deliveredOn = Day(r, 6), deliveryNo = r.GetString(7), signed = r.GetInt32(8) == 1, assetId = r.GetInt32(9) },
            token, ("@e", who));
        var assets = await Held(s.Id);
        var documents = await Rows(c, null, "SELECT d.delivery_id,d.delivery_no,a.asset_no,d.delivered_on,d.received_at_utc FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id WHERE d.employee_id=@e ORDER BY d.delivery_id DESC",
            r => new { deliveryId = r.GetInt32(0), deliveryNo = r.GetString(1), assetNo = r.GetString(2), deliveredOn = Day(r, 3), receivedAt = r.IsDBNull(4) ? (DateTime?)null : Utc(r, 4) }, token, ("@e", s.Id));
        var mine = await Rows(c, null, RequestSelect + "WHERE q.employee_id=@e OR q.requested_by=@e ORDER BY q.request_id DESC", MapRequest, token, ("@e", s.Id));
        var waiting = new List<object>();
        foreach (var w in await Rows(c, null, RequestSelect + "WHERE q.status IN(N'WaitingRequestApproval',N'WaitingCostApproval') ORDER BY q.request_id", r => (Row: MapRequest(r), Id: r.GetInt32(0)), token))
        {
            var r = await LoadRequest(c, null, w.Id, token);
            if (await ManagerOf(c, null, s, r, token)) waiting.Add(w.Row);
        }
        var reports = await Rows(c, null, "SELECT employee_id,display_name,job_title FROM itam_access.employee WHERE manager_id=@m AND enabled=1 ORDER BY display_name", r => (Id: r.GetGuid(0), Name: r.GetString(1), Title: S(r, 2)), token, ("@m", s.Id));
        var team = new List<object>();
        foreach (var p in reports)
        {
            var open = (await Rows(c, null, RequestSelect + "WHERE q.employee_id=@e AND q.status NOT IN(N'Fulfilled',N'Rejected',N'Cancelled') ORDER BY q.request_id DESC", MapRequest, token, ("@e", p.Id)));
            var notSigned = Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.delivery WHERE employee_id=@e AND received_at_utc IS NULL AND signed_file_id IS NULL", token, ("@e", p.Id)));
            team.Add(new { id = p.Id, name = p.Name, title = p.Title, assets = await Held(p.Id), requests = open, notSigned });
        }
        var notSignedMine = assets.Count(a => !((dynamic)a).signed);
        var movements = await Rows(c, null, MoveSelect + "WHERE m.requested_by=@e OR m.employee_id=@e OR m.sender_id=@e OR m.receiver_id=@e ORDER BY m.movement_id DESC", MapMove, token, ("@e", s.Id));
        return new { me = s.Id, assets, documents, requests = mine, waiting, team, notSigned = notSignedMine, inkApprovals = await PendingApprovals(c, s, token), approvalDays = ApprovalDays, movements, purposes = MovementPurposes };
    }

    internal static async Task<int> RequestApprovalsCount(DbConnection c, ItamScope s, CancellationToken token)
    {
        var n = 0;
        foreach (var id in await Rows(c, null, "SELECT request_id FROM itam_data.request WHERE status IN(N'WaitingRequestApproval',N'WaitingCostApproval')", r => r.GetInt32(0), token))
            if (await ManagerOf(c, null, s, await LoadRequest(c, null, id, token), token)) n++;
        return n;
    }

    /// <summary>The request by its number (the QR code on the request document opens /r/PR-n).</summary>
    public async Task<object> RequestByNo(ItamUser user, string? no, CancellationToken token)
    {
        await using var c = await Open(token);
        await Scope(c, null, user, token);
        var id = await Scalar(c, null, "SELECT request_id FROM itam_data.request WHERE request_no=@n", token, ("@n", (no ?? "").Trim().ToUpperInvariant())) as int? ?? throw new ArgumentException("The request was not found.");
        return new { id };
    }

    // Approval deadline (owner, 22 October 2026): the direct manager decides within ApprovalDays, counted from when the request
    // (or the cost table) reached the manager; otherwise the request is cancelled automatically.
    public int ApprovalDays { get; init; } = 3;
    const string ExpiredReason = "Cancelled automatically: the manager did not decide within the approval time";

    /// <summary>Cancels every request whose manager decision is overdue; returns how many. Run by the background timer and at the
    /// start of every request page and decision, so an overdue request is never approved late.</summary>
    public async Task<int> ExpireRequests(CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var limit = UtcNow.AddDays(-ApprovalDays);
        var due = await Rows(c, tx, "SELECT request_id,request_no,status FROM itam_data.request WITH (UPDLOCK) WHERE (status=N'WaitingRequestApproval' AND requested_at_utc<@l) OR (status=N'WaitingCostApproval' AND priced_at_utc<@l)",
            r => (Id: r.GetInt32(0), No: r.GetString(1), Status: r.GetString(2)), token, ("@l", limit));
        foreach (var r in due)
        {
            await Exec(c, tx, "UPDATE itam_data.request SET status=N'Cancelled',cancelled_reason=@r,reserved_asset_id=NULL WHERE request_id=@i", token,
                ("@r", $"{ExpiredReason} ({ApprovalDays} days, {(r.Status == "WaitingCostApproval" ? "cost approval" : "request approval")})."), ("@i", r.Id));
            await Audit(c, tx, Guid.Empty, "request", r.No, "Expired", null, new { r.Status, days = ApprovalDays }, "Not decided within the approval time", token);
        }
        foreach (var r in due) await MailRequest(c, tx, r.Id, "expired", Guid.Empty);
        await tx.CommitAsync(token);
        return due.Count;
    }
    async Task Expire(CancellationToken token) { if (await ExpireCheck(token)) await ExpireRequests(token); }
    async Task<bool> ExpireCheck(CancellationToken token)
    {
        await using var c = await Open(token);
        return await Scalar(c, null, "SELECT TOP 1 1 FROM itam_data.request WHERE (status=N'WaitingRequestApproval' AND requested_at_utc<@l) OR (status=N'WaitingCostApproval' AND priced_at_utc<@l)", token, ("@l", UtcNow.AddDays(-ApprovalDays))) is not null;
    }
}
