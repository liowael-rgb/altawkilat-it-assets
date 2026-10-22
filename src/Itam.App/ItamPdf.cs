using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Itam.App;

/// <summary>Writes bilingual A4 pages (English left, Arabic right): embeds a TrueType font with Arabic presentation forms
/// (for example Windows Arial), joins Arabic letters and writes right-to-left text in visual order. Used for the PO
/// Delivery Report now and the delivery, receipt and assessment notes later.</summary>
public sealed class ItamPdf
{
    readonly byte[] font; readonly int unitsPerEm; readonly ushort[] advance; readonly Dictionary<int, ushort> cmap = [];
    readonly string fontName;

    public ItamPdf(byte[] ttf, string fontName = "Arial")
    {
        font = ttf; this.fontName = new string(fontName.Where(char.IsLetterOrDigit).ToArray());
        var tables = new Dictionary<string, (int Offset, int Length)>();
        int count = U16(4);
        for (var i = 0; i < count; i++) { var p = 12 + i * 16; tables[Encoding.ASCII.GetString(ttf, p, 4)] = ((int)U32(p + 8), (int)U32(p + 12)); }
        unitsPerEm = U16(tables["head"].Offset + 18);
        int numGlyphs = U16(tables["maxp"].Offset + 4), metrics = U16(tables["hhea"].Offset + 34);
        advance = new ushort[numGlyphs];
        var hmtx = tables["hmtx"].Offset; ushort last = 0;
        for (var g = 0; g < numGlyphs; g++) { if (g < metrics) last = (ushort)U16(hmtx + g * 4); advance[g] = last; }
        var cm = tables["cmap"].Offset; int subtables = U16(cm + 2); int best = -1, bestFormat = 0;
        for (var i = 0; i < subtables; i++)
        {
            int platform = U16(cm + 4 + i * 8), encoding = U16(cm + 6 + i * 8); var offset = cm + (int)U32(cm + 8 + i * 8); int format = U16(offset);
            if (platform == 3 && (encoding == 10 && format == 12 || encoding == 1 && format == 4 && bestFormat != 12)) { best = offset; bestFormat = format; }
        }
        if (best < 0) throw new InvalidOperationException("The PDF font has no Unicode character map.");
        if (bestFormat == 4)
        {
            int segs = U16(best + 6) / 2; int ends = best + 14, starts = ends + segs * 2 + 2, deltas = starts + segs * 2, ranges = deltas + segs * 2;
            for (var s = 0; s < segs; s++)
            {
                int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = (short)U16(deltas + s * 2), range = U16(ranges + s * 2);
                for (var ch = start; ch <= end && ch != 0xFFFF; ch++)
                {
                    int g = range == 0 ? (ch + delta) & 0xFFFF : U16(ranges + s * 2 + range + (ch - start) * 2);
                    if (range != 0 && g != 0) g = (g + delta) & 0xFFFF;
                    if (g != 0) cmap[ch] = (ushort)g;
                }
            }
        }
        else
        {
            var groups = U32(best + 12);
            for (var i = 0u; i < groups; i++)
            {
                var p = best + 16 + (int)i * 12; uint start = U32(p), end = U32(p + 4), glyph = U32(p + 8);
                for (var ch = start; ch <= end && ch < 0x110000; ch++) cmap[(int)ch] = (ushort)(glyph + ch - start);
            }
        }
        if (!cmap.ContainsKey(0xFE8D)) throw new InvalidOperationException("The PDF font has no Arabic presentation forms.");
    }

    int U16(int p) => font[p] << 8 | font[p + 1];
    uint U32(int p) => (uint)(font[p] << 24 | font[p + 1] << 16 | font[p + 2] << 8 | font[p + 3]);

    static readonly Dictionary<char, int[]> Forms = new()
    {
        ['ء'] = [0xFE80, 0, 0, 0], ['آ'] = [0xFE81, 0xFE82, 0, 0], ['أ'] = [0xFE83, 0xFE84, 0, 0], ['ؤ'] = [0xFE85, 0xFE86, 0, 0],
        ['إ'] = [0xFE87, 0xFE88, 0, 0], ['ئ'] = [0xFE89, 0xFE8A, 0xFE8B, 0xFE8C], ['ا'] = [0xFE8D, 0xFE8E, 0, 0], ['ب'] = [0xFE8F, 0xFE90, 0xFE91, 0xFE92],
        ['ة'] = [0xFE93, 0xFE94, 0, 0], ['ت'] = [0xFE95, 0xFE96, 0xFE97, 0xFE98], ['ث'] = [0xFE99, 0xFE9A, 0xFE9B, 0xFE9C], ['ج'] = [0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0],
        ['ح'] = [0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4], ['خ'] = [0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8], ['د'] = [0xFEA9, 0xFEAA, 0, 0], ['ذ'] = [0xFEAB, 0xFEAC, 0, 0],
        ['ر'] = [0xFEAD, 0xFEAE, 0, 0], ['ز'] = [0xFEAF, 0xFEB0, 0, 0], ['س'] = [0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4], ['ش'] = [0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8],
        ['ص'] = [0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC], ['ض'] = [0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0], ['ط'] = [0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4], ['ظ'] = [0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8],
        ['ع'] = [0xFEC9, 0xFECA, 0xFECB, 0xFECC], ['غ'] = [0xFECD, 0xFECE, 0xFECF, 0xFED0], ['ـ'] = [0x0640, 0x0640, 0x0640, 0x0640], ['ف'] = [0xFED1, 0xFED2, 0xFED3, 0xFED4],
        ['ق'] = [0xFED5, 0xFED6, 0xFED7, 0xFED8], ['ك'] = [0xFED9, 0xFEDA, 0xFEDB, 0xFEDC], ['ل'] = [0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0], ['م'] = [0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4],
        ['ن'] = [0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8], ['ه'] = [0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC], ['و'] = [0xFEED, 0xFEEE, 0, 0], ['ى'] = [0xFEEF, 0xFEF0, 0, 0],
        ['ي'] = [0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4]
    };
    static readonly Dictionary<char, int[]> LamAlef = new() { ['آ'] = [0xFEF5, 0xFEF6], ['أ'] = [0xFEF7, 0xFEF8], ['إ'] = [0xFEF9, 0xFEFA], ['ا'] = [0xFEFB, 0xFEFC] };
    static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﻿';
    static bool Harakat(char c) => c is >= 'ً' and <= 'ْ';
    static bool JoinsForward(char c) => Forms.TryGetValue(c, out var f) && f[2] != 0;

    public static string Shape(string text)
    {
        var chars = text.Where(c => !Harakat(c)).ToArray(); var sb = new StringBuilder();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!Forms.TryGetValue(c, out var forms)) { sb.Append(c); continue; }
            var prevJoins = i > 0 && JoinsForward(chars[i - 1]);
            if (c == 'ل' && i + 1 < chars.Length && LamAlef.TryGetValue(chars[i + 1], out var la)) { sb.Append((char)(prevJoins ? la[1] : la[0])); i++; continue; }
            var nextJoins = forms[2] != 0 && i + 1 < chars.Length && Forms.ContainsKey(chars[i + 1]);
            var form = prevJoins && nextJoins && forms[3] != 0 ? forms[3] : prevJoins && forms[1] != 0 ? forms[1] : nextJoins && forms[2] != 0 ? forms[2] : forms[0];
            sb.Append((char)form);
        }
        return sb.ToString();
    }

    public static string Visual(string text)
    {
        var shaped = Shape(text);
        if (!shaped.Any(IsArabic)) return shaped;
        var rtl = shaped.FirstOrDefault(c => char.IsLetter(c)) is var first && IsArabic(first);
        var tokens = new List<(string Text, bool Arabic)>(); var current = new StringBuilder(); bool? arabic = null;
        foreach (var c in shaped)
        {
            var isAr = IsArabic(c) ? true : char.IsLetterOrDigit(c) ? false : (bool?)null;
            if (isAr is null) { if (current.Length > 0) { tokens.Add((current.ToString(), arabic ?? false)); current.Clear(); arabic = null; } tokens.Add((c.ToString(), rtl)); continue; }
            if (arabic is not null && arabic != isAr) { tokens.Add((current.ToString(), arabic.Value)); current.Clear(); }
            current.Append(c); arabic = isAr;
        }
        if (current.Length > 0) tokens.Add((current.ToString(), arabic ?? false));
        var words = tokens.Select(t => t.Arabic ? new string(t.Text.Reverse().ToArray()) : t.Text).ToList();
        if (!rtl)
        {
            var result = new StringBuilder(); var run = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Arabic || (run.Count > 0 && tokens[i].Text.Trim().Length == 0 && i + 1 < tokens.Count && tokens[i + 1].Arabic)) { run.Add(words[i]); continue; }
                if (run.Count > 0) { run.Reverse(); result.Append(string.Concat(run)); run.Clear(); }
                result.Append(words[i]);
            }
            if (run.Count > 0) { run.Reverse(); result.Append(string.Concat(run)); }
            return result.ToString();
        }
        var output = new List<string>(); var ltr = new List<string>();
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (!tokens[i].Arabic && (char.IsLetterOrDigit(tokens[i].Text[0]) || (ltr.Count > 0 && i > 0 && !tokens[i - 1].Arabic && char.IsLetterOrDigit(tokens[i - 1].Text[^1]))))
            { ltr.Insert(0, words[i]); continue; }
            if (ltr.Count > 0) { output.Add(string.Concat(ltr)); ltr.Clear(); }
            output.Add(words[i] switch { "(" => ")", ")" => "(", var w => w });
        }
        if (ltr.Count > 0) output.Add(string.Concat(ltr));
        return string.Concat(output);
    }

    ushort Glyph(int ch) => cmap.TryGetValue(ch, out var g) ? g : cmap.TryGetValue('?', out var q) ? q : (ushort)0;
    public double Width(string text, double size) => Visual(text).Sum(c => advance[Glyph(c)]) * size / unitsPerEm;

    /// <summary>One document being written: pages, text, lines and filled boxes; A4 portrait unless a page size is given
    /// (labels: 63.5 x 38.1 mm).</summary>
    public sealed class Doc(ItamPdf pdf, double pageW = Doc.W, double pageH = Doc.H)
    {
        public const double W = 595.28, H = 841.89, M = 40;
        readonly List<StringBuilder> pages = []; readonly SortedSet<ushort> used = [];
        StringBuilder page = null!;
        public double Y;
        public int Pages => pages.Count;
        public double PageW => pageW; public double PageH => pageH;
        public void NewPage() { page = new StringBuilder(); pages.Add(page); Y = pageH - M; }
        string Hex(string visual) { var sb = new StringBuilder("<"); foreach (var c in visual) { var g = pdf.Glyph(c); used.Add(g); sb.Append(g.ToString("X4", CultureInfo.InvariantCulture)); } return sb.Append('>').ToString(); }
        public void Text(string? text, double x, double y, double size, bool right = false, string color = "0.08 0.21 0.31")
        {
            if (string.IsNullOrEmpty(text)) return;
            var v = Visual(text); var tx = right ? x - v.Sum(c => pdf.advance[pdf.Glyph(c)]) * size / pdf.unitsPerEm : x;
            page.Append(CultureInfo.InvariantCulture, $"BT {color} rg /F1 {size:0.##} Tf {tx:0.##} {y:0.##} Td {Hex(v)} Tj ET\n");
        }
        /// <summary>Text cut with "…" to fit a width.</summary>
        public void Fit(string? text, double x, double y, double size, double width, string color = "0.16 0.24 0.31")
        {
            var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…";
            Text(t, x, y, size, false, color);
        }
        public void Line(double x1, double y1, double x2, double y2, string color = "0.78 0.83 0.88") => page.Append(CultureInfo.InvariantCulture, $"{color} RG 0.5 w {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S\n");
        public void Box(double x, double y, double w, double h, string fill) => page.Append(CultureInfo.InvariantCulture, $"{fill} rg {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f\n");
        public byte[] Build() => pdf.Build(pages, used, pageW, pageH);
    }
    public Doc Start() { var d = new Doc(this); d.NewPage(); return d; }
    public Doc Start(double width, double height) { var d = new Doc(this, width, height); d.NewPage(); return d; }

    byte[] Build(List<StringBuilder> pages, SortedSet<ushort> used, double pageW, double pageH)
    {
        var objects = new List<byte[]>();
        int Add(byte[] content) { objects.Add(content); return objects.Count; }
        static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        static byte[] Stream(string dict, byte[] data) { using var ms = new MemoryStream(); var head = Ascii($"<< {dict} /Length {data.Length} >>\nstream\n"); ms.Write(head); ms.Write(data); ms.Write(Ascii("\nendstream")); return ms.ToArray(); }
        static byte[] Deflate(byte[] data) { using var ms = new MemoryStream(); using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data); return ms.ToArray(); }
        var inv = CultureInfo.InvariantCulture;
        var fontFile = Add(Stream($"/Filter /FlateDecode /Length1 {font.Length}", Deflate(font)));
        var descriptor = Add(Ascii($"<< /Type /FontDescriptor /FontName /{fontName} /Flags 32 /FontBBox [-1000 -400 2000 1100] /ItalicAngle 0 /Ascent 900 /Descent -250 /CapHeight 700 /StemV 80 /FontFile2 {fontFile} 0 R >>"));
        var widths = string.Join(" ", used.Select(g => $"{g} [{(advance[g] * 1000.0 / unitsPerEm).ToString("0", inv)}]"));
        var cid = Add(Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{fontName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /W [{widths}] /CIDToGIDMap /Identity >>"));
        var type0 = Add(Ascii($"<< /Type /Font /Subtype /Type0 /BaseFont /{fontName} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>"));
        var pagesId = objects.Count + 1; Add([]);
        var kids = new List<int>();
        foreach (var p in pages)
        {
            var content = Add(Stream("/Filter /FlateDecode", Deflate(Ascii(p.ToString()))));
            kids.Add(Add(Ascii($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {pageW.ToString("0.##", inv)} {pageH.ToString("0.##", inv)}] /Resources << /Font << /F1 {type0} 0 R >> >> /Contents {content} 0 R >>")));
        }
        objects[pagesId - 1] = Ascii($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => k + " 0 R"))}] /Count {kids.Count} >>");
        var catalog = Add(Ascii($"<< /Type /Catalog /Pages {pagesId} 0 R >>"));
        using var output = new MemoryStream();
        output.Write(Ascii("%PDF-1.7\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); output.Write(Ascii($"{i + 1} 0 obj\n")); output.Write(objects[i]); output.Write(Ascii("\nendobj\n")); }
        var xref = output.Position;
        output.Write(Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var o in offsets) output.Write(Ascii($"{o.ToString("0000000000", inv)} 00000 n \n"));
        output.Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }
}
