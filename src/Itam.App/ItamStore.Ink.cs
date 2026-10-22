using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamInkRequestInput(int? PrinterId, Guid? EmployeeId, int? Quantity, string? Toner, string? Note,
    string? NewSerial = null, int? NewModelId = null, string? NewModel = null);
public sealed record ItamInkPrinter(int? PrinterId);
public sealed record ItamInkRegisterPrinter(int? LocationId, int? ManufacturerId, int? ModelId, string? NewModel, string? SerialNo, string? TonerCode, string? PrinterLocation, string? Condition);
public sealed record ItamDecision(bool Approve, string? Reason);
public sealed record ItamOrderInput(int? SupplierId, int[]? RequestIds, string? Note);
public sealed record ItamCancelInput(string? Reason);

/// <summary>Build step 3a, ink (forms 10 Ink Request and 11 Ink Delivery, as the owner's toner process): the employee asks for
/// ink for an active printer, the ink officer sends it to the approving manager, that manager (or the manager's backup on
/// leave) approves, IT delivers from the shelf or
/// orders from a supplier; prices come from the supplier invoice. No toner stock list. The ink officer chooses the approving
/// manager (a senior manager, not the Active Directory manager; owner, 22 October 2026).</summary>
public sealed partial class ItamStore
{
    static string InkStatusLabel(string s) => s switch { "Submitted" => "Waiting for the Ink Officer", "WaitingApproval" => "Waiting manager approval", "Ordered" => "Ordered from supplier", _ => s };

    /// <summary>Printers that can get ink: In Use, found by Asset No., serial number or toner code (typed or scanned from the label).</summary>
    public async Task<object> Printers(ItamUser user, string? q, CancellationToken token)
    {
        await using var c = await Open(token);
        await Scope(c, null, user, token);
        var t = (q ?? "").Trim(); if (t.Length < 2) return Array.Empty<object>();
        var like = "%" + t.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        return await Rows(c, null, "SELECT TOP 20 a.asset_id,a.asset_no,m.name_en,mo.name_en,a.serial_no,g.name_en,g.region_code,tc.value,pl.value " +
            "FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_data.asset_attribute tc ON tc.asset_id=a.asset_id AND tc.attr_code=N'TonerCode' LEFT JOIN itam_data.asset_attribute pl ON pl.asset_id=a.asset_id AND pl.attr_code=N'PrinterLocation' " +
            "WHERE a.type_code=N'PNT' AND a.status=N'InUse' AND (a.asset_no LIKE @lk OR a.serial_key LIKE @k OR tc.value LIKE @lk) ORDER BY a.asset_no",
            r => new { id = r.GetInt32(0), assetNo = r.GetString(1), model = string.Join(" ", new[] { S(r, 2), S(r, 3) }.Where(v => v is not null)), serial = S(r, 4), keptAt = r.GetString(5), region = r.GetString(6), toner = S(r, 7), printerLocation = S(r, 8) },
            token, ("@lk", like), ("@k", "%" + (Key(t) ?? "~") + "%"));
    }

    /// <summary>Ask for ink (INK-n): for yourself, or for an employee by IT (or by the employee's manager). The request waits for
    /// the ink officer, who sends it to the approving manager.</summary>
    public async Task<object> CreateInkRequest(ItamUser user, ItamInkRequestInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var who = x.EmployeeId ?? s.Id;
        var emp = (await Rows(c, tx, "SELECT display_name,manager_id,enabled FROM itam_access.employee WHERE employee_id=@e", r => (Name: r.GetString(0), Manager: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), Enabled: r.GetBoolean(2)), token, ("@e", who))).SingleOrDefault();
        if (emp.Name is null) throw new ArgumentException("Choose the employee.");
        if (who != s.Id) Require(s.InkForOthers || emp.Manager == s.Id, "You can ask for ink for yourself; IT and the employee's manager can ask for an employee.");
        if (!emp.Enabled) throw new ArgumentException($"{emp.Name} is disabled in Active Directory.");
        // A printer not in IT Assets: the serial number and model are required; a serial number already registered is linked.
        string? newSerial = null, newModel = null, location = null, adRegion = null; int? newModelId = null; var printerId = x.PrinterId;
        if (printerId is null)
        {
            newSerial = Text(x.NewSerial, "Serial number of the printer", 100);
            if (await Scalar(c, tx, "SELECT asset_id FROM itam_data.asset WHERE type_code=N'PNT' AND status=N'InUse' AND serial_key=@k", token, ("@k", Key(newSerial) ?? "~")) is int found) { printerId = found; newSerial = null; }
            else
            {
                if (x.NewModelId is int m)
                    newModelId = await Scalar(c, tx, "SELECT model_id FROM itam_ref.model WHERE model_id=@m AND type_code=N'PNT' AND active=1", token, ("@m", m)) as int? ?? throw new ArgumentException("Choose the printer model.");
                else newModel = Text(x.NewModel, "Printer model", 100);
                (location, adRegion) = (await Rows(c, tx, "SELECT branch,region FROM itam_access.employee WHERE employee_id=@e", r => (S(r, 0), S(r, 1)), token, ("@e", who))).Single();
            }
        }
        string? printerNo = null, region, defaultToner = null;
        if (printerId is not null)
        {
            var p = (await Rows(c, tx, "SELECT a.asset_no,a.type_code,a.status,g.region_code,(SELECT value FROM itam_data.asset_attribute WHERE asset_id=a.asset_id AND attr_code=N'TonerCode') FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@a",
                r => (No: r.GetString(0), Type: r.GetString(1), Status: r.GetString(2), Region: r.GetString(3), Toner: S(r, 4)), token, ("@a", printerId))).SingleOrDefault();
            if (p.No is null || p.Type != "PNT") throw new ArgumentException("Choose the printer (its Asset No. is on the label).");
            if (p.Status != "InUse") throw new ArgumentException($"{p.No} is not an active printer ({ItamStatuses.Label(p.Status)}).");
            (printerNo, region, defaultToner) = (p.No, p.Region, p.Toner);
        }
        // The region comes from the employee's region in Active Directory (matched to a region code or name); the office is the
        // printer location.
        else region = await Scalar(c, tx, "SELECT TOP 1 region_code FROM itam_ref.region WHERE (region_code=@r OR name_en=@r) AND active=1", token, ("@r", adRegion ?? "")) as string;
        var toner = Optional(x.Toner, "Toner", 100) ?? defaultToner ?? throw new ArgumentException("Type the toner code (it is printed on the cartridge).");
        var qty = x.Quantity is >= 1 and <= 50 ? x.Quantity.Value : throw new ArgumentException("Quantity: 1 to 50.");
        var no = $"INK-{await Next(c, tx, "InkRequest", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.ink_request(request_no,printer_asset_id,new_serial,new_model_id,new_model,employee_location,employee_id,requested_by,toner,quantity,note,region_code,status,manager_id) " +
            "OUTPUT inserted.request_id VALUES(@n,@p,@ns,@nm,@nt,@el,@e,@b,@t,@q,@x,@r,N'Submitted',NULL)", token,
            ("@n", no), ("@p", printerId), ("@ns", newSerial), ("@nm", newModelId), ("@nt", newModel), ("@el", location), ("@e", who), ("@b", s.Id), ("@t", toner), ("@q", qty), ("@x", Optional(x.Note, "Note", 500)), ("@r", region)));
        await Audit(c, tx, s.Id, "ink", no, "Request", null, new { printer = printerNo, newSerial, newModelId, newModel, location, toner, qty, employee = who }, null, token);
        await tx.CommitAsync(token);
        return new { id, requestNo = no };
    }

    public sealed record ItamSendForApproval(Guid? ApproverId);

    /// <summary>The ink officer (IT of the printer's region) sends the request to the manager who approves it: a senior manager
    /// chosen by IT, not the Active Directory manager (owner, 22 October 2026). Can be re-sent to another manager while it waits.</summary>
    public async Task SendInkForApproval(ItamUser user, int requestId, ItamSendForApproval x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = (await Rows(c, tx, "SELECT request_no,status,region_code,employee_id,printer_asset_id FROM itam_data.ink_request WITH (UPDLOCK) WHERE request_id=@i",
            q => (No: q.GetString(0), Status: q.GetString(1), Region: S(q, 2), Employee: q.GetGuid(3), Printer: I(q, 4)), token, ("@i", requestId))).SingleOrDefault();
        if (r.No is null) throw new ArgumentException("The ink request was not found.");
        Require(s.CanInkSend(r.Region), "You cannot send this ink request for approval.");
        if (r.Status is not ("Submitted" or "WaitingApproval")) throw new ArgumentException($"{r.No} is {InkStatusLabel(r.Status)}.");
        if (r.Printer is null) throw new ArgumentException($"{r.No}: the printer is not in IT Assets yet. Link it to its Asset No. or register it first.");
        var a = (await Rows(c, tx, "SELECT display_name,enabled FROM itam_access.employee WHERE employee_id=@e", q => (Name: q.GetString(0), Enabled: q.GetBoolean(1)), token, ("@e", x.ApproverId ?? Guid.Empty))).SingleOrDefault();
        if (a.Name is null) throw new ArgumentException("Choose the manager who approves.");
        if (!a.Enabled) throw new ArgumentException($"{a.Name} is disabled in Active Directory.");
        if (x.ApproverId == r.Employee) throw new ArgumentException("The employee cannot approve their own request.");
        await Exec(c, tx, "UPDATE itam_data.ink_request SET status=N'WaitingApproval',manager_id=@m,sent_by=@b,sent_at_utc=SYSUTCDATETIME() WHERE request_id=@i", token, ("@m", x.ApproverId), ("@b", s.Id), ("@i", requestId));
        await Audit(c, tx, s.Id, "ink", r.No, "Send for approval", null, new { approver = a.Name }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>The Ink Officer links a request for a printer not in IT Assets to the printer's Asset No.; the request takes the
    /// printer's region.</summary>
    public async Task LinkInkPrinter(ItamUser user, int requestId, ItamInkPrinter x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var no = await UnregisteredInk(c, tx, s, requestId, token);
        var p = (await Rows(c, tx, "SELECT a.asset_no,a.type_code,a.status,g.region_code FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@a",
            r => (No: r.GetString(0), Type: r.GetString(1), Status: r.GetString(2), Region: r.GetString(3)), token, ("@a", x.PrinterId ?? 0))).SingleOrDefault();
        if (p.No is null || p.Type != "PNT") throw new ArgumentException("Choose the printer.");
        if (p.Status != "InUse") throw new ArgumentException($"{p.No} is not an active printer ({ItamStatuses.Label(p.Status)}).");
        await Exec(c, tx, "UPDATE itam_data.ink_request SET printer_asset_id=@p,region_code=@r WHERE request_id=@i", token, ("@p", x.PrinterId), ("@r", p.Region), ("@i", requestId));
        await Audit(c, tx, s.Id, "ink", no, "Link printer", null, new { printer = p.No }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>The Ink Officer registers the printer of a request (old stock, no PO) and links it: the printer is In Use where
    /// it is kept. Returns the new Asset No. so its label can be printed.</summary>
    public async Task<object> RegisterInkPrinter(ItamUser user, int requestId, ItamInkRegisterPrinter x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var no = await UnregisteredInk(c, tx, s, requestId, token);
        var input = new ItamAssetInput("PNT", x.LocationId, x.ManufacturerId, x.ModelId, x.NewModel, x.SerialNo, null, null, null, x.Condition ?? "Used", null, null, null, null, null,
            $"Registered from ink request {no}", new() { ["TonerCode"] = x.TonerCode, ["PrinterLocation"] = x.PrinterLocation }, null, null);
        var d = await Check(c, tx, s, input, "PNT", null, token);
        Require(s.CanInkPrinter(d.Region), "Register the printer in a branch or IT warehouse of your regions.");
        var (id, assetNo) = await Create(c, tx, s.Id, d, null, null, null, null, null, $"Registered from ink request {no} (old stock, no PO)", token);
        await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InUse' WHERE asset_id=@a", token, ("@a", id));
        await Event(c, tx, id, s.Id, "In use", "In use where it is kept (printer found through an ink request)", token);
        await Exec(c, tx, "UPDATE itam_data.ink_request SET printer_asset_id=@p,region_code=@r WHERE request_id=@i", token, ("@p", id), ("@r", d.Region), ("@i", requestId));
        await Audit(c, tx, s.Id, "asset", assetNo, "Register", null, new { d.Type, d.LocationId, d.Serial, inkRequest = no }, null, token);
        await Audit(c, tx, s.Id, "ink", no, "Link printer", null, new { printer = assetNo }, null, token);
        await tx.CommitAsync(token);
        return new { id, assetNo };
    }

    async Task<string> UnregisteredInk(DbConnection c, DbTransaction tx, ItamScope s, int requestId, CancellationToken token)
    {
        var r = (await Rows(c, tx, "SELECT request_no,status,region_code,printer_asset_id FROM itam_data.ink_request WITH (UPDLOCK) WHERE request_id=@i",
            q => (No: q.GetString(0), Status: q.GetString(1), Region: S(q, 2), Printer: I(q, 3)), token, ("@i", requestId))).SingleOrDefault();
        if (r.No is null) throw new ArgumentException("The ink request was not found.");
        Require(s.CanInkPrinter(r.Region), "You cannot link or register this printer.");
        if (r.Printer is not null) throw new ArgumentException($"{r.No} is already linked to its printer.");
        if (r.Status != "Submitted") throw new ArgumentException($"{r.No} is {InkStatusLabel(r.Status)}.");
        return r.No;
    }

    /// <summary>The direct manager, or the manager's backup during leave, approves or rejects (a rejection needs a reason);
    /// nobody approves their own request.</summary>
    public async Task DecideInk(ItamUser user, int requestId, ItamDecision x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = (await Rows(c, tx, "SELECT request_no,status,manager_id,employee_id FROM itam_data.ink_request WITH (UPDLOCK) WHERE request_id=@i",
            q => (No: q.GetString(0), Status: q.GetString(1), Manager: q.IsDBNull(2) ? (Guid?)null : q.GetGuid(2), Employee: q.GetGuid(3)), token, ("@i", requestId))).SingleOrDefault();
        if (r.No is null) throw new ArgumentException("The ink request was not found.");
        Require(r.Manager is Guid m && await ActsFor(c, tx, s.Id, m, token), "Only the manager the request was sent to, or that manager's backup during leave, decides this request.");
        Require(r.Employee != s.Id, "You cannot approve your own request.");
        if (r.Status != "WaitingApproval") throw new ArgumentException($"{r.No} is already {InkStatusLabel(r.Status)}.");
        var reason = x.Approve ? null : Text(x.Reason, "Reason for the rejection", 500);
        await Exec(c, tx, "UPDATE itam_data.ink_request SET status=@s,decided_by=@b,decided_at_utc=SYSUTCDATETIME(),reject_reason=@r WHERE request_id=@i", token,
            ("@s", x.Approve ? "Approved" : "Rejected"), ("@b", s.Id), ("@r", reason), ("@i", requestId));
        await Audit(c, tx, s.Id, "ink", r.No, x.Approve ? "Approve" : "Reject", null, new { reason }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>Ink requests waiting for the person's decision (as the direct manager, or as a manager's backup on leave).</summary>
    internal static async Task<int> PendingApprovals(DbConnection c, ItamScope s, CancellationToken token)
        => Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.ink_request r WHERE r.status=N'WaitingApproval' AND r.employee_id<>@me AND (r.manager_id=@me OR EXISTS(SELECT 1 FROM itam_access.leave_backup b " +
            "WHERE b.person_id=r.manager_id AND b.backup_employee_id=@me AND b.cancelled_at_utc IS NULL AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to))", token, ("@me", s.Id)));

    /// <summary>True when the person is the manager, or the manager's backup on an active leave today.</summary>
    static async Task<bool> ActsFor(DbConnection c, DbTransaction? tx, Guid person, Guid manager, CancellationToken token)
        => person == manager || await Scalar(c, tx, "SELECT 1 FROM itam_access.leave_backup WHERE person_id=@m AND backup_employee_id=@p AND cancelled_at_utc IS NULL " +
            "AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN leave_from AND leave_to", token, ("@m", manager), ("@p", person)) is not null;

    /// <summary>Cancel: the requester while it waits for approval; IT (the region's handlers) before delivery.</summary>
    public async Task CancelInk(ItamUser user, int requestId, ItamCancelInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = (await Rows(c, tx, "SELECT request_no,status,requested_by,employee_id,region_code FROM itam_data.ink_request WITH (UPDLOCK) WHERE request_id=@i",
            q => (No: q.GetString(0), Status: q.GetString(1), By: q.GetGuid(2), Employee: q.GetGuid(3), Region: S(q, 4)), token, ("@i", requestId))).SingleOrDefault();
        if (r.No is null) throw new ArgumentException("The ink request was not found.");
        var own = (r.By == s.Id || r.Employee == s.Id) && r.Status is "Submitted" or "WaitingApproval";
        Require(own || s.CanInk(r.Region), "You cannot cancel this request.");
        if (r.Status is "Delivered" or "Rejected" or "Cancelled") throw new ArgumentException($"{r.No} is {InkStatusLabel(r.Status)}.");
        await Exec(c, tx, "UPDATE itam_data.ink_request SET status=N'Cancelled',cancelled_reason=@r,order_id=NULL WHERE request_id=@i", token, ("@r", Optional(x.Reason, "Reason", 500)), ("@i", requestId));
        await Audit(c, tx, s.Id, "ink", r.No, "Cancel", null, new { x.Reason }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>Group approved requests of one region into a supplier order (today's order email, ORD-n).</summary>
    public async Task<object> CreateOrder(ItamUser user, ItamOrderInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s AND active=1", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
        var ids = (x.RequestIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Choose the approved requests to order.");
        var reqs = new List<(string No, string Status, string Region)>();
        foreach (var id in ids)
            reqs.Add((await Rows(c, tx, "SELECT request_no,status,region_code FROM itam_data.ink_request WITH (UPDLOCK) WHERE request_id=@i", r => (r.GetString(0), r.GetString(1), r.GetString(2)), token, ("@i", id))).SingleOrDefault());
        if (reqs.Any(r => r.No is null)) throw new ArgumentException("A request was not found.");
        if (reqs.FirstOrDefault(r => r.Status != "Approved") is { No: not null } bad) throw new ArgumentException($"{bad.No} is {InkStatusLabel(bad.Status)}: only approved requests are ordered.");
        if (reqs.Select(r => r.Region).Distinct().Count() > 1) throw new ArgumentException("One order holds the requests of one region.");
        Require(s.CanInk(reqs[0].Region), "Only the Ink Officer of the region orders its ink.");
        var no = $"ORD-{await Next(c, tx, "SupplierOrder", token)}";
        var order = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.supplier_order(order_no,supplier_id,region_code,note,created_by) OUTPUT inserted.order_id VALUES(@n,@s,@r,@x,@b)", token,
            ("@n", no), ("@s", x.SupplierId), ("@r", reqs[0].Region), ("@x", Optional(x.Note, "Note", 500)), ("@b", s.Id)));
        foreach (var id in ids) await Exec(c, tx, "UPDATE itam_data.ink_request SET status=N'Ordered',order_id=@o WHERE request_id=@i", token, ("@o", order), ("@i", id));
        await Audit(c, tx, s.Id, "order", no, "Create", null, new { x.SupplierId, requests = reqs.Select(r => r.No) }, null, token);
        await tx.CommitAsync(token);
        return new { id = order, orderNo = no };
    }

    sealed record InkRow(int id, string no, int? printerId, string printer, string? printerModel, string? printerLocation, string toner, int quantity, string? note, string? region,
        string status, string statusLabel, Guid employeeId, string employee, string? department, string? branch, Guid? managerId, string? manager, DateTime requestedAt, string? requestedBy,
        DateTime? decidedAt, string? decidedBy, string? rejectReason, int? orderId, string? orderNo, string? supplier, int? deliveryId, DateOnly? deliveredOn, string? source, decimal? unitCost, DateTime? sentAt, string? sentBy, string? invoiceNo, bool registered, string? newSerial);

    // A printer not yet registered shows "Not registered" with the model and serial number typed by the employee, and the
    // employee's Active Directory location.
    const string InkSelect = "SELECT r.request_id,r.request_no,a.asset_id,ISNULL(a.asset_no,N'Not registered')," +
        "CASE WHEN a.asset_id IS NULL THEN ISNULL(nm.name_en+N' '+nmo.name_en,r.new_model) ELSE CONCAT(m.name_en,' ',mo.name_en) END,ISNULL(pl.value,r.employee_location),r.toner,r.quantity,r.note,r.region_code,r.status," +
        "r.employee_id,e.display_name,e.department,e.branch,r.manager_id,mg.display_name,r.requested_at_utc,rb.display_name,r.decided_at_utc,db.display_name,r.reject_reason," +
        "r.order_id,o.order_no,su.name_en,d.ink_delivery_id,d.delivered_on,d.source,d.unit_cost,r.sent_at_utc,sb.display_name,iv.invoice_no,r.new_serial " +
        "FROM itam_data.ink_request r LEFT JOIN itam_data.asset a ON a.asset_id=r.printer_asset_id LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
        "LEFT JOIN itam_ref.model nmo ON nmo.model_id=r.new_model_id LEFT JOIN itam_ref.manufacturer nm ON nm.manufacturer_id=nmo.manufacturer_id " +
        "LEFT JOIN itam_data.asset_attribute pl ON pl.asset_id=a.asset_id AND pl.attr_code=N'PrinterLocation' JOIN itam_access.employee e ON e.employee_id=r.employee_id " +
        "LEFT JOIN itam_access.employee mg ON mg.employee_id=r.manager_id LEFT JOIN itam_access.employee rb ON rb.employee_id=r.requested_by LEFT JOIN itam_access.employee db ON db.employee_id=r.decided_by " +
        "LEFT JOIN itam_data.supplier_order o ON o.order_id=r.order_id LEFT JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id LEFT JOIN itam_data.ink_delivery d ON d.request_id=r.request_id " +
        "LEFT JOIN itam_access.employee sb ON sb.employee_id=r.sent_by LEFT JOIN itam_data.invoice_line il ON il.line_id=d.invoice_line_id LEFT JOIN itam_data.invoice iv ON iv.invoice_id=il.invoice_id ";
    static InkRow MapInk(DbDataReader r) => new(r.GetInt32(0), r.GetString(1), I(r, 2), r.GetString(3), S(r, 4)?.Trim(), S(r, 5), r.GetString(6), r.GetInt32(7), S(r, 8), S(r, 9),
        r.GetString(10), InkStatusLabel(r.GetString(10)), r.GetGuid(11), r.GetString(12), S(r, 13), S(r, 14), r.IsDBNull(15) ? null : r.GetGuid(15), S(r, 16), Utc(r, 17), S(r, 18),
        r.IsDBNull(19) ? null : Utc(r, 19), S(r, 20), S(r, 21), I(r, 22), S(r, 23), S(r, 24), I(r, 25), Day(r, 26), S(r, 27), D(r, 28), r.IsDBNull(29) ? null : Utc(r, 29), S(r, 30), S(r, 31),
        !r.IsDBNull(2), S(r, 32));

    /// <summary>The Ink page: my requests, the requests waiting for my approval (as manager or backup), and, for IT, the
    /// requests of my regions to order and deliver, with the open supplier orders.</summary>
    public async Task<object> Ink(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var mine = await Rows(c, null, InkSelect + "WHERE r.employee_id=@me OR r.requested_by=@me ORDER BY r.request_id DESC", MapInk, token, ("@me", s.Id));
        // Managers I act for today: myself, and the people whose backup I am on an active leave.
        var actFor = (await Rows(c, null, "SELECT person_id FROM itam_access.leave_backup WHERE backup_employee_id=@me AND cancelled_at_utc IS NULL AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN leave_from AND leave_to",
            r => r.GetGuid(0), token, ("@me", s.Id))).Append(s.Id).ToHashSet();
        var approvals = (await Rows(c, null, InkSelect + "WHERE r.status=N'WaitingApproval' OR (r.decided_by=@me AND r.decided_at_utc>DATEADD(day,-30,SYSUTCDATETIME())) ORDER BY r.request_id DESC", MapInk, token, ("@me", s.Id)))
            .Where(r => r.status != "WaitingApproval" || (r.managerId is Guid m && actFor.Contains(m) && r.employeeId != s.Id)).ToList();
        var handle = s.InkHandle
            ? (await Rows(c, null, InkSelect + "WHERE r.status IN(N'Submitted',N'WaitingApproval',N'Approved',N'Ordered') OR r.requested_at_utc>DATEADD(day,-60,SYSUTCDATETIME()) ORDER BY r.request_id DESC", MapInk, token)).Where(r => s.CanInkSend(r.region) || s.InkView || (s.Specialist && !r.registered)).ToList()
            : [];
        var orders = s.InkView
            ? (await Rows(c, null, "SELECT o.order_id,o.order_no,su.name_en,o.region_code,o.created_at_utc,e.display_name,(SELECT COUNT(*) FROM itam_data.ink_request r WHERE r.order_id=o.order_id)," +
                "(SELECT COUNT(*) FROM itam_data.ink_request r WHERE r.order_id=o.order_id AND r.status=N'Delivered') FROM itam_data.supplier_order o JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id " +
                "LEFT JOIN itam_access.employee e ON e.employee_id=o.created_by WHERE o.created_at_utc>DATEADD(day,-120,SYSUTCDATETIME()) ORDER BY o.order_id DESC",
                r => new { id = r.GetInt32(0), no = r.GetString(1), supplier = r.GetString(2), region = r.GetString(3), at = Utc(r, 4), by = S(r, 5), requests = r.GetInt32(6), delivered = r.GetInt32(7) }, token))
                .Where(o => s.CanInk(o.region) || s.InkView).ToList()
            : [];
        // Toner invoice lines with cartridges left, to deliver from.
        var inkLines = s.CanInkAny ? await Rows(c, null, "SELECT l.line_id,i.invoice_no,su.name_en,l.description,l.quantity-ISNULL((SELECT SUM(q.quantity) FROM itam_data.ink_delivery d JOIN itam_data.ink_request q ON q.request_id=d.request_id WHERE d.invoice_line_id=l.line_id),0) " +
            "FROM itam_data.invoice_line l JOIN itam_data.invoice i ON i.invoice_id=l.invoice_id JOIN itam_ref.supplier su ON su.supplier_id=i.supplier_id WHERE i.category=N'Toners' ORDER BY i.invoice_date DESC,l.line_no",
            r => new { id = r.GetInt32(0), invoice = r.GetString(1), supplier = r.GetString(2), description = r.GetString(3), remaining = r.GetInt32(4) }, token) : [];
        // The manager last chosen for each department, suggested when the ink officer sends a request for approval.
        var lastApprover = s.CanInkAny || s.TeamLeader ? (await Rows(c, null, "SELECT e.department,r.manager_id,m.display_name,m.account FROM itam_data.ink_request r JOIN itam_access.employee e ON e.employee_id=r.employee_id JOIN itam_access.employee m ON m.employee_id=r.manager_id " +
            "WHERE r.manager_id IS NOT NULL AND e.department IS NOT NULL ORDER BY r.request_id DESC", q => (Dept: q.GetString(0), Id: q.GetGuid(1), Label: $"{q.GetString(2)} ({q.GetString(3)})"), token))
            .GroupBy(x => x.Dept).ToDictionary(g => g.Key, g => new { id = g.First().Id, label = g.First().Label }) : [];
        return new { me = s.Id, lastApprover, mine, approvals, handle, orders, inkLines = inkLines.Where(l => l.remaining > 0), canHandle = s.InkHandle, canSend = s.CanInkAny || s.TeamLeader, canOrder = s.CanInkAny, canPrinter = s.InkHandle };
    }

    /// <summary>The order sheet sent to the supplier: one line per request, no prices.</summary>
    public async Task<ItamUpload> OrderPdf(ItamUser user, int orderId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var o = (await Rows(c, null, "SELECT o.order_no,su.name_en,o.region_code,o.created_at_utc,e.display_name,o.note FROM itam_data.supplier_order o JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id LEFT JOIN itam_access.employee e ON e.employee_id=o.created_by WHERE o.order_id=@o",
            r => (No: r.GetString(0), Supplier: r.GetString(1), Region: r.GetString(2), At: Utc(r, 3), By: S(r, 4), Note: S(r, 5)), token, ("@o", orderId))).SingleOrDefault();
        if (o.No is null) throw new ArgumentException("The order was not found.");
        Require(s.CanInk(o.Region) || s.InkView || s.InvoicesView, "You cannot see this order.");
        var reqs = await Rows(c, null, InkSelect + "WHERE r.order_id=@o ORDER BY r.request_id", MapInk, token, ("@o", orderId));
        return new($"Order {o.No}.pdf", "application/pdf", OrderSheet(o.No, o.Supplier, o.At, o.By, o.Note, reqs));
    }

    byte[] OrderSheet(string no, string supplier, DateTime at, string? by, string? note, List<InkRow> reqs)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var d = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W; const string muted = "0.35 0.35 0.35", ink = "0 0 0";
        d.Logo(M, d.Y + 10, 96); d.Text("إدارة أصول تقنية المعلومات", W - M, d.Y, 9, true, muted); d.Text("IT Assets Management", W - M, d.Y - 13, 9, true, muted); d.Y -= 40;
        d.Text($"Toner Order {no}", M, d.Y, 20, false, ink); d.Text("طلب أحبار", W - M, d.Y, 18, true, ink); d.Y -= 12; d.Box(M, d.Y, W - 2 * M, 2, "0 0 0"); d.Y -= 22;
        d.Text($"To: {supplier}", M, d.Y, 11, false, ink); d.Text($"Date: {at.AddHours(3):dd/MM/yyyy}", W - M - 150, d.Y, 11, false, ink); d.Y -= 18;
        d.Text("Please deliver the requests below to the IT department as soon as possible, with the invoice.", M, d.Y, 10, false, ink); d.Y -= 22;
        double[] col = [M, M + 62, M + 180, M + 285, M + 375, M + 485];
        d.Box(M, d.Y - 6, W - 2 * M, 20, "0.93 0.93 0.93");
        foreach (var (h, i) in new[] { "Request", "Employee", "Department", "Branch", "Printer – Toner", "Qty" }.Select((h, i) => (h, i))) d.Text(h, col[i] + 4, d.Y, 9, false, muted);
        d.Y -= 22;
        foreach (var r in reqs)
        {
            if (d.Y < M + 60) d.NewPage();
            d.Text(r.no, col[0] + 4, d.Y, 9, false, ink); d.Fit(r.employee, col[1] + 4, d.Y, 9, 110, ink); d.Fit(r.department, col[2] + 4, d.Y, 9, 98, ink); d.Fit(r.branch, col[3] + 4, d.Y, 9, 84, ink);
            d.Fit($"{r.printer} – {r.toner}", col[4] + 4, d.Y, 9, 104, ink); d.Text(r.quantity.ToString(CultureInfo.InvariantCulture), col[5] + 4, d.Y, 9, false, ink);
            d.Y -= 6; d.Line(M, d.Y, W - M, d.Y); d.Y -= 13;
        }
        d.Y -= 8; d.Text($"Total cartridges: {reqs.Sum(r => r.quantity)}", M, d.Y, 10, false, ink); d.Y -= 16;
        if (note is not null) { d.Fit("Note: " + note, M, d.Y, 9.5, W - 2 * M, ink); d.Y -= 16; }
        d.Text($"Ordered by {by} · IT Assets", M, M - 10, 8, false, muted);
        return d.Build();
    }
}
