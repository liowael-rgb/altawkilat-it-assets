using System.Data;
using System.Data.Common;

namespace Itam.App;

public sealed record ItamPoLineInput(int? LineNo, string? Type, int? ManufacturerId, int? ModelId, string? NewModel, string? Specification, int? Quantity, decimal? UnitCost);
public sealed record ItamPoInput(string? PoNumber, DateOnly? PoDate, int? SupplierId, string? RaisedBy, string? RequestRef, string? InvoiceNo, string? Note,
    ItamPoLineInput[]? Lines, string? Version = null, string? Reason = null);
public sealed record ItamDeviceInput(string? SerialNo, string? ServiceTag, string? MacAddress, string? IpAddress, string? Condition, Dictionary<string, string?>? Attributes,
    string? OsKey, string? SoftwareKey, string? Notes);
public sealed record ItamReceiveInput(int? PoLineId, int? LocationId, DateTime? ReceivedAt, string? InvoiceRef, string? Note, ItamDeviceInput[]? Devices);

public sealed partial class ItamStore
{
    public const decimal Vat = 0.15m;

    /// <summary>Record a PO sent by Procurement (form 12): Team Leader or IT Assets Officer; the PO document is required.</summary>
    public async Task<object> CreatePo(ItamUser user, ItamPoInput x, ItamUpload? document, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.PoManage, "You cannot record purchase orders.");
        if (document is null) throw new ArgumentException("Attach the PO document (PDF) sent by Procurement.");
        var type = CheckFile(document, pdfOnly: true);
        var header = await CheckPo(c, tx, x, null, token);
        var file = await SaveFile(c, tx, document, type, s.Id, token);
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.po(po_number,po_date,supplier_id,raised_by,request_ref,invoice_no,document_file_id,note,created_by) OUTPUT inserted.po_id VALUES(@n,@d,@s,@r,@q,@i,@f,@t,@b)", token,
            ("@n", header.Number), ("@d", DateParam(x.PoDate)), ("@s", x.SupplierId), ("@r", header.RaisedBy), ("@q", header.RequestRef), ("@i", header.Invoice), ("@f", file), ("@t", header.Note), ("@b", s.Id)));
        await SaveLines(c, tx, s, id, x.Lines, token);
        await Audit(c, tx, s.Id, "po", header.Number, "Add", null, new { header.Number, x.PoDate, x.SupplierId, lines = x.Lines?.Length }, x.Reason, token);
        await tx.CommitAsync(token);
        return new { id, poNumber = header.Number };
    }

    /// <summary>Change a PO: header, invoice number, lines (a line keeps at least the devices already received on it), and
    /// optionally a corrected PO document.</summary>
    public async Task UpdatePo(ItamUser user, int id, ItamPoInput x, ItamUpload? document, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.PoManage, "You cannot change purchase orders.");
        var version = await Scalar(c, tx, "SELECT CONVERT(varchar(30),CONVERT(varbinary(8),version),2) FROM itam_data.po WHERE po_id=@i", token, ("@i", id)) as string ?? throw new ArgumentException("The PO was not found.");
        if (x.Version != version) throw new ItamConflict("Someone changed this PO after you opened it. Reload and try again.");
        var header = await CheckPo(c, tx, x, id, token);
        long? file = document is null ? null : await SaveFile(c, tx, document, CheckFile(document, pdfOnly: true), s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.po SET po_number=@n,po_date=@d,supplier_id=@s,raised_by=@r,request_ref=@q,invoice_no=@i,note=@t,document_file_id=COALESCE(@f,document_file_id) WHERE po_id=@id", token,
            ("@id", id), ("@n", header.Number), ("@d", DateParam(x.PoDate)), ("@s", x.SupplierId), ("@r", header.RaisedBy), ("@q", header.RequestRef), ("@i", header.Invoice), ("@t", header.Note), ("@f", file));
        await SaveLines(c, tx, s, id, x.Lines, token);
        await Audit(c, tx, s.Id, "po", header.Number, "Change", null, new { header.Number, x.PoDate, x.SupplierId, header.Invoice, lines = x.Lines?.Length, newDocument = file is not null }, x.Reason, token);
        await tx.CommitAsync(token);
    }

    sealed record PoHeader(string Number, string RaisedBy, string? RequestRef, string? Invoice, string? Note);
    async Task<PoHeader> CheckPo(DbConnection c, DbTransaction tx, ItamPoInput x, int? self, CancellationToken token)
    {
        var number = Text(x.PoNumber, "PO number", 40);
        if (await Scalar(c, tx, "SELECT 1 FROM itam_data.po WHERE po_number=@n AND po_id<>@i", token, ("@n", number), ("@i", self ?? 0)) is not null) throw new ArgumentException($"PO {number} is already recorded.");
        if (x.PoDate is null) throw new ArgumentException("The PO date is required.");
        if (x.PoDate > DateOnly.FromDateTime(UtcNow.AddHours(3))) throw new ArgumentException("The PO date cannot be in the future.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s AND active=1", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
        var request = Optional(x.RequestRef, "Request number", 20)?.ToUpperInvariant();
        if (request is not null && !System.Text.RegularExpressions.Regex.IsMatch(request, @"^PR-\d+$")) throw new ArgumentException("Request number: PR- and a number, for example PR-12.");
        if ((x.Lines ?? []).Length == 0) throw new ArgumentException("Add at least one PO line.");
        return new(number, Text(x.RaisedBy, "Raised by", 200), request, Optional(x.InvoiceNo, "Invoice number", 60), Optional(x.Note, "Note", 1000));
    }

    async Task SaveLines(DbConnection c, DbTransaction tx, ItamScope s, int po, ItamPoLineInput[]? lines, CancellationToken token)
    {
        var existing = (await Rows(c, tx, "SELECT l.po_line_id,l.line_no,l.type_code,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.po_line_id=l.po_line_id) FROM itam_data.po_line l WHERE l.po_id=@p",
            r => (Id: r.GetInt32(0), No: r.GetInt32(1), Type: r.GetString(2), Received: r.GetInt32(3)), token, ("@p", po))).ToDictionary(l => l.No);
        var given = (lines ?? []).Select((l, i) => l with { LineNo = l.LineNo ?? existing.Keys.Concat([0]).Max() + i + 1 }).ToList();
        if (given.Select(l => l.LineNo).Distinct().Count() != given.Count) throw new ArgumentException("Each PO line needs its own line number.");
        foreach (var gone in existing.Values.Where(e => given.All(g => g.LineNo != e.No)))
        {
            if (gone.Received > 0) throw new ArgumentException($"Line {gone.No} has {gone.Received} devices received: it cannot be removed.");
            await Exec(c, tx, "DELETE itam_data.po_line WHERE po_line_id=@l", token, ("@l", gone.Id));
        }
        foreach (var l in given)
        {
            var type = (l.Type ?? "").Trim().ToUpperInvariant();
            if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.asset_type WHERE type_code=@t AND active=1", token, ("@t", type)) is null) throw new ArgumentException($"Line {l.LineNo}: choose the product type.");
            // Software has no manufacturer or model; toner has no model (the same rules as registering).
            int? maker = ItamTypeFields.ManufacturerUsed(type) ? l.ManufacturerId : null;
            if (ItamTypeFields.ManufacturerUsed(type) && await Scalar(c, tx, "SELECT 1 FROM itam_ref.manufacturer WHERE manufacturer_id=@m AND active=1", token, ("@m", maker ?? 0)) is null) throw new ArgumentException($"Line {l.LineNo}: choose the manufacturer.");
            int? model;
            if (!ItamTypeFields.ModelUsed(type)) model = null;
            else if (!string.IsNullOrWhiteSpace(l.NewModel))
            {
                var name = Text(l.NewModel, "Model", 150);
                model = await Scalar(c, tx, "SELECT model_id FROM itam_ref.model WHERE manufacturer_id=@m AND type_code=@t AND name_en=@n", token, ("@m", l.ManufacturerId), ("@t", type), ("@n", name)) is int m ? m
                    : Convert.ToInt32(await Scalar(c, tx, "INSERT itam_ref.model(manufacturer_id,type_code,name_en) OUTPUT inserted.model_id VALUES(@m,@t,@n)", token, ("@m", l.ManufacturerId), ("@t", type), ("@n", name)));
            }
            else if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.model WHERE model_id=@m AND manufacturer_id=@f AND type_code=@t", token, ("@m", l.ModelId ?? 0), ("@f", l.ManufacturerId), ("@t", type)) is null)
                throw new ArgumentException($"Line {l.LineNo}: choose the model (of this manufacturer and type).");
            else model = l.ModelId!.Value;
            var qty = l.Quantity is >= 1 and <= 10000 ? l.Quantity.Value : throw new ArgumentException($"Line {l.LineNo}: quantity 1 to 10,000.");
            var cost = l.UnitCost is >= 0 and <= 10_000_000 ? decimal.Round(l.UnitCost.Value, 2) : throw new ArgumentException($"Line {l.LineNo}: unit cost in SAR before VAT.");
            var spec = Optional(l.Specification, $"Line {l.LineNo} specification", 500);
            if (existing.TryGetValue(l.LineNo!.Value, out var e))
            {
                if (qty < e.Received) throw new ArgumentException($"Line {e.No}: {e.Received} devices are already received; the quantity cannot be lower.");
                if (e.Received > 0 && e.Type != type) throw new ArgumentException($"Line {e.No}: devices are received; its product type cannot change.");
                await Exec(c, tx, "UPDATE itam_data.po_line SET type_code=@t,manufacturer_id=@m,model_id=@mo,specification=@s,quantity=@q,unit_cost=@u WHERE po_line_id=@l", token,
                    ("@l", e.Id), ("@t", type), ("@m", maker), ("@mo", model), ("@s", spec), ("@q", qty), ("@u", cost));
            }
            else await Exec(c, tx, "INSERT itam_data.po_line(po_id,line_no,type_code,manufacturer_id,model_id,specification,quantity,unit_cost) VALUES(@p,@n,@t,@m,@mo,@s,@q,@u)", token,
                ("@p", po), ("@n", l.LineNo), ("@t", type), ("@m", maker), ("@mo", model), ("@s", spec), ("@q", qty), ("@u", cost));
        }
    }

    /// <summary>Receive devices against one PO line (form 13): each row becomes an asset In Store where it is received.
    /// Partial receipts are allowed; more than the remaining quantity is refused, and so are duplicate identifiers.</summary>
    public async Task<object> Receive(ItamUser user, int po, ItamReceiveInput x, ItamUpload? evidence, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.PoManage, "You cannot receive devices on a PO.");
        var head = (await Rows(c, tx, "SELECT po_number,supplier_id,po_date,invoice_no FROM itam_data.po WITH (UPDLOCK) WHERE po_id=@p",
            r => (No: r.GetString(0), Supplier: r.GetInt32(1), Date: Day(r, 2), Invoice: S(r, 3)), token, ("@p", po))).SingleOrDefault();
        if (head.No is null) throw new ArgumentException("The PO was not found.");
        var line = (await Rows(c, tx, "SELECT po_line_id,line_no,type_code,manufacturer_id,model_id,quantity,unit_cost,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.po_line_id=l.po_line_id) FROM itam_data.po_line l WHERE po_line_id=@l AND po_id=@p",
            r => (Id: r.GetInt32(0), No: r.GetInt32(1), Type: r.GetString(2), Manufacturer: I(r, 3), Model: I(r, 4), Qty: r.GetInt32(5), Cost: r.GetDecimal(6), Received: r.GetInt32(7)), token, ("@l", x.PoLineId ?? 0), ("@p", po))).SingleOrDefault();
        if (line.Id == 0) throw new ArgumentException("Choose the PO line.");
        var devices = x.Devices ?? [];
        if (devices.Length == 0) throw new ArgumentException("Enter at least one device.");
        if (devices.Length > line.Qty - line.Received) throw new ArgumentException($"Line {line.No}: {line.Qty - line.Received} devices are still to come; {devices.Length} cannot be received.");
        var receivedAt = x.ReceivedAt?.ToUniversalTime() ?? UtcNow;
        if (receivedAt > UtcNow.AddMinutes(5)) throw new ArgumentException("The receipt date cannot be in the future.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.location WHERE location_id=@l AND active=1", token, ("@l", x.LocationId ?? 0)) is null) throw new ArgumentException("Choose the IT warehouse or branch receiving the devices.");
        var into = await Scalar(c, tx, "SELECT region_code FROM itam_ref.location WHERE location_id=@l", token, ("@l", x.LocationId)) as string;
        Require(s.CanReceiveInto(into ?? ""), $"You receive devices into IT warehouses and branches of your regions only ({string.Join(", ", s.Regions)}).");
        long? file = evidence is null ? null : await SaveFile(c, tx, evidence, CheckFile(evidence, pdfOnly: false), s.Id, token);
        var receipt = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.po_receipt(po_id,location_id,received_at_utc,received_by,invoice_ref,evidence_file_id,note) OUTPUT inserted.receipt_id VALUES(@p,@l,@t,@b,@i,@f,@n)", token,
            ("@p", po), ("@l", x.LocationId ?? 0), ("@t", receivedAt), ("@b", s.Id), ("@i", Optional(x.InvoiceRef, "Invoice reference", 60)), ("@f", file), ("@n", Optional(x.Note, "Note", 500))));
        var created = new List<string>(); var row = 0;
        foreach (var dvc in devices)
        {
            row++;
            try
            {
                // The same serial number twice in this receipt (checked before the rows already saved in it).
                if (Key(dvc.SerialNo) is string key && devices.Take(row - 1).Any(o => Key(o.SerialNo) == key)) throw new ArgumentException($"Serial number {dvc.SerialNo!.Trim()} is entered twice.");
                if (Key(dvc.ServiceTag) is string tagKey && devices.Take(row - 1).Any(o => Key(o.ServiceTag) == tagKey)) throw new ArgumentException($"Service tag {dvc.ServiceTag!.Trim()} is entered twice.");
                var d = await Check(c, tx, s, new ItamAssetInput(line.Type, x.LocationId, line.Manufacturer, line.Model, null, dvc.SerialNo, dvc.ServiceTag, dvc.MacAddress, dvc.IpAddress,
                    dvc.Condition ?? "New", head.Date, head.Supplier, head.No, line.Cost, x.InvoiceRef ?? head.Invoice, dvc.Notes, dvc.Attributes, null, null), line.Type, null, token);
                var (_, no) = await Create(c, tx, s.Id, d, po, line.Id, receipt, dvc.OsKey, dvc.SoftwareKey, $"Received on PO {head.No}, line {line.No}", token);
                created.Add(no);
            }
            catch (ArgumentException ex) { throw new ArgumentException($"Device {row}: {ex.Message} Nothing was received.", ex); }
        }
        await Audit(c, tx, s.Id, "po", head.No, "Receive", null, new { line = line.No, location = x.LocationId, devices = created }, null, token);
        await tx.CommitAsync(token);
        return new { receipt, assets = created };
    }

    // PO pages -------------------------------------------------------------------------------------------------------------------------
    public async Task<object> Pos(ItamUser user, string? text, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.PoView, "You cannot see purchase orders.");
        var like = "%" + (text ?? "").Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        return await Rows(c, null,
            "SELECT p.po_id,p.po_number,p.po_date,su.name_en,p.raised_by,p.invoice_no,(SELECT SUM(quantity) FROM itam_data.po_line l WHERE l.po_id=p.po_id)," +
            "(SELECT COUNT(*) FROM itam_data.asset a WHERE a.po_id=p.po_id),(SELECT SUM(quantity*unit_cost) FROM itam_data.po_line l WHERE l.po_id=p.po_id),(SELECT MAX(sent_on) FROM itam_data.pack_print k WHERE k.po_id=p.po_id) " +
            "FROM itam_data.po p JOIN itam_ref.supplier su ON su.supplier_id=p.supplier_id WHERE p.po_number LIKE @q OR su.name_en LIKE @q OR p.invoice_no LIKE @q ORDER BY p.po_date DESC,p.po_id DESC",
            r => new { id = r.GetInt32(0), poNumber = r.GetString(1), poDate = Day(r, 2), supplier = r.GetString(3), raisedBy = r.GetString(4), invoiceNo = S(r, 5),
                ordered = r.IsDBNull(6) ? 0 : r.GetInt32(6), received = r.GetInt32(7), total = r.IsDBNull(8) ? 0 : decimal.Round(r.GetDecimal(8) * (1 + Vat), 2), sentOn = Day(r, 9) }, token, ("@q", like));
    }

    /// <summary>A device received on the PO, with its first delivery after the purchase (the note that goes to Accounting).</summary>
    internal sealed record PoDevice(int Id, string AssetNo, string Type, string? Model, string? Serial, string? ServiceTag, string Status, string KeptAt, string Region, int LineNo, DateTime ReceivedAt,
        int? DeliveryId = null, string? DeliveryNo = null, string? Employee = null, DateOnly? DeliveredOn = null, DateTime? SignedAt = null, long? SignedFile = null)
    {
        public bool Delivered => DeliveryId is not null;
        public bool Signed => SignedFile is not null;
    }
    internal sealed record PoView(int Id, string Number, DateOnly Date, string Supplier, string? SupplierVat, string RaisedBy, string? RequestRef, string? Invoice,
        string? Note, long DocumentFile, string Version, List<PoLineView> Lines, List<PoDevice> Devices);
    internal sealed record PoLineView(int Id, int No, string Type, string TypeName, int? ManufacturerId, string? Manufacturer, int? ModelId, string? Model, string? Specification, int Quantity, decimal UnitCost, int Received);

    internal static async Task<PoView> LoadPo(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
    {
        var p = (await Rows(c, tx, "SELECT p.po_number,p.po_date,su.name_en,su.vat_no,p.raised_by,p.request_ref,p.invoice_no,p.note,p.document_file_id,CONVERT(varchar(30),CONVERT(varbinary(8),p.version),2) " +
            "FROM itam_data.po p JOIN itam_ref.supplier su ON su.supplier_id=p.supplier_id WHERE p.po_id=@i",
            r => (No: r.GetString(0), Date: Day(r, 1)!.Value, Supplier: r.GetString(2), Vat: S(r, 3), RaisedBy: r.GetString(4), Request: S(r, 5), Invoice: S(r, 6), Note: S(r, 7), File: r.GetInt64(8), Version: r.GetString(9)),
            token, ("@i", id))).SingleOrDefault();
        if (p.No is null) throw new ArgumentException("The PO was not found.");
        var lines = await Rows(c, tx, "SELECT l.po_line_id,l.line_no,l.type_code,t.name_en,l.manufacturer_id,m.name_en,l.model_id,mo.name_en,l.specification,l.quantity,l.unit_cost,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.po_line_id=l.po_line_id) " +
            "FROM itam_data.po_line l JOIN itam_ref.asset_type t ON t.type_code=l.type_code LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=l.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=l.model_id WHERE l.po_id=@i ORDER BY l.line_no",
            r => new PoLineView(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), I(r, 4), S(r, 5), I(r, 6), S(r, 7), S(r, 8), r.GetInt32(9), r.GetDecimal(10), r.GetInt32(11)), token, ("@i", id));
        var devices = await Rows(c, tx, "SELECT a.asset_id,a.asset_no,a.type_code,mo.name_en,a.serial_no,a.service_tag,a.status,g.name_en,g.region_code,l.line_no,r.received_at_utc," +
            "fd.delivery_id,fd.delivery_no,e.display_name,fd.delivered_on,fd.signed_at_utc,fd.signed_file_id " +
            "FROM itam_data.asset a LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id JOIN itam_ref.location g ON g.location_id=a.location_id JOIN itam_data.po_line l ON l.po_line_id=a.po_line_id " +
            "JOIN itam_data.po_receipt r ON r.receipt_id=a.receipt_id " +
            "OUTER APPLY (SELECT TOP 1 d.delivery_id,d.delivery_no,d.employee_id,d.delivered_on,d.signed_at_utc,d.signed_file_id FROM itam_data.delivery d WHERE d.asset_id=a.asset_id ORDER BY d.delivery_id) fd " +
            "LEFT JOIN itam_access.employee e ON e.employee_id=fd.employee_id WHERE a.po_id=@i ORDER BY a.asset_seq",
            r => new PoDevice(r.GetInt32(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4), S(r, 5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9), Utc(r, 10),
                I(r, 11), S(r, 12), S(r, 13), Day(r, 14), r.IsDBNull(15) ? null : Utc(r, 15), r.IsDBNull(16) ? null : r.GetInt64(16)), token, ("@i", id));
        return new(id, p.No, p.Date, p.Supplier, p.Vat, p.RaisedBy, p.Request, p.Invoice, p.Note, p.File, p.Version, lines, devices);
    }

    /// <summary>The PO page counts. Delivered counts follow the first delivery note of each device: waiting for the copy the
    /// employee signed on paper, or signed (copy uploaded). Reservations arrive with requests (build step 3).</summary>
    internal static object Counts(PoView p)
    {
        int ordered = p.Lines.Sum(l => l.Quantity), received = p.Devices.Count;
        return new
        {
            ordered, received, stillToCome = ordered - received, inStock = p.Devices.Count(d => d.Status == "InStore" && !d.Delivered), reserved = 0,
            deliveredWaitingSigned = p.Devices.Count(d => d.Delivered && !d.Signed), deliveredSigned = p.Devices.Count(d => d.Signed),
            other = p.Devices.Count(d => !d.Delivered && d.Status != "InStore")
        };
    }

    public async Task<object> Po(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.PoView, "You cannot see purchase orders.");
        var p = await LoadPo(c, null, id, token);
        var receipts = await Rows(c, null, "SELECT r.receipt_id,r.received_at_utc,e.display_name,g.name_en,r.invoice_ref,r.evidence_file_id,r.note,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.receipt_id=r.receipt_id) " +
            "FROM itam_data.po_receipt r JOIN itam_ref.location g ON g.location_id=r.location_id LEFT JOIN itam_access.employee e ON e.employee_id=r.received_by WHERE r.po_id=@i ORDER BY r.receipt_id",
            r => new { id = r.GetInt32(0), at = Utc(r, 1), by = S(r, 2), location = r.GetString(3), invoiceRef = S(r, 4), evidence = r.IsDBNull(5) ? (long?)null : r.GetInt64(5), note = S(r, 6), devices = r.GetInt32(7) }, token, ("@i", id));
        var invoices = await Rows(c, null, "SELECT i.invoice_id,i.invoice_no,i.invoice_date,i.total,(SELECT COUNT(*) FROM itam_data.asset a JOIN itam_data.po_receipt r ON r.receipt_id=a.receipt_id WHERE r.invoice_id=i.invoice_id),pk.pack_no,pk.sent_on,pk.paid_on FROM itam_data.invoice i LEFT JOIN itam_data.payment_pack pk ON pk.pack_id=i.pack_id WHERE i.po_id=@i ORDER BY i.invoice_date",
            r => new { id = r.GetInt32(0), no = r.GetString(1), date = Day(r, 2), total = r.GetDecimal(3), devices = r.GetInt32(4), pack = S(r, 5), sentOn = Day(r, 6), paidOn = Day(r, 7) }, token, ("@i", id));
        var prints = await Rows(c, null, "SELECT k.print_id,k.printed_at_utc,e.display_name,k.devices,k.notes_signed,k.notes_unsigned,k.sent_on,f.display_name FROM itam_data.pack_print k " +
            "LEFT JOIN itam_access.employee e ON e.employee_id=k.printed_by LEFT JOIN itam_access.employee f ON f.employee_id=k.sent_by WHERE k.po_id=@i ORDER BY k.print_id DESC",
            r => new { id = r.GetInt32(0), at = Utc(r, 1), by = S(r, 2), devices = r.GetInt32(3), signed = r.GetInt32(4), unsigned = r.GetInt32(5), sentOn = Day(r, 6), sentBy = S(r, 7) }, token, ("@i", id));
        decimal net = p.Lines.Sum(l => l.Quantity * l.UnitCost);
        return new
        {
            po = new { p.Id, number = p.Number, date = p.Date, p.Supplier, supplierVat = p.SupplierVat, p.RaisedBy, requestRef = p.RequestRef, invoiceNo = p.Invoice, p.Note, p.Version, documentFile = p.DocumentFile,
                supplierId = await Scalar(c, null, "SELECT supplier_id FROM itam_data.po WHERE po_id=@i", token, ("@i", id)) },
            lines = p.Lines.Select(l => new { l.Id, no = l.No, l.Type, l.TypeName, l.ManufacturerId, l.Manufacturer, l.ModelId, l.Model, l.Specification, l.Quantity, l.UnitCost, lineTotal = l.Quantity * l.UnitCost, l.Received, toCome = l.Quantity - l.Received,
                fields = ItamTypeFields.For(l.Type), manufacturerUsed = ItamTypeFields.ManufacturerUsed(l.Type), modelUsed = ItamTypeFields.ModelUsed(l.Type), serialUsed = ItamTypeFields.SerialUsed(l.Type), serialRequired = ItamTypeFields.SerialRequired(l.Type), serviceTag = ItamTypeFields.ServiceTagUsed(l.Type), mac = ItamTypeFields.MacUsed(l.Type), macRequired = ItamTypeFields.MacRequired(l.Type), ip = ItamTypeFields.IpUsed(l.Type), key = ItamTypeFields.KeyUsed(l.Type) }),
            devices = p.Devices.Select(d => new { d.Id, d.AssetNo, d.Type, d.Model, d.Serial, d.ServiceTag, status = ItamStatuses.Label(d.Status), d.KeptAt, d.Region, line = d.LineNo, d.ReceivedAt,
                d.DeliveryId, d.DeliveryNo, d.Employee, d.DeliveredOn, d.SignedAt, signed = d.Signed }),
            invoices, invoicedDevices = invoices.Sum(x => x.devices), counts = Counts(p), totals = new { net, vat = decimal.Round(net * Vat, 2), total = decimal.Round(net * (1 + Vat), 2) },
            receipts, prints, canManage = s.PoManage
        };
    }

    public async Task<ItamUpload> File(ItamUser user, long fileId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        // Files of purchase orders and receipts: the people who see purchase orders.
        var owner = await Scalar(c, null, "SELECT CASE WHEN EXISTS(SELECT 1 FROM itam_data.po WHERE document_file_id=@f) OR EXISTS(SELECT 1 FROM itam_data.po_receipt WHERE evidence_file_id=@f) THEN 'po' END", token, ("@f", fileId)) as string;
        Require(owner == "po" && s.PoView, "You cannot open this file.");
        return await LoadFile(c, null, fileId, token) ?? throw new ArgumentException("The file was not found.");
    }

    /// <summary>Records that the printed PO pack went to Accounting.</summary>
    public async Task MarkSent(ItamUser user, int po, int print, DateOnly? on, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(token);
        var s = await Scope(c, tx, user, token);
        Require(s.PoManage, "You cannot record what went to Accounting.");
        var day = on ?? DateOnly.FromDateTime(UtcNow.AddHours(3));
        if (day > DateOnly.FromDateTime(UtcNow.AddHours(3))) throw new ArgumentException("The date cannot be in the future.");
        if (await Exec(c, tx, "UPDATE itam_data.pack_print SET sent_on=@d,sent_by=@b WHERE print_id=@k AND po_id=@p", token, ("@d", DateParam(day)), ("@b", s.Id), ("@k", print), ("@p", po)) == 0)
            throw new ArgumentException("The print was not found.");
        await Audit(c, tx, s.Id, "po", po.ToString(), "Sent to Accounting", null, new { print, day }, null, token);
        await tx.CommitAsync(token);
    }
}
