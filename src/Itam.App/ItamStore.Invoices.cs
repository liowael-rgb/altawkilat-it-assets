using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Itam.App;

public sealed record ItamInvoiceLineInput(string? Description, int? Quantity, decimal? UnitCost);
public sealed record ItamInvoiceInput(int? SupplierId, string? InvoiceNo, DateOnly? InvoiceDate, string? Category, int? OrderId, int? PoId, int[]? ReceiptIds, string? Note, ItamInvoiceLineInput[]? Lines);
public sealed record ItamInkDeliverInput(DateOnly? DeliveredOn, string? Source, int? InvoiceLineId);
public sealed record ItamPackInput(int? SupplierId, string? Category, int[]? InvoiceIds);
public sealed record ItamPackDates(DateOnly? SentOn, DateOnly? PaidOn, string? PaidNote);

/// <summary>Build step 3a: supplier invoices (toners, devices, parts and maintenance), ink delivery notes, and the payment pack
/// for Accounting (the owner's Inter-Office Memo package: cover, then each invoice with its order and signed notes).</summary>
public sealed partial class ItamStore
{
    public static readonly (string Code, string Label)[] InvoiceCategories = [("Toners", "Toners"), ("Devices", "Devices"), ("PartsMaintenance", "Parts and maintenance")];
    static string CategoryLabel(string c) => InvoiceCategories.FirstOrDefault(x => x.Code == c).Label ?? c;
    /// <summary>Toner and parts invoices: the regional IT handlers; device invoices (PO receipts): Team Leader and IT Assets Officer.</summary>
    static bool CanInvoice(ItamScope s, string category) => category == "Devices" ? s.PoManage : s.CanHandoverAny;
    static bool CanSeeInvoices(ItamScope s) => s.PoView || s.CanHandoverAny;

    // Invoices -----------------------------------------------------------------------------------------------------------------------
    public async Task<object> CreateInvoice(ItamUser user, ItamInvoiceInput x, ItamUpload? document, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var category = InvoiceCategories.Any(k => k.Code == x.Category) ? x.Category! : throw new ArgumentException("Choose the category: Toners, Devices, or Parts and maintenance.");
        Require(CanInvoice(s, category), category == "Devices" ? "Only the IT Helpdesk Team Leader and the IT Assets Officer record device invoices." : "Only IT handlers record toner and parts invoices.");
        if (document is null) throw new ArgumentException("Attach the invoice (PDF).");
        var fileType = CheckFile(document, pdfOnly: true);
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
        var no = Text(x.InvoiceNo, "Invoice number", 60);
        if (await Scalar(c, tx, "SELECT 1 FROM itam_data.invoice WHERE supplier_id=@s AND invoice_no=@n", token, ("@s", x.SupplierId), ("@n", no)) is not null)
            throw new ArgumentException($"Invoice {no} of this supplier is already recorded.");
        if (x.InvoiceDate is null || x.InvoiceDate > DateOnly.FromDateTime(UtcNow.AddHours(3))) throw new ArgumentException("The invoice date is required and cannot be in the future.");
        var lines = (x.Lines ?? []).Select((l, i) => (No: i + 1, Desc: Text(l.Description, $"Line {i + 1} description", 200),
            Qty: l.Quantity is >= 1 and <= 10000 ? l.Quantity.Value : throw new ArgumentException($"Line {i + 1}: quantity 1 to 10,000."),
            Cost: l.UnitCost is >= 0 and <= 10_000_000 ? decimal.Round(l.UnitCost.Value, 2) : throw new ArgumentException($"Line {i + 1}: unit cost in SAR before VAT."))).ToList();
        if (lines.Count == 0) throw new ArgumentException("Add the invoice lines.");
        int? order = null, po = null;
        if (category == "Toners" && x.OrderId is int o)
        {
            if (await Scalar(c, tx, "SELECT supplier_id FROM itam_data.supplier_order WHERE order_id=@o", token, ("@o", o)) is not int sup || sup != x.SupplierId) throw new ArgumentException("The order belongs to another supplier.");
            order = o;
        }
        var receipts = (x.ReceiptIds ?? []).Distinct().ToArray();
        if (category == "Devices")
        {
            if (await Scalar(c, tx, "SELECT supplier_id FROM itam_data.po WHERE po_id=@p", token, ("@p", x.PoId ?? 0)) is not int sup) throw new ArgumentException("Choose the PO this invoice is for.");
            if (sup != x.SupplierId) throw new ArgumentException("The PO belongs to another supplier.");
            po = x.PoId;
            foreach (var r in receipts)
            {
                var rec = (await Rows(c, tx, "SELECT po_id,invoice_id FROM itam_data.po_receipt WITH (UPDLOCK) WHERE receipt_id=@r", q => (Po: q.GetInt32(0), Inv: I(q, 1)), token, ("@r", r))).SingleOrDefault();
                if (rec.Po != po) throw new ArgumentException("A receipt does not belong to this PO.");
                if (rec.Inv is not null) throw new ArgumentException("A receipt is already on another invoice.");
            }
        }
        var net = lines.Sum(l => l.Qty * l.Cost); var vat = decimal.Round(net * Vat, 2);
        var file = await SaveFile(c, tx, document, fileType, s.Id, token);
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.invoice(supplier_id,invoice_no,invoice_date,category,file_id,order_id,po_id,net,vat,total,note,created_by) OUTPUT inserted.invoice_id " +
            "VALUES(@s,@n,@d,@c,@f,@o,@p,@net,@vat,@tot,@x,@b)", token, ("@s", x.SupplierId), ("@n", no), ("@d", DateParam(x.InvoiceDate)), ("@c", category), ("@f", file), ("@o", order), ("@p", po),
            ("@net", net), ("@vat", vat), ("@tot", net + vat), ("@x", Optional(x.Note, "Note", 500)), ("@b", s.Id)));
        foreach (var l in lines) await Exec(c, tx, "INSERT itam_data.invoice_line(invoice_id,line_no,description,quantity,unit_cost) VALUES(@i,@n,@d,@q,@u)", token, ("@i", id), ("@n", l.No), ("@d", l.Desc), ("@q", l.Qty), ("@u", l.Cost));
        foreach (var r in receipts) await Exec(c, tx, "UPDATE itam_data.po_receipt SET invoice_id=@i WHERE receipt_id=@r", token, ("@i", id), ("@r", r));
        await Audit(c, tx, s.Id, "invoice", no, "Add", null, new { x.SupplierId, category, order, po, receipts, net, vat }, null, token);
        await tx.CommitAsync(token);
        return new { id, invoiceNo = no };
    }

    public async Task<object> Invoices(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s), "You cannot see invoices.");
        var list = await Rows(c, null, "SELECT i.invoice_id,i.invoice_no,i.invoice_date,su.name_en,i.supplier_id,i.category,i.total,o.order_no,p.po_number,pk.pack_no,i.pack_id,pk.sent_on,pk.paid_on " +
            "FROM itam_data.invoice i JOIN itam_ref.supplier su ON su.supplier_id=i.supplier_id LEFT JOIN itam_data.supplier_order o ON o.order_id=i.order_id LEFT JOIN itam_data.po p ON p.po_id=i.po_id " +
            "LEFT JOIN itam_data.payment_pack pk ON pk.pack_id=i.pack_id ORDER BY i.invoice_date DESC,i.invoice_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), date = Day(r, 2), supplier = r.GetString(3), supplierId = r.GetInt32(4), category = r.GetString(5), categoryLabel = CategoryLabel(r.GetString(5)),
                total = r.GetDecimal(6), order = S(r, 7), po = S(r, 8), pack = S(r, 9), packId = I(r, 10), sentOn = Day(r, 11), paidOn = Day(r, 12) }, token);
        var packs = await Rows(c, null, "SELECT pk.pack_id,pk.pack_no,su.name_en,pk.category,pk.created_at_utc,e.display_name,pk.sent_on,pk.paid_on,pk.paid_note,(SELECT COUNT(*) FROM itam_data.invoice i WHERE i.pack_id=pk.pack_id)," +
            "(SELECT SUM(total) FROM itam_data.invoice i WHERE i.pack_id=pk.pack_id),CASE WHEN pk.signed_cover_file_id IS NULL THEN 0 ELSE 1 END FROM itam_data.payment_pack pk JOIN itam_ref.supplier su ON su.supplier_id=pk.supplier_id " +
            "LEFT JOIN itam_access.employee e ON e.employee_id=pk.created_by ORDER BY pk.pack_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), supplier = r.GetString(2), category = CategoryLabel(r.GetString(3)), at = Utc(r, 4), by = S(r, 5), sentOn = Day(r, 6), paidOn = Day(r, 7), paidNote = S(r, 8),
                invoices = r.GetInt32(9), total = r.IsDBNull(10) ? 0 : r.GetDecimal(10), signedCover = r.GetInt32(11) == 1 }, token);
        var orders = await Rows(c, null, "SELECT o.order_id,o.order_no,o.supplier_id,su.name_en FROM itam_data.supplier_order o JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id ORDER BY o.order_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), supplierId = r.GetInt32(2), supplier = r.GetString(3) }, token);
        var pos = s.PoManage ? await Rows(c, null, "SELECT p.po_id,p.po_number,p.supplier_id FROM itam_data.po p ORDER BY p.po_date DESC", r => new { id = r.GetInt32(0), no = r.GetString(1), supplierId = r.GetInt32(2) }, token) : [];
        return new { invoices = list, packs, orders, pos, categories = InvoiceCategories.Where(k => CanInvoice(s, k.Code)).Select(k => new { code = k.Code, label = k.Label }), canPack = s.CanHandoverAny || s.PoManage };
    }

    /// <summary>One invoice: lines with what was delivered from each (ink), the requests of its order, or the PO receipts it covers.</summary>
    public async Task<object> Invoice(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s), "You cannot see invoices.");
        var i = (await Rows(c, null, "SELECT i.invoice_no,i.invoice_date,su.name_en,su.vat_no,i.category,i.file_id,i.order_id,o.order_no,i.po_id,p.po_number,i.net,i.vat,i.total,i.note,pk.pack_no,i.pack_id,e.display_name,i.created_at_utc " +
            "FROM itam_data.invoice i JOIN itam_ref.supplier su ON su.supplier_id=i.supplier_id LEFT JOIN itam_data.supplier_order o ON o.order_id=i.order_id LEFT JOIN itam_data.po p ON p.po_id=i.po_id " +
            "LEFT JOIN itam_data.payment_pack pk ON pk.pack_id=i.pack_id LEFT JOIN itam_access.employee e ON e.employee_id=i.created_by WHERE i.invoice_id=@i",
            r => new { no = r.GetString(0), date = Day(r, 1), supplier = r.GetString(2), supplierVat = S(r, 3), category = r.GetString(4), categoryLabel = CategoryLabel(r.GetString(4)), file = r.GetInt64(5), orderId = I(r, 6), orderNo = S(r, 7),
                poId = I(r, 8), poNumber = S(r, 9), net = r.GetDecimal(10), vat = r.GetDecimal(11), total = r.GetDecimal(12), note = S(r, 13), pack = S(r, 14), packId = I(r, 15), by = S(r, 16), at = Utc(r, 17) }, token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The invoice was not found.");
        var lines = await Rows(c, null, "SELECT l.line_id,l.line_no,l.description,l.quantity,l.unit_cost,ISNULL((SELECT SUM(r.quantity) FROM itam_data.ink_delivery d JOIN itam_data.ink_request r ON r.request_id=d.request_id WHERE d.invoice_line_id=l.line_id),0) " +
            "FROM itam_data.invoice_line l WHERE l.invoice_id=@i ORDER BY l.line_no",
            r => new { id = r.GetInt32(0), no = r.GetInt32(1), description = r.GetString(2), quantity = r.GetInt32(3), unitCost = r.GetDecimal(4), lineTotal = r.GetInt32(3) * r.GetDecimal(4), delivered = r.GetInt32(5), remaining = r.GetInt32(3) - r.GetInt32(5) }, token, ("@i", id));
        var requests = i.orderId is int o ? (await Rows(c, null, InkSelect + "WHERE r.order_id=@o ORDER BY r.request_id", MapInk, token, ("@o", o))) : [];
        var receipts = i.poId is int p ? await Rows(c, null, "SELECT r.receipt_id,r.received_at_utc,g.name_en,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.receipt_id=r.receipt_id),r.invoice_id FROM itam_data.po_receipt r JOIN itam_ref.location g ON g.location_id=r.location_id WHERE r.po_id=@p ORDER BY r.receipt_id",
            r => new { id = r.GetInt32(0), at = Utc(r, 1), location = r.GetString(2), devices = r.GetInt32(3), onThis = I(r, 4) == id, onOther = I(r, 4) is int other && other != id }, token, ("@p", p)) : [];
        return new { invoice = i, lines, requests, receipts };
    }

    public async Task<ItamUpload> InvoiceFile(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s), "You cannot see invoices.");
        var f = await Scalar(c, null, "SELECT file_id FROM itam_data.invoice WHERE invoice_id=@i", token, ("@i", id)) as long? ?? throw new ArgumentException("The invoice was not found.");
        return await LoadFile(c, null, f, token) ?? throw new ArgumentException("The file was not found.");
    }

    // Ink delivery notes -------------------------------------------------------------------------------------------------------------
    /// <summary>Deliver the ink of an approved (or ordered) request: from the IT shelf, or from a line of a toner invoice (never
    /// more than the line's quantity). Ink delivery note = printer Asset No. + ink sequence (after 601).</summary>
    public async Task<object> DeliverInk(ItamUser user, int requestId, ItamInkDeliverInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var r = (await Rows(c, tx, InkSelect + "WHERE r.request_id=@i", MapInk, token, ("@i", requestId))).SingleOrDefault() ?? throw new ArgumentException("The ink request was not found.");
        Require(s.CanHandover(r.region), "You deliver ink in your regions only.");
        if (r.status is not ("Approved" or "Ordered")) throw new ArgumentException($"{r.no} is {r.statusLabel}: only approved requests are delivered.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.DeliveredOn ?? today;
        if (on > today) throw new ArgumentException("The delivery date cannot be in the future.");
        var source = x.Source is "Shelf" or "Invoice" ? x.Source : throw new ArgumentException("Choose where the toner comes from: the IT shelf or a supplier invoice.");
        decimal? unit = null; string sourceText = "IT shelf";
        if (source == "Invoice")
        {
            var l = (await Rows(c, tx, "SELECT l.quantity,l.unit_cost,i.category,i.invoice_no,ISNULL((SELECT SUM(q.quantity) FROM itam_data.ink_delivery d JOIN itam_data.ink_request q ON q.request_id=d.request_id WHERE d.invoice_line_id=l.line_id),0) " +
                "FROM itam_data.invoice_line l WITH (UPDLOCK) JOIN itam_data.invoice i ON i.invoice_id=l.invoice_id WHERE l.line_id=@l",
                q => (Qty: q.GetInt32(0), Cost: q.GetDecimal(1), Cat: q.GetString(2), No: q.GetString(3), Used: q.GetInt32(4)), token, ("@l", x.InvoiceLineId ?? 0))).SingleOrDefault();
            if (l.No is null || l.Cat != "Toners") throw new ArgumentException("Choose the toner invoice line the cartridges come from.");
            if (l.Used + r.quantity > l.Qty) throw new ArgumentException($"Invoice {l.No}: only {l.Qty - l.Used} left on that line.");
            unit = l.Cost; sourceText = $"Supplier invoice {l.No}";
        }
        var emp = await EmployeeRows(c, tx, r.employeeId, token);
        var printer = new List<NoteRow> { new("Printer Asset No.", "رقم الطابعة", r.printer), new("Printer", "الطابعة", r.printerModel), new("Printer Location", "موقع الطابعة", r.printerLocation),
            new("Toner", "الحبر", r.toner), new("Quantity", "الكمية", r.quantity.ToString(CultureInfo.InvariantCulture)), new("Ink Request No.", "رقم طلب الحبر", r.no), new("Source", "المصدر", sourceText) };
        if (unit is decimal u) printer.Add(new("Cost incl. VAT", "التكلفة شاملة الضريبة", Money(decimal.Round(u * r.quantity * (1 + Vat), 2))));
        var snapshot = new NoteSnapshot(emp.Rows, printer.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToList(), emp.Name, s.Name);
        var no = $"{r.printer}-{await Next(c, tx, "InkDelivery", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.ink_delivery(delivery_no,request_id,delivered_on,source,invoice_line_id,unit_cost,snapshot_json,issued_by) OUTPUT inserted.ink_delivery_id VALUES(@n,@r,@d,@s,@l,@u,@j,@b)", token,
            ("@n", no), ("@r", requestId), ("@d", DateParam(on)), ("@s", source), ("@l", source == "Invoice" ? x.InvoiceLineId : null), ("@u", unit), ("@j", JsonSerializer.Serialize(snapshot, Json)), ("@b", s.Id)));
        await Exec(c, tx, "UPDATE itam_data.ink_request SET status=N'Delivered' WHERE request_id=@i", token, ("@i", requestId));
        await Event(c, tx, r.printerId, s.Id, "Ink delivered", $"Ink delivery note {no}: {r.quantity} × {r.toner} to {r.employee} ({r.no}, {sourceText})", token);
        await Audit(c, tx, s.Id, "ink", r.no, "Deliver", null, new { no, source, x.InvoiceLineId }, null, token);
        await tx.CommitAsync(token);
        return new { id, deliveryNo = no };
    }

    async Task<(string Name, List<NoteRow> Rows)> EmployeeRows(DbConnection c, DbTransaction? tx, Guid employee, CancellationToken token)
    {
        var e = (await Rows(c, tx, "SELECT e.display_name,e.email,e.mobile,e.job_title,e.department,e.branch,e.company,e.hr_code,m.display_name FROM itam_access.employee e LEFT JOIN itam_access.employee m ON m.employee_id=e.manager_id WHERE e.employee_id=@e",
            r => (Name: r.GetString(0), Email: S(r, 1), Mobile: S(r, 2), Title: S(r, 3), Dept: S(r, 4), Branch: S(r, 5), Company: S(r, 6), Hr: S(r, 7), Manager: S(r, 8)), token, ("@e", employee))).Single();
        var rows = new List<NoteRow> { new("HR Code", "الكود الوظيفي", e.Hr), new("Employee Name", "اسم الموظف", e.Name), new("Employee Email", "البريد الالكتروني", e.Email), new("Mobile", "رقم الجوال", e.Mobile),
            new("Job Title", "المسمى الوظيفي", e.Title), new("Branch Name", "اسم الفرع", e.Branch), new("Department", "اسم القسم", e.Dept), new("Company Name", "اسم الشركة", e.Company), new("Direct Manager", "المدير المباشر", e.Manager) };
        return (e.Name, rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList());
    }

    sealed record InkNoteRow(int Id, string No, int RequestId, string Region, DateOnly On, string Snapshot, long? SignedFile, DateTime? SignedAt, int? InvoiceLine);
    static async Task<InkNoteRow> LoadInkNote(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
        => (await Rows(c, tx, "SELECT d.ink_delivery_id,d.delivery_no,d.request_id,r.region_code,d.delivered_on,d.snapshot_json,d.signed_file_id,d.signed_at_utc,d.invoice_line_id FROM itam_data.ink_delivery d JOIN itam_data.ink_request r ON r.request_id=d.request_id WHERE d.ink_delivery_id=@i",
            r => new InkNoteRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), Day(r, 4)!.Value, r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : Utc(r, 7), I(r, 8)), token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The ink delivery note was not found.");

    async Task<(DbConnection C, ItamScope S, InkNoteRow N)> InkAccess(ItamUser user, int id, CancellationToken token)
    {
        var c = await Open(token); var s = await Scope(c, null, user, token); var n = await LoadInkNote(c, null, id, token);
        var mine = await Scalar(c, null, "SELECT 1 FROM itam_data.ink_request WHERE request_id=@r AND (employee_id=@me OR requested_by=@me)", token, ("@r", n.RequestId), ("@me", s.Id)) is not null;
        if (!(mine || s.CanHandover(n.Region) || s.ViewsAll || s.PoView)) { await c.DisposeAsync(); throw new ItamAccessDenied("You cannot see this ink delivery note."); }
        return (c, s, n);
    }

    public async Task<ItamUpload> InkNote(ItamUser user, int id, CancellationToken token)
    {
        var (c, _, n) = await InkAccess(user, id, token); await using var _c = c;
        return new($"Ink delivery note {n.No}.pdf", "application/pdf", InkNotePdf(n, null));
    }

    public async Task<ItamUpload> InkSignedFile(ItamUser user, int id, CancellationToken token)
    {
        var (c, _, n) = await InkAccess(user, id, token); await using var _c = c;
        return n.SignedFile is long f ? await LoadFile(c, null, f, token) ?? throw new ArgumentException("The file was not found.") : throw new ArgumentException("No signed copy is uploaded.");
    }

    public async Task UploadInkSigned(ItamUser user, int id, ItamUpload? file, CancellationToken token)
    {
        if (file is null) throw new ArgumentException("Attach the scan of the signed ink delivery note (PDF, JPEG or PNG).");
        var type = CheckFile(file, pdfOnly: false);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var n = await LoadInkNote(c, tx, id, token);
        Require(s.CanHandover(n.Region), "You cannot upload signed ink delivery notes in this region.");
        if (n.SignedFile is not null) throw new ArgumentException($"The signed copy of {n.No} is already uploaded; it is kept unchanged.");
        var f = await SaveFile(c, tx, file, type, s.Id, token);
        await Exec(c, tx, "UPDATE itam_data.ink_delivery SET signed_file_id=@f,signed_by=@b,signed_at_utc=SYSUTCDATETIME() WHERE ink_delivery_id=@i", token, ("@f", f), ("@b", s.Id), ("@i", id));
        await Audit(c, tx, s.Id, "ink", n.No, "Signed copy", null, new { file = file.FileName }, null, token);
        await tx.CommitAsync(token);
    }

    byte[] InkNotePdf(InkNoteRow n, string? stamp)
    {
        var snap = JsonSerializer.Deserialize<NoteSnapshot>(n.Snapshot, Json)!;
        var printer = snap.Product.FirstOrDefault(p => p.En == "Printer Asset No.")?.Value ?? "";
        return BuildNote("Ink Delivery Note", "سند تسليم حبر", [("Date", "التاريخ", n.On.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), ("Document No.", "رقم المستند", n.No), ("Printer Asset No.", "رقم الطابعة", printer)],
            [("Employee Information", "بيانات الموظف", snap.Employee), ("Printer and Ink", "الطابعة والحبر", snap.Product)],
            ("I received the ink above.", "أقر باستلام الحبر الموضح أعلاه."), [("Employee Signature", "توقيع الموظف", snap.EmployeeName), ("IT Representative Signature", "توقيع ممثل تقنية المعلومات", snap.IssuedByName)], stamp);
    }

    /// <summary>A bilingual note in the layout of the delivery note: values in the middle between the English and Arabic labels.</summary>
    byte[] BuildNote(string titleEn, string titleAr, (string En, string Ar, string V)[] head, List<(string En, string Ar, List<NoteRow> Rows)> sections, (string En, string Ar) statement,
        (string En, string Ar, string? Name)[] signatures, string? stamp)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var doc = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W; const string muted = "0.38 0.47 0.55", ink = "0.07 0.09 0.15", soft = "0.93 0.95 0.99";
        void Centre(string? text, double cx, double y, double size, double width) { var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…"; doc.Text(t, cx - pdf.Width(t, size) / 2, y, size, false, ink); }
        doc.Text("ALTAWKILAT · IT Assets Management", M, doc.Y, 9, false, muted); doc.Text("إدارة أصول تقنية المعلومات", W - M, doc.Y, 9, true, muted); doc.Y -= 26;
        doc.Text(titleEn, M, doc.Y, 20, false, ink); doc.Text(titleAr, W - M, doc.Y, 18, true, ink); doc.Y -= 12; doc.Box(M, doc.Y, W - 2 * M, 2, "0.15 0.39 0.92"); doc.Y -= 18;
        if (stamp is not null) { doc.Box(M, doc.Y - 6, W - 2 * M, 22, "0.99 0.92 0.92"); doc.Text(stamp, M + 10, doc.Y, 11, false, "0.78 0.16 0.16"); doc.Text("لم يوقع بعد", W - M - 10, doc.Y, 11, true, "0.78 0.16 0.16"); doc.Y -= 28; }
        var cw = (W - 2 * M - 16) / 3;
        for (var i = 0; i < head.Length && i < 3; i++)
        { var x = M + i * (cw + 8); doc.Box(x, doc.Y - 30, cw, 44, soft); doc.Text(head[i].En, x + 8, doc.Y, 8.5, false, muted); doc.Text(head[i].Ar, x + cw - 8, doc.Y, 8.5, true, muted); Centre(head[i].V, x + cw / 2, doc.Y - 20, 12.5, cw - 16); }
        doc.Y -= 52;
        foreach (var (en, ar, rows) in sections)
        {
            doc.Box(M, doc.Y - 6, W - 2 * M, 20, "0.08 0.21 0.48"); doc.Text(en, M + 8, doc.Y, 10.5, false, "1 1 1"); doc.Text(ar, W - M - 8, doc.Y, 10.5, true, "1 1 1"); doc.Y -= 22;
            foreach (var r in rows) { doc.Text(r.En, M + 8, doc.Y, 9.5, false, muted); Centre(r.Value, W / 2, doc.Y, 10.5, 250); doc.Text(r.Ar, W - M - 8, doc.Y, 9.5, true, muted); doc.Y -= 6; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 12; }
            doc.Y -= 8;
        }
        doc.Text(statement.En, M, doc.Y, 9.5, false, ink); doc.Y -= 14; doc.Text(statement.Ar, W - M, doc.Y, 9.5, true, ink); doc.Y -= 24;
        var sigY = Math.Min(doc.Y, M + 110); var half = (W - 2 * M - 30) / 2;
        for (var i = 0; i < signatures.Length && i < 2; i++)
        {
            var x = M + i * (half + 30); var (en, ar, name) = signatures[i];
            doc.Text(en, x, sigY, 9.5, false, muted); doc.Text(ar, x + half, sigY, 9.5, true, muted); if (name is not null) doc.Fit(name, x, sigY - 18, 10, half, ink);
            doc.Line(x, sigY - 58, x + half, sigY - 58, "0.55 0.60 0.68"); doc.Text("Signature · التوقيع", x, sigY - 70, 8, false, muted);
        }
        doc.Text($"IT Assets · {head[1].V}", M, M - 10, 7.5, false, muted); doc.Text("Printed from IT Assets", W - M, M - 10, 7.5, true, muted);
        return doc.Build();
    }

    // Payment pack ---------------------------------------------------------------------------------------------------------------------
    /// <summary>Put invoices of one supplier and category in a payment pack (PAY-n) for Accounting.</summary>
    public async Task<object> CreatePack(ItamUser user, ItamPackInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var category = InvoiceCategories.Any(k => k.Code == x.Category) ? x.Category! : throw new ArgumentException("Choose the category.");
        Require(CanInvoice(s, category), "You cannot prepare payment packs for this category.");
        var ids = (x.InvoiceIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Choose the invoices for this payment pack.");
        foreach (var id in ids)
        {
            var i = (await Rows(c, tx, "SELECT invoice_no,supplier_id,category,pack_id FROM itam_data.invoice WITH (UPDLOCK) WHERE invoice_id=@i", r => (No: r.GetString(0), Sup: r.GetInt32(1), Cat: r.GetString(2), Pack: I(r, 3)), token, ("@i", id))).SingleOrDefault();
            if (i.No is null) throw new ArgumentException("An invoice was not found.");
            if (i.Sup != x.SupplierId || i.Cat != category) throw new ArgumentException($"Invoice {i.No} is of another supplier or category: one pack holds one supplier and category.");
            if (i.Pack is not null) throw new ArgumentException($"Invoice {i.No} is already in a payment pack.");
        }
        var no = $"PAY-{await Next(c, tx, "PaymentPack", token)}";
        var pack = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.payment_pack(pack_no,supplier_id,category,created_by) OUTPUT inserted.pack_id VALUES(@n,@s,@c,@b)", token, ("@n", no), ("@s", x.SupplierId), ("@c", category), ("@b", s.Id)));
        foreach (var id in ids) await Exec(c, tx, "UPDATE itam_data.invoice SET pack_id=@p WHERE invoice_id=@i", token, ("@p", pack), ("@i", id));
        await Audit(c, tx, s.Id, "pack", no, "Create", null, new { x.SupplierId, category, invoices = ids }, null, token);
        await tx.CommitAsync(token);
        return new { id = pack, packNo = no };
    }

    /// <summary>The payment pack PDF: the Inter-Office Memo cover (Date | Invoice No. | Amount | Total; checked by IT, approved by
    /// the Head of Department, signed by the Accountant), then for each invoice: the invoice, its order, and its ink delivery
    /// notes (signed copy, or the issued note marked "Not yet signed").</summary>
    public async Task<ItamUpload> PackPdf(ItamUser user, int packId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s), "You cannot see payment packs.");
        var p = (await Rows(c, null, "SELECT pk.pack_no,su.name_en,pk.category,pk.created_at_utc,e.display_name FROM itam_data.payment_pack pk JOIN itam_ref.supplier su ON su.supplier_id=pk.supplier_id LEFT JOIN itam_access.employee e ON e.employee_id=pk.created_by WHERE pk.pack_id=@p",
            r => (No: r.GetString(0), Supplier: r.GetString(1), Category: r.GetString(2), At: Utc(r, 3), By: S(r, 4)), token, ("@p", packId))).SingleOrDefault();
        if (p.No is null) throw new ArgumentException("The payment pack was not found.");
        var invoices = await Rows(c, null, "SELECT i.invoice_id,i.invoice_no,i.invoice_date,i.total,i.file_id,i.order_id FROM itam_data.invoice i WHERE i.pack_id=@p ORDER BY i.invoice_date,i.invoice_id",
            r => (Id: r.GetInt32(0), No: r.GetString(1), Date: Day(r, 2)!.Value, Total: r.GetDecimal(3), File: r.GetInt64(4), Order: I(r, 5)), token, ("@p", packId));
        var pack = new PdfDocument();
        void Append(byte[] bytes) { using var ms = new MemoryStream(bytes); using var input = PdfReader.Open(ms, PdfDocumentOpenMode.Import); foreach (var page in input.Pages) pack.AddPage(page); }
        Append(Cover(p.No, p.Supplier, p.Category, invoices.Select(i => (i.Date, i.No, i.Total)).ToList(), p.By));
        foreach (var inv in invoices)
        {
            if (await LoadFile(c, null, inv.File, token) is { } f) { try { Append(f.Content); } catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException) { } }
            if (inv.Order is int o)
            {
                var o2 = (await Rows(c, null, "SELECT o.order_no,su.name_en,o.created_at_utc,e.display_name,o.note FROM itam_data.supplier_order o JOIN itam_ref.supplier su ON su.supplier_id=o.supplier_id LEFT JOIN itam_access.employee e ON e.employee_id=o.created_by WHERE o.order_id=@o",
                    r => (No: r.GetString(0), Sup: r.GetString(1), At: Utc(r, 2), By: S(r, 3), Note: S(r, 4)), token, ("@o", o))).Single();
                Append(OrderSheet(o2.No, o2.Sup, o2.At, o2.By, o2.Note, await Rows(c, null, InkSelect + "WHERE r.order_id=@o ORDER BY r.request_id", MapInk, token, ("@o", o))));
            }
            foreach (var nid in await Rows(c, null, "SELECT d.ink_delivery_id FROM itam_data.ink_delivery d JOIN itam_data.invoice_line l ON l.line_id=d.invoice_line_id WHERE l.invoice_id=@i ORDER BY d.ink_delivery_id", r => r.GetInt32(0), token, ("@i", inv.Id)))
            {
                var n = await LoadInkNote(c, null, nid, token);
                var signed = n.SignedFile is long sf ? await LoadFile(c, null, sf, token) : null;
                if (signed is { MediaType: "application/pdf" }) { try { Append(signed.Content); continue; } catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException) { } }
                else if (signed is not null)
                {
                    var page = pack.AddPage(); page.Size = PdfSharp.PageSize.A4;
                    using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page); using var img = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(signed.Content));
                    var scale = Math.Min((page.Width.Point - 56) / img.PointWidth, (page.Height.Point - 56) / img.PointHeight); gfx.DrawImage(img, 28, 28, img.PointWidth * scale, img.PointHeight * scale); continue;
                }
                Append(InkNotePdf(n, "NOT YET SIGNED: the employee has not signed this ink delivery note yet"));
            }
        }
        using var output = new MemoryStream(); pack.Save(output, false);
        return new($"Payment pack {p.No}.pdf", "application/pdf", output.ToArray());
    }

    byte[] Cover(string no, string supplier, string category, List<(DateOnly Date, string No, decimal Total)> invoices, string? by)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var d = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W; const string muted = "0.38 0.47 0.55", ink = "0.07 0.09 0.15";
        d.Text("ALTAWKILAT", M, d.Y, 11, false, muted); d.Text("التوكيلات", W - M, d.Y, 11, true, muted); d.Y -= 30;
        d.Text("INTER-OFFICE MEMORANDUM", M, d.Y, 20, false, ink); d.Text("مذكرة داخلية", W - M, d.Y, 18, true, ink); d.Y -= 12; d.Box(M, d.Y, W - 2 * M, 2, "0.15 0.39 0.92"); d.Y -= 24;
        foreach (var (k, v) in new[] { ("Date", DateTime.UtcNow.AddHours(3).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)), ("To", "Accounts Department"), ("From", "IT Department"),
            ("Subject", $"Payment of {CategoryLabel(category).ToLowerInvariant()} invoices — {supplier}"), ("Reference", no) })
        { d.Text(k + ":", M, d.Y, 10.5, false, muted); d.Fit(v, M + 80, d.Y, 10.5, W - 2 * M - 80, ink); d.Y -= 17; }
        d.Y -= 10;
        d.Fit($"Attached please find {invoices.Count} invoice{(invoices.Count == 1 ? "" : "s")} from {supplier} which {(invoices.Count == 1 ? "is" : "are")} due for payment.", M, d.Y, 11, W - 2 * M, ink); d.Y -= 24;
        double[] col = [M, M + 150, M + 340];
        d.Box(M, d.Y - 6, W - 2 * M, 20, "0.93 0.95 0.99"); d.Text("Date", col[0] + 6, d.Y, 9.5, false, muted); d.Text("Invoice No.", col[1] + 6, d.Y, 9.5, false, muted); d.Text("Amount (SAR, incl. VAT)", col[2] + 6, d.Y, 9.5, false, muted); d.Y -= 22;
        foreach (var i in invoices)
        { d.Text(i.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), col[0] + 6, d.Y, 10.5, false, ink); d.Fit(i.No, col[1] + 6, d.Y, 10.5, 180, ink); d.Text(i.Total.ToString("#,##0.00", CultureInfo.InvariantCulture), col[2] + 6, d.Y, 10.5, false, ink); d.Y -= 6; d.Line(M, d.Y, W - M, d.Y); d.Y -= 14; }
        d.Text("Total (SAR)", col[1] + 6, d.Y, 11, false, ink); d.Text(invoices.Sum(i => i.Total).ToString("#,##0.00", CultureInfo.InvariantCulture), col[2] + 6, d.Y, 11.5, false, ink); d.Y -= 44;
        var w = (W - 2 * M - 40) / 3;
        foreach (var (k, i) in new[] { "Checked by (IT)", "Approved by (Head of Department)", "Accountant" }.Select((k, i) => (k, i)))
        { var x = M + i * (w + 20); d.Text(k, x, d.Y, 9.5, false, muted); if (i == 0 && by is not null) d.Fit(by, x, d.Y - 16, 10, w, ink); d.Line(x, d.Y - 56, x + w, d.Y - 56, "0.55 0.60 0.68"); d.Text("Signature and date", x, d.Y - 68, 8, false, muted); }
        d.Text($"IT Assets · {no}", M, M - 10, 7.5, false, muted);
        return d.Build();
    }

    /// <summary>Record when the pack went to Accounting, and (optionally) when the supplier was paid.</summary>
    public async Task PackDates(ItamUser user, int packId, ItamPackDates x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var cat = await Scalar(c, tx, "SELECT category FROM itam_data.payment_pack WHERE pack_id=@p", token, ("@p", packId)) as string ?? throw new ArgumentException("The payment pack was not found.");
        Require(CanInvoice(s, cat), "You cannot change this payment pack.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        if (x.SentOn > today || x.PaidOn > today) throw new ArgumentException("Dates cannot be in the future.");
        if (x.PaidOn is not null && x.SentOn is null && await Scalar(c, tx, "SELECT sent_on FROM itam_data.payment_pack WHERE pack_id=@p", token, ("@p", packId)) is null) throw new ArgumentException("Record when it went to Accounting first.");
        await Exec(c, tx, "UPDATE itam_data.payment_pack SET sent_on=COALESCE(@s,sent_on),sent_by=CASE WHEN @s IS NULL THEN sent_by ELSE @b END,paid_on=COALESCE(@p,paid_on),paid_note=COALESCE(@n,paid_note) WHERE pack_id=@id", token,
            ("@s", DateParam(x.SentOn)), ("@b", s.Id), ("@p", DateParam(x.PaidOn)), ("@n", Optional(x.PaidNote, "Payment note", 200)), ("@id", packId));
        await Audit(c, tx, s.Id, "pack", packId.ToString(CultureInfo.InvariantCulture), "Dates", null, new { x.SentOn, x.PaidOn, x.PaidNote }, null, token);
        await tx.CommitAsync(token);
    }
}
