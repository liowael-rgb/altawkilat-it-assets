using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Itam.App;

public sealed partial class ItamStore
{
    /// <summary>The bilingual PDF writer (needs a font with Arabic letters, for example Windows Arial).</summary>
    public ItamPdf? Pdf { get; init; }

    static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture) + " SAR";

    /// <summary>PO Delivery Report (cover of the PO pack): the PO, its counts, its lines and one line per device.</summary>
    byte[] Report(PoView p, string printedBy, DateTime at)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        var d = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W;
        const string muted = "0.38 0.47 0.55", ink = "0.16 0.24 0.31";
        void Header()
        {
            d.Text("IT Assets Management", M, d.Y, 9, false, muted); d.Text("إدارة أصول تقنية المعلومات", W - M, d.Y, 9, true, muted); d.Y -= 22;
            d.Text($"PO Delivery Report — PO {p.Number}", M, d.Y, 15); d.Text("تقرير تسليم أمر الشراء", W - M, d.Y, 13, true); d.Y -= 12; d.Line(M, d.Y, W - M, d.Y); d.Y -= 16;
        }
        void Need(double space) { if (d.Y - space < M + 30) { d.NewPage(); Header(); } }
        Header();
        // PO fields: English label, value, Arabic label.
        (string En, string Ar, string? Value)[] fields =
        [
            ("PO number", "رقم أمر الشراء", p.Number), ("PO date", "تاريخ أمر الشراء", p.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
            ("Supplier", "المورد", p.Supplier + (p.SupplierVat is null ? "" : $" (VAT {p.SupplierVat})")),
            ("Raised by", "صادر من", p.RaisedBy), ("Request", "رقم الطلب", p.RequestRef), ("Invoice number", "رقم الفاتورة", p.Invoice)
        ];
        foreach (var f in fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        { d.Text(f.En, M, d.Y, 9.5); d.Fit(f.Value, M + 120, d.Y, 10, 300); d.Text(f.Ar, W - M, d.Y, 9.5, true); d.Y -= 15; }
        d.Y -= 6;
        // Counts.
        var ordered = p.Lines.Sum(l => l.Quantity); var received = p.Devices.Count; var inStock = p.Devices.Count(x => x.Status == "InStore" && !x.Delivered);
        int waiting = p.Devices.Count(x => x.Delivered && !x.Signed), signed = p.Devices.Count(x => x.Signed);
        (string En, string Ar, int N)[] counts =
        [
            ("Ordered", "الكمية المطلوبة", ordered), ("Received by IT", "المستلم من قبل تقنية المعلومات", received), ("Still to come", "المتبقي", ordered - received),
            ("In IT stock", "في مخزون تقنية المعلومات", inStock), ("Reserved, pending delivery", "محجوز بانتظار التسليم", 0),
            ("Delivered, waiting for signed copy", "مسلم بانتظار النسخة الموقعة", waiting), ("Delivered and signed", "مسلم وموقع", signed)
        ];
        d.Box(M, d.Y - counts.Length * 14 - 4, W - 2 * M, counts.Length * 14 + 14, "0.95 0.97 0.99");
        foreach (var x in counts) { d.Text(x.En, M + 8, d.Y, 9.5); d.Text(x.N.ToString(CultureInfo.InvariantCulture), M + 260, d.Y, 10, true); d.Text(x.Ar, W - M - 8, d.Y, 9.5, true); d.Y -= 14; }
        d.Y -= 16;
        // Lines with totals (VAT 15%).
        Need(60);
        d.Text("PO lines", M, d.Y, 11.5); d.Text("بنود أمر الشراء", W - M, d.Y, 11, true); d.Y -= 15;
        double[] lc = [M, M + 26, M + 110, M + 300, M + 345, M + 430];
        d.Text("Line", lc[0], d.Y, 8.5, false, muted); d.Text("Type", lc[1], d.Y, 8.5, false, muted); d.Text("Manufacturer and model", lc[2], d.Y, 8.5, false, muted);
        d.Text("Qty", lc[3], d.Y, 8.5, false, muted); d.Text("Unit cost", lc[4], d.Y, 8.5, false, muted); d.Text("Line total", lc[5], d.Y, 8.5, false, muted); d.Y -= 4; d.Line(M, d.Y, W - M, d.Y); d.Y -= 11;
        foreach (var l in p.Lines)
        {
            Need(14);
            d.Text(l.No.ToString(CultureInfo.InvariantCulture), lc[0], d.Y, 8.5, false, ink); d.Fit(l.TypeName, lc[1], d.Y, 8.5, 80); d.Fit(string.Join(" ", new[] { l.Manufacturer, l.Model }.Where(v => !string.IsNullOrWhiteSpace(v))), lc[2], d.Y, 8.5, 185);
            d.Text(l.Quantity.ToString(CultureInfo.InvariantCulture), lc[3], d.Y, 8.5, false, ink); d.Text(Money(l.UnitCost), lc[4], d.Y, 8.5, false, ink); d.Text(Money(l.Quantity * l.UnitCost), lc[5], d.Y, 8.5, false, ink); d.Y -= 12;
        }
        var net = p.Lines.Sum(l => l.Quantity * l.UnitCost); var vat = decimal.Round(net * Vat, 2);
        d.Y -= 2; d.Line(M + 180, d.Y + 8, W - M, d.Y + 8);
        foreach (var (en, ar, v) in new[] { ("Subtotal", "المجموع", net), ("VAT 15%", "ضريبة القيمة المضافة", vat), ("Total including VAT", "الإجمالي شامل الضريبة", net + vat) })
        { Need(13); d.Text(en, M + 180, d.Y, 9); d.Text(ar, lc[5] - 8, d.Y, 8.5, true, muted); d.Text(Money(v), lc[5], d.Y, 9, false, ink); d.Y -= 13; }
        d.Y -= 14;
        // Devices.
        Need(60);
        d.Text("Devices received on this PO", M, d.Y, 11.5); d.Text("الأجهزة المستلمة على أمر الشراء", W - M, d.Y, 11, true); d.Y -= 15;
        double[] dc = [M, M + 55, M + 135, M + 222, M + 278, M + 385];
        d.Text("Asset No.", dc[0], d.Y, 8.5, false, muted); d.Text("Model", dc[1], d.Y, 8.5, false, muted); d.Text("Serial / service tag", dc[2], d.Y, 8.5, false, muted);
        d.Text("Status", dc[3], d.Y, 8.5, false, muted); d.Text("Kept at / employee", dc[4], d.Y, 8.5, false, muted); d.Text("Delivery note, signed", dc[5], d.Y, 8.5, false, muted); d.Y -= 4; d.Line(M, d.Y, W - M, d.Y); d.Y -= 11;
        if (p.Devices.Count == 0) { d.Text("No device received yet.", M, d.Y, 9, false, ink); d.Y -= 12; }
        foreach (var x in p.Devices)
        {
            Need(14);
            d.Text(x.AssetNo, dc[0], d.Y, 8.5, false, ink); d.Fit(x.Model ?? "", dc[1], d.Y, 8.5, 76); d.Fit(x.Serial ?? x.ServiceTag ?? "", dc[2], d.Y, 8.5, 84);
            d.Fit(ItamStatuses.Label(x.Status), dc[3], d.Y, 8.5, 54); d.Fit(x.Employee ?? x.KeptAt, dc[4], d.Y, 8.5, 104);
            if (!x.Delivered) d.Text("Not yet delivered", dc[5], d.Y, 8.5, false, muted);
            else d.Fit($"{x.DeliveryNo}, {(x.SignedAt is DateTime s ? s.AddHours(3).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "not yet signed")}", dc[5], d.Y, 8.5, W - M - dc[5], x.Signed ? ink : "0.60 0.36 0.00");
            d.Y -= 12;
        }
        var missing = (ordered - received) + p.Devices.Count(x => !x.Signed);
        d.Y -= 8; Need(30);
        if (missing > 0) { d.Text($"Not yet complete: {ordered - received} still to come, {p.Devices.Count(x => !x.Delivered)} not yet delivered, {waiting} waiting for the signed copy.", M, d.Y, 9, false, "0.60 0.36 0.00"); d.Y -= 18; }
        Need(70); d.Y -= 10;
        d.Text("IT Assets Officer: ______________________   Date: ____________", M, d.Y, 9.5); d.Text("مسؤول أصول تقنية المعلومات", W - M, d.Y, 9, true); d.Y -= 26;
        d.Text("Received by Accounting: _________________   Date: ____________", M, d.Y, 9.5); d.Text("استلام المحاسبة", W - M, d.Y, 9, true); d.Y -= 22;
        d.Text($"Printed {at.AddHours(3):dd/MM/yyyy HH:mm} by {printedBy}", M, d.Y, 8, false, muted);
        return d.Build();
    }

    async Task<(PoView Po, string Name)> PrintAccess(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.PoView, "You cannot print purchase order documents.");
        return (await LoadPo(c, null, id, token), s.Name);
    }

    public async Task<ItamUpload> PoReport(ItamUser user, int id, CancellationToken token)
    {
        var (p, name) = await PrintAccess(user, id, token);
        return new($"PO {p.Number} delivery report.pdf", "application/pdf", Report(p, name, UtcNow));
    }

    /// <summary>PO pack: the report, the PO document, then the delivery note of every device on the PO (the signed copy;
    /// otherwise the issued note marked "Not yet signed"). Delivery notes arrive with build step 2. Each print is recorded.</summary>
    public async Task<ItamUpload> PoPack(ItamUser user, int id, CancellationToken token)
    {
        var (p, name) = await PrintAccess(user, id, token);
        await using var c = await Open(token);
        var document = await LoadFile(c, null, p.DocumentFile, token);
        var pack = new PdfDocument();
        void Append(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var input = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
            foreach (var page in input.Pages) pack.AddPage(page);
        }
        Append(Report(p, name, UtcNow));
        try { if (document is not null) Append(document.Content); }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException)
        {
            // A protected or damaged PO document: say so on its own page; it can still be printed from the PO page.
            var pdf = Pdf!; var d = pdf.Start(); d.Text($"The PO document {document?.FileName} could not be added (protected or damaged PDF).", ItamPdf.Doc.M, d.Y, 11);
            d.Y -= 16; d.Text("Print it from the PO page and add it to this pack.", ItamPdf.Doc.M, d.Y, 10); Append(d.Build());
        }
        // The delivery note of every delivered device, in Asset No. order: the copy the employee signed on paper, or the
        // issued note marked "Not yet signed". Devices not yet delivered are listed on the cover only.
        int notesSigned = 0, notesUnsigned = 0;
        void AppendImage(byte[] bytes)
        {
            var page = pack.AddPage(); page.Size = PdfSharp.PageSize.A4;
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            using var img = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(bytes));
            double margin = 28, maxW = page.Width.Point - 2 * margin, maxH = page.Height.Point - 2 * margin;
            var scale = Math.Min(maxW / img.PointWidth, maxH / img.PointHeight);
            gfx.DrawImage(img, margin, margin, img.PointWidth * scale, img.PointHeight * scale);
        }
        foreach (var x in p.Devices.Where(x => x.Delivered))
        {
            var dr = await LoadDelivery(c, null, x.DeliveryId!.Value, false, token);
            var signedCopy = dr.SignedFile is long sf ? await LoadFile(c, null, sf, token) : null;
            if (signedCopy is null) { Append(NotePdf(NoteKind.Delivery, dr, null, null, "NOT YET SIGNED: the employee has not signed this delivery note yet")); notesUnsigned++; continue; }
            try { if (signedCopy.MediaType == "application/pdf") Append(signedCopy.Content); else AppendImage(signedCopy.Content); }
            catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotSupportedException or ArgumentException)
            { Append(NotePdf(NoteKind.Delivery, dr, null, null, "The signed copy could not be added: print it from the asset page")); }
            notesSigned++;
        }
        var pages = pack.PageCount;
        using var output = new MemoryStream(); pack.Save(output, false);
        await using (var tx = await c.BeginTransactionAsync(token))
        {
            var s = await Scope(c, tx, user, token);
            await Exec(c, tx, "INSERT itam_data.pack_print(po_id,printed_by,devices,notes_signed,notes_unsigned) VALUES(@p,@b,@d,@ns,@nu)", token, ("@p", id), ("@b", s.Id), ("@d", p.Devices.Count), ("@ns", notesSigned), ("@nu", notesUnsigned));
            await Audit(c, tx, s.Id, "po", p.Number, "Print pack", null, new { devices = p.Devices.Count, pages, notesSigned, notesUnsigned }, null, token);
            await tx.CommitAsync(token);
        }
        return new($"PO {p.Number} pack.pdf", "application/pdf", output.ToArray());
    }
}
