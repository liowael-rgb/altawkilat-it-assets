using System.Data.Common;
using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QRCoder;

namespace Itam.App;

/// <summary>The request document for Accounting (plan v1.9, design approved 20 October 2026): one bilingual A4 page in the
/// style of the delivery notes for the new device and IT accessory purchase requests: header, employee information with the
/// cost center, request details, the cost table with subtotal, VAT 15%, grand total and the amount in words, the supplier or
/// shop, the approvals recorded in IT Assets, the IT Manager and Accounting signatures, and a QR code to check the request.</summary>
public sealed partial class ItamStore
{
    string BaseUrl => LabelBaseUrl.EndsWith("/a/", StringComparison.Ordinal) ? LabelBaseUrl[..^3] : LabelBaseUrl.TrimEnd('/');

    public async Task<ItamUpload> RequestPdf(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var r = await LoadRequest(c, null, id, token);
        Require(await SeesRequest(c, s, r, token), "You cannot see this request.");
        if (r.Source is not ("NewDevice" or "Accessory")) throw new ArgumentException("The request document is for a new device or an IT accessory purchase.");
        return new($"Request {r.No}.pdf", "application/pdf", await RequestDocument(c, id, s.Name, token));
    }

    /// <summary>New device after delivery: the request document and the delivery note together, for Accounting.</summary>
    public async Task<ItamUpload> RequestPack(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var r = await LoadRequest(c, null, id, token);
        Require(await SeesRequest(c, s, r, token), "You cannot see this request.");
        var delivery = await Scalar(c, null, "SELECT delivery_id FROM itam_data.request WHERE request_id=@i", token, ("@i", id)) as int? ?? throw new ArgumentException("The request is not delivered yet.");
        var pack = new PdfDocument();
        void Append(byte[] bytes) { using var ms = new MemoryStream(bytes); using var input = PdfReader.Open(ms, PdfDocumentOpenMode.Import); foreach (var page in input.Pages) pack.AddPage(page); }
        Append(await RequestDocument(c, id, s.Name, token));
        Append(NotePdf(NoteKind.Delivery, await LoadDelivery(c, null, delivery, false, token), null, null));
        using var output = new MemoryStream(); pack.Save(output, false);
        return new($"Request {r.No} with delivery note.pdf", "application/pdf", output.ToArray());
    }

    async Task<byte[]> RequestDocument(DbConnection c, int id, string printedBy, CancellationToken token)
    {
        var pdf = Pdf ?? throw new InvalidOperationException("The PDF font is not configured.");
        dynamic q = (await Rows(c, null, RequestSelect + "WHERE q.request_id=@i", MapRequest, token, ("@i", id))).Single();
        var e = (await Rows(c, null, "SELECT e.hr_code,e.sap_id,e.job_title,e.branch,e.company,e.email FROM itam_access.employee e JOIN itam_data.request q ON q.employee_id=e.employee_id WHERE q.request_id=@i",
            r => (Hr: S(r, 0), Sap: S(r, 1), Title: S(r, 2), Branch: S(r, 3), Company: S(r, 4), Email: S(r, 5)), token, ("@i", id))).Single();
        var lines = await Rows(c, null, "SELECT item,specification,quantity,unit_cost FROM itam_data.request_line WHERE request_id=@i ORDER BY line_no",
            r => (Item: r.GetString(0), Spec: S(r, 1), Qty: r.GetInt32(2), Unit: r.GetDecimal(3)), token, ("@i", id));
        var inv = CultureInfo.InvariantCulture; string At(DateTime? t) => t is DateTime x ? x.AddHours(3).ToString("dd/MM/yyyy HH:mm", inv) : "";
        var doc = pdf.Start(); const double M = ItamPdf.Doc.M, W = ItamPdf.Doc.W; const string muted = "0.35 0.35 0.35", ink = "0 0 0", soft = "0.93 0.93 0.93", band = "0.86 0.86 0.86";
        string source = q.source; var accessory = source == "Accessory";
        var (titleEn, titleAr) = accessory ? ("IT Accessory Purchase Request", "طلب شراء ملحقات تقنية المعلومات") : ("New Device Purchase Request", "طلب شراء جهاز جديد");
        void Centre(string? text, double cx, double y, double size, double width) { var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…"; doc.Text(t, cx - pdf.Width(t, size) / 2, y, size, false, ink); }
        void Section(string en, string ar) { doc.Box(M, doc.Y - 6, W - 2 * M, 20, band); doc.Text(en, M + 8, doc.Y, 10.5, false, ink, strong: true); doc.Text(ar, W - M - 8, doc.Y, 10.5, true, ink); doc.Y -= 22; }
        void Row(string en, string ar, string? v) { if (string.IsNullOrWhiteSpace(v)) return; doc.Text(en, M + 8, doc.Y, 9, false, muted); Centre(v, W / 2, doc.Y, 10, 260); doc.Text(ar, W - M - 8, doc.Y, 9, true, muted); doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11; }
        // Header
        doc.Logo(M, doc.Y + 10, 96); doc.Text("إدارة أصول تقنية المعلومات", W - M, doc.Y, 9, true, muted); doc.Text("IT Assets Management", W - M, doc.Y - 13, 9, true, muted); doc.Y -= 40;
        doc.Text(titleEn, M, doc.Y, 18, false, ink, strong: true); doc.Text(titleAr, W - M, doc.Y, 16, true, ink); doc.Y -= 10; doc.Line(M, doc.Y, W - M, doc.Y, ink); doc.Y -= 16;
        var cw = (W - 2 * M - 16) / 3; DateTime requestedAt = q.requestedAt;
        (string, string, string)[] head = [("Request Date", "تاريخ الطلب", requestedAt.AddHours(3).ToString("dd/MM/yyyy", inv)), ("Request No.", "رقم الطلب", (string)q.no), ("Request Type", "نوع الطلب", accessory ? "IT Accessory" : "New Device")];
        for (var i = 0; i < 3; i++) { var x = M + i * (cw + 8); doc.Box(x, doc.Y - 28, cw, 40, soft); doc.Text(head[i].Item1, x + 7, doc.Y, 8, false, muted); doc.Text(head[i].Item2, x + cw - 7, doc.Y, 8, true, muted); Centre(head[i].Item3, x + cw / 2, doc.Y - 19, 12, cw - 14); }
        doc.Y -= 46;
        Section("Employee Information", "بيانات الموظف");
        Row("Employee Name", "اسم الموظف", q.employee); Row("HR Code", "الكود الوظيفي", e.Hr); Row("SAP ID", "رقم ساب", e.Sap); Row("Job Title", "المسمى الوظيفي", e.Title);
        Row("Cost Center (Department / Region)", "مركز التكلفة", string.Join(" / ", new[] { (string?)q.department, (string?)q.region }.Where(v => !string.IsNullOrEmpty(v)))); Row("Branch", "الفرع", e.Branch);
        Section("Request Details", "تفاصيل الطلب");
        Row("Request", "الطلب", (string)q.type == "Replacement" ? $"Replacement of {q.replaced}" : "New");
        Row("Technical Assessment", "التقييم الفني", q.assessmentNo ?? ((bool)q.hasAssessmentFile ? "Attached to the request" : null));
        var desc = (string)q.description; var words = desc.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries); var line = ""; var shown = 0;
        doc.Text("Reason", M + 8, doc.Y, 9, false, muted); doc.Text("السبب", W - M - 8, doc.Y, 9, true, muted);
        foreach (var w in words) { var t = line.Length == 0 ? w : line + " " + w; if (pdf.Width(t, 9.5) > 300 && line.Length > 0) { if (shown++ < 3) { Centre(line, W / 2, doc.Y, 9.5, 300); doc.Y -= 12; } line = w; } else line = t; }
        if (line.Length > 0 && shown < 4) { Centre(line, W / 2, doc.Y, 9.5, 300); doc.Y -= 12; }
        doc.Y += 7; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11;
        Row(accessory ? "Shop" : "Supplier", accessory ? "المحل" : "المورد", accessory ? q.shop : q.supplier);
        // Cost table
        Section("Cost", "التكلفة");
        double[] col = [M, M + 150, M + 330, M + 375, M + 445, W - M];
        doc.Text("Item", col[0] + 6, doc.Y, 8.5, false, muted, strong: true); doc.Text("Specification", col[1] + 6, doc.Y, 8.5, false, muted, strong: true); doc.Text("Qty", col[3] - 6, doc.Y, 8.5, true, muted, strong: true);
        doc.Text("Unit (SAR)", col[4] - 6, doc.Y, 8.5, true, muted, strong: true); doc.Text("Total (SAR)", col[5] - 6, doc.Y, 8.5, true, muted, strong: true); doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y, ink); doc.Y -= 12;
        foreach (var l in lines)
        {
            doc.Fit(l.Item, col[0] + 6, doc.Y, 9.5, col[1] - col[0] - 10, ink); doc.Fit(l.Spec, col[1] + 6, doc.Y, 9, col[2] - col[1] + 30, ink);
            doc.Text(l.Qty.ToString(inv), col[3] - 6, doc.Y, 9.5, true, ink); doc.Text(l.Unit.ToString("#,##0.00", inv), col[4] - 6, doc.Y, 9.5, true, ink); doc.Text((l.Qty * l.Unit).ToString("#,##0.00", inv), col[5] - 6, doc.Y, 9.5, true, ink);
            doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11;
        }
        decimal net = q.net, vat = q.vat, grand = q.grand;
        foreach (var (en, v, strong) in new[] { ("Subtotal", net, false), ($"VAT {Vat * 100:0}%", vat, false), ("Grand total incl. VAT", grand, true) })
        { doc.Text(en, col[4] - 6, doc.Y, 9.5, true, ink, strong); doc.Text(v.ToString("#,##0.00", inv), col[5] - 6, doc.Y, 9.5, true, ink, strong); doc.Y -= 13; }
        doc.Fit($"Amount in words: {AmountInWords(grand)}", M + 6, doc.Y, 9, W - 2 * M - 12, ink); doc.Y -= 18;
        // Approvals recorded electronically
        Section("Approvals recorded in IT Assets", "الموافقات المسجلة في النظام");
        var steps = new List<(string Step, string? Who, string Decision, DateTime? When)> { ("Submitted", q.requestedBy, "Request", (DateTime?)q.requestedAt), ("Request approved (manager / IT Manager)", q.approvedBy, "Approved", (DateTime?)q.approvedAt),
            ("Priced", q.pricedBy, accessory ? "Priced in region" : "Cost table", (DateTime?)q.pricedAt), ("Cost approved (manager / IT Manager)", q.costApprovedBy, "Approved", (DateTime?)q.costApprovedAt) };
        foreach (var st in steps.Where(x => x.Who is not null))
        { doc.Text(st.Step, M + 8, doc.Y, 9, false, muted); doc.Fit(st.Who, M + 210, doc.Y, 9.5, 170, ink); doc.Text(st.Decision, M + 385, doc.Y, 9, false, ink); doc.Text(At(st.When), W - M - 8, doc.Y, 9, true, ink); doc.Y -= 5; doc.Line(M, doc.Y, W - M, doc.Y); doc.Y -= 11; }
        // Signatures by hand and the QR code to check the request
        var sigY = Math.Min(doc.Y - 6, M + 118); var half = (W - 2 * M - 120) / 2;
        foreach (var (en, ar, i) in new[] { ("IT Manager", "مدير تقنية المعلومات", 0), ("Received by Accounting (name, signature, date)", "استلام المحاسبة", 1) })
        { var x = M + i * (half + 20); doc.Text(en, x, sigY, 9, false, muted); doc.Text(ar, x + half, sigY - 12, 9, true, muted); doc.Line(x, sigY - 54, x + half, sigY - 54, "0.45 0.45 0.45"); doc.Text("Signature · التوقيع", x, sigY - 65, 7.5, false, muted); }
        using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode($"{BaseUrl}/r/{q.no}", QRCodeGenerator.ECCLevel.M);
        var mx = data.ModuleMatrix; var qr = 78.0; var cell = qr / mx.Count; double qx = W - M - qr, qy = sigY - 70;
        for (var rr = 0; rr < mx.Count; rr++) for (var k = 0; k < mx[rr].Length; k++) if (mx[rr][k]) doc.Box(qx + k * cell, qy + (mx.Count - 1 - rr) * cell, cell + 0.05, cell + 0.05, "0 0 0");
        doc.Text("Check the approvals", qx + qr / 2 - pdf.Width("Check the approvals", 7) / 2, qy - 9, 7, false, muted);
        doc.Text($"Printed {DateTime.UtcNow.AddHours(3).ToString("dd/MM/yyyy HH:mm", inv)} by {printedBy} · IT Assets · {q.no}", M, M - 10, 7.5, false, muted);
        return doc.Build();
    }

    /// <summary>The grand total in English words, Saudi riyals and halalas (for example "One thousand two hundred riyals and fifty halalas").</summary>
    public static string AmountInWords(decimal amount)
    {
        string[] ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        string Below1000(long n)
        {
            var parts = new List<string>();
            if (n >= 100) { parts.Add(ones[n / 100] + " hundred"); n %= 100; }
            if (n >= 20) { parts.Add(tens[n / 10] + (n % 10 > 0 ? "-" + ones[n % 10] : "")); }
            else if (n > 0 || parts.Count == 0) parts.Add(ones[n]);
            return string.Join(" ", parts);
        }
        string Words(long n)
        {
            if (n == 0) return "zero";
            var parts = new List<string>();
            foreach (var (size, name) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1_000L, "thousand") })
                if (n >= size) { parts.Add(Below1000(n / size) + " " + name); n %= size; }
            if (n > 0) parts.Add(Below1000(n));
            return string.Join(" ", parts);
        }
        var riyals = (long)Math.Floor(amount); var halalas = (int)Math.Round((amount - riyals) * 100);
        var text = Words(riyals) + (riyals == 1 ? " riyal" : " riyals") + (halalas > 0 ? $" and {Words(halalas)} halala{(halalas == 1 ? "" : "s")}" : "");
        return char.ToUpperInvariant(text[0]) + text[1..] + " only";
    }
}
