using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Itam.App;

/// <summary>Writes bilingual A4 pages (English left, Arabic right): English in the Latin font (Calibri), Arabic in a font
/// with Arabic presentation forms (Windows Arial); joins Arabic letters and writes right-to-left text in visual order. Each
/// document embeds only the letters it uses (font subset).</summary>
public sealed class ItamPdf
{
    /// <summary>One TrueType font: glyph widths and the Unicode character map.</summary>
    sealed class Face
    {
        public readonly byte[] Data; public readonly string Name; public readonly int UnitsPerEm; public readonly ushort[] Advance;
        readonly Dictionary<int, ushort> cmap = [];
        public Face(byte[] ttf, string name)
        {
            Data = ttf; Name = new string(name.Where(char.IsLetterOrDigit).ToArray());
            var tables = Tables(ttf);
            UnitsPerEm = U16(ttf, tables["head"].Offset + 18);
            int numGlyphs = U16(ttf, tables["maxp"].Offset + 4), metrics = U16(ttf, tables["hhea"].Offset + 34);
            Advance = new ushort[numGlyphs];
            var hmtx = tables["hmtx"].Offset; ushort last = 0;
            for (var g = 0; g < numGlyphs; g++) { if (g < metrics) last = (ushort)U16(ttf, hmtx + g * 4); Advance[g] = last; }
            var cm = tables["cmap"].Offset; int subtables = U16(ttf, cm + 2); int best = -1, bestFormat = 0;
            for (var i = 0; i < subtables; i++)
            {
                int platform = U16(ttf, cm + 4 + i * 8), encoding = U16(ttf, cm + 6 + i * 8); var offset = cm + (int)U32(ttf, cm + 8 + i * 8); int format = U16(ttf, offset);
                if (platform == 3 && (encoding == 10 && format == 12 || encoding == 1 && format == 4 && bestFormat != 12)) { best = offset; bestFormat = format; }
            }
            if (best < 0) throw new InvalidOperationException("The PDF font has no Unicode character map.");
            if (bestFormat == 4)
            {
                int segs = U16(ttf, best + 6) / 2; int ends = best + 14, starts = ends + segs * 2 + 2, deltas = starts + segs * 2, ranges = deltas + segs * 2;
                for (var s = 0; s < segs; s++)
                {
                    int end = U16(ttf, ends + s * 2), start = U16(ttf, starts + s * 2), delta = (short)U16(ttf, deltas + s * 2), range = U16(ttf, ranges + s * 2);
                    for (var ch = start; ch <= end && ch != 0xFFFF; ch++)
                    {
                        int g = range == 0 ? (ch + delta) & 0xFFFF : U16(ttf, ranges + s * 2 + range + (ch - start) * 2);
                        if (range != 0 && g != 0) g = (g + delta) & 0xFFFF;
                        if (g != 0) cmap[ch] = (ushort)g;
                    }
                }
            }
            else
            {
                var groups = U32(ttf, best + 12);
                for (var i = 0u; i < groups; i++)
                {
                    var p = best + 16 + (int)i * 12; uint start = U32(ttf, p), end = U32(ttf, p + 4), glyph = U32(ttf, p + 8);
                    for (var ch = start; ch <= end && ch < 0x110000; ch++) cmap[(int)ch] = (ushort)(glyph + ch - start);
                }
            }
        }
        public bool Has(int ch) => cmap.ContainsKey(ch);
        public ushort Glyph(int ch) => cmap.TryGetValue(ch, out var g) ? g : cmap.TryGetValue('?', out var q) ? q : (ushort)0;

        /// <summary>A copy of the font that keeps only the used glyphs (and the parts of composite glyphs); glyph numbers stay
        /// the same, so the PDF text needs no change.</summary>
        public byte[] Subset(IEnumerable<ushort> used)
        {
            var t = Tables(Data); var head = t["head"].Offset; var longLoca = U16(Data, head + 50) == 1;
            int numGlyphs = U16(Data, t["maxp"].Offset + 4); var loca = t["loca"].Offset; var glyf = t["glyf"].Offset;
            int Off(int g) => longLoca ? (int)U32(Data, loca + g * 4) : U16(Data, loca + g * 2) * 2;
            var keep = new HashSet<int>(); var todo = new Stack<int>(used.Select(g => (int)g).Append(0));
            while (todo.Count > 0)
            {
                var g = todo.Pop(); if (g >= numGlyphs || !keep.Add(g)) continue;
                int start = Off(g), end = Off(g + 1); if (end - start < 10 || (short)U16(Data, glyf + start) >= 0) continue;
                for (var p = glyf + start + 10; ;)
                {
                    int flags = U16(Data, p), component = U16(Data, p + 2); todo.Push(component);
                    p += 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
                    if ((flags & 0x20) == 0) break;
                }
            }
            using var newGlyf = new MemoryStream(); var newLoca = new byte[(numGlyphs + 1) * 4];
            for (var g = 0; g < numGlyphs; g++)
            {
                W32(newLoca, g * 4, (uint)newGlyf.Position);
                if (!keep.Contains(g)) continue;
                int start = Off(g), end = Off(g + 1); newGlyf.Write(Data, glyf + start, end - start);
                while (newGlyf.Position % 4 != 0) newGlyf.WriteByte(0);
            }
            W32(newLoca, numGlyphs * 4, (uint)newGlyf.Position);
            var headCopy = Data.AsSpan(head, t["head"].Length).ToArray(); W32(headCopy, 8, 0); headCopy[50] = 0; headCopy[51] = 1;
            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["head"] = headCopy, ["loca"] = newLoca, ["glyf"] = newGlyf.ToArray() };
            foreach (var name in new[] { "hhea", "hmtx", "maxp", "cvt ", "fpgm", "prep", "OS/2", "cmap" })
                if (t.TryGetValue(name, out var x)) tables[name] = Data.AsSpan(x.Offset, x.Length).ToArray();
            var n = tables.Count; var pow = 1; var log = 0; while (pow * 2 <= n) { pow *= 2; log++; }
            var dir = new byte[12 + n * 16]; W32(dir, 0, 0x00010000); W16(dir, 4, n); W16(dir, 6, pow * 16); W16(dir, 8, log); W16(dir, 10, n * 16 - pow * 16);
            var offset = dir.Length; var i = 0;
            foreach (var (name, bytes) in tables)
            {
                var p = 12 + i++ * 16; Encoding.ASCII.GetBytes(name).CopyTo(dir, p);
                uint sum = 0;
                for (var k = 0; k < bytes.Length; k += 4)
                    sum += (uint)(bytes[k] << 24 | (k + 1 < bytes.Length ? bytes[k + 1] : 0) << 16 | (k + 2 < bytes.Length ? bytes[k + 2] : 0) << 8 | (k + 3 < bytes.Length ? bytes[k + 3] : 0));
                W32(dir, p + 4, sum); W32(dir, p + 8, (uint)offset); W32(dir, p + 12, (uint)bytes.Length); offset += (bytes.Length + 3) & ~3;
            }
            using var o = new MemoryStream();
            o.Write(dir); foreach (var bytes in tables.Values) { o.Write(bytes); for (var k = bytes.Length; k % 4 != 0; k++) o.WriteByte(0); }
            return o.ToArray();
        }

        static Dictionary<string, (int Offset, int Length)> Tables(byte[] ttf)
        {
            var tables = new Dictionary<string, (int Offset, int Length)>(); int count = U16(ttf, 4);
            for (var i = 0; i < count; i++) { var p = 12 + i * 16; tables[Encoding.ASCII.GetString(ttf, p, 4)] = ((int)U32(ttf, p + 8), (int)U32(ttf, p + 12)); }
            return tables;
        }
        static int U16(byte[] b, int p) => b[p] << 8 | b[p + 1];
        static uint U32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
        static void W16(byte[] b, int p, int v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }
        static void W32(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
    }

    readonly Face arabic, latin, bold;
    Face[] Faces => [arabic, latin, bold];

    /// <param name="ttf">A font with Arabic presentation forms (Windows Arial).</param>
    /// <param name="latinTtf">The font for English text (Windows Calibri); Arial when not given.</param>
    /// <param name="latinBoldTtf">Bold English text (Windows Calibri Bold); the regular English font when not given.</param>
    public ItamPdf(byte[] ttf, byte[]? latinTtf = null, byte[]? latinBoldTtf = null)
    {
        arabic = new Face(ttf, "Arial");
        if (!arabic.Has(0xFE8D)) throw new InvalidOperationException("The PDF font has no Arabic presentation forms.");
        latin = latinTtf is null ? arabic : new Face(latinTtf, "Calibri");
        bold = latinBoldTtf is null ? latin : new Face(latinBoldTtf, "CalibriBold");
    }

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

    Face FaceFor(string visual, bool strong = false) => visual.Any(IsArabic) ? arabic : strong ? bold : latin;
    public double Width(string text, double size, bool strong = false) { var v = Visual(text); var f = FaceFor(v, strong); return v.Sum(c => f.Advance[f.Glyph(c)]) * size / f.UnitsPerEm; }

    /// <summary>One document being written: pages, text, lines and filled boxes; A4 portrait unless a page size is given
    /// (labels: 63.5 x 38.1 mm).</summary>
    public sealed class Doc(ItamPdf pdf, double pageW = Doc.W, double pageH = Doc.H)
    {
        public const double W = 595.28, H = 841.89, M = 40;
        readonly List<StringBuilder> pages = []; readonly SortedSet<ushort>[] used = [[], [], []]; readonly List<Picture> pictures = [];
        StringBuilder page = null!;
        public double Y;
        public int Pages => pages.Count;
        public double PageW => pageW; public double PageH => pageH;
        public void NewPage() { page = new StringBuilder(); pages.Add(page); Y = pageH - M; }
        string Hex(string visual, int f) { var face = pdf.Faces[f]; var sb = new StringBuilder("<"); foreach (var c in visual) { var g = face.Glyph(c); used[f].Add(g); sb.Append(g.ToString("X4", CultureInfo.InvariantCulture)); } return sb.Append('>').ToString(); }
        public void Text(string? text, double x, double y, double size, bool right = false, string color = "0 0 0", bool strong = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            var v = Visual(text); var face = pdf.FaceFor(v, strong); var f = Array.IndexOf(pdf.Faces, face); var tx = right ? x - v.Sum(c => face.Advance[face.Glyph(c)]) * size / face.UnitsPerEm : x;
            page.Append(CultureInfo.InvariantCulture, $"BT {color} rg /F{f + 1} {size:0.##} Tf {tx:0.##} {y:0.##} Td {Hex(v, f)} Tj ET\n");
        }
        /// <summary>Text cut with "…" to fit a width.</summary>
        public void Fit(string? text, double x, double y, double size, double width, string color = "0.1 0.1 0.1")
        {
            var t = text ?? ""; while (t.Length > 1 && pdf.Width(t, size) > width) t = t[..^2] + "…";
            Text(t, x, y, size, false, color);
        }
        public void Line(double x1, double y1, double x2, double y2, string color = "0.7 0.7 0.7") => page.Append(CultureInfo.InvariantCulture, $"{color} RG 0.5 w {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S\n");
        public void Box(double x, double y, double w, double h, string fill) => page.Append(CultureInfo.InvariantCulture, $"{fill} rg {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f\n");
        /// <summary>Draws a picture with its bottom-left corner at (x, y) and the given width (height follows).</summary>
        public double Image(Picture p, double x, double y, double width)
        {
            var i = pictures.IndexOf(p); if (i < 0) { pictures.Add(p); i = pictures.Count - 1; }
            var height = width * p.Height / p.Width;
            page.Append(CultureInfo.InvariantCulture, $"q {width:0.##} 0 0 {height:0.##} {x:0.##} {y:0.##} cm /Im{i} Do Q\n");
            return height;
        }
        /// <summary>The company logo with its top at <paramref name="top"/>; returns its height.</summary>
        public double Logo(double x, double top, double width) { var h = width * Logo_.Height / Logo_.Width; Image(Logo_, x, top - h, width); return h; }
        static Picture Logo_ => ItamPdf.Logo;
        public byte[] Build() => pdf.Build(pages, used, pictures, pageW, pageH);
    }
    public Doc Start() { var d = new Doc(this); d.NewPage(); return d; }
    public Doc Start(double width, double height) { var d = new Doc(this, width, height); d.NewPage(); return d; }


    // Images and the company logo ------------------------------------------------------------------------------------------
    /// <summary>A grayscale picture flattened on white paper (prints the same on black-and-white printers).</summary>
    public sealed record Picture(int Width, int Height, byte[] Gray);

    /// <summary>The company logo (Assets/company-logo.png, black on transparent).</summary>
    public static Picture Logo { get; } = ReadPng(LogoBytes());
    static byte[] LogoBytes()
    {
        var asm = typeof(ItamPdf).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("company-logo.png", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!; using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    /// <summary>Reads an 8-bit, non-interlaced PNG (gray, RGB, gray + alpha or RGBA) into gray pixels on white.</summary>
    public static Picture ReadPng(byte[] png)
    {
        static int Be(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];
        static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
        int w = 0, h = 0, colorType = 0; using var idat = new MemoryStream();
        for (var i = 8; i + 8 <= png.Length;)
        {
            var len = Be(png, i); var type = Encoding.ASCII.GetString(png, i + 4, 4);
            if (type == "IHDR")
            {
                w = Be(png, i + 8); h = Be(png, i + 12); colorType = png[i + 17];
                if (png[i + 16] != 8 || png[i + 20] != 0) throw new NotSupportedException("Only 8-bit, non-interlaced PNG images are supported.");
            }
            else if (type == "IDAT") idat.Write(png, i + 8, len);
            else if (type == "IEND") break;
            i += 12 + len;
        }
        var bpp = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException("Unsupported PNG colour type.") };
        idat.Position = 0; using var z = new ZLibStream(idat, CompressionMode.Decompress); using var raw = new MemoryStream(); z.CopyTo(raw);
        var data = raw.ToArray(); var stride = w * bpp; var prev = new byte[stride]; var cur = new byte[stride]; var gray = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var f = data[y * (stride + 1)]; Array.Copy(data, y * (stride + 1) + 1, cur, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                cur[x] = (byte)(cur[x] + f switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0
                });
            }
            for (var x = 0; x < w; x++)
            {
                var o = x * bpp;
                double lum = bpp >= 3 ? 0.299 * cur[o] + 0.587 * cur[o + 1] + 0.114 * cur[o + 2] : cur[o];
                double alpha = bpp == 4 ? cur[o + 3] / 255.0 : bpp == 2 ? cur[o + 1] / 255.0 : 1;
                gray[y * w + x] = (byte)Math.Round(255 - alpha * (255 - lum));
            }
            (prev, cur) = (cur, prev);
        }
        return new(w, h, gray);
    }

    byte[] Build(List<StringBuilder> pages, SortedSet<ushort>[] used, List<Picture> pictures, double pageW, double pageH)
    {
        var objects = new List<byte[]>();
        int Add(byte[] content) { objects.Add(content); return objects.Count; }
        static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        static byte[] Stream(string dict, byte[] data) { using var ms = new MemoryStream(); var head = Ascii($"<< {dict} /Length {data.Length} >>\nstream\n"); ms.Write(head); ms.Write(data); ms.Write(Ascii("\nendstream")); return ms.ToArray(); }
        static byte[] Deflate(byte[] data) { using var ms = new MemoryStream(); using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data); return ms.ToArray(); }
        var inv = CultureInfo.InvariantCulture;
        // Each font is embedded as a subset of the glyphs this document uses (F1 Arabic, F2 English, F3 English bold).
        int Font(Face face, SortedSet<ushort> used, string tag)
        {
            var name = $"{tag}+{face.Name}"; var data = face.Subset(used);
            var fontFile = Add(Stream($"/Filter /FlateDecode /Length1 {data.Length}", Deflate(data)));
            var descriptor = Add(Ascii($"<< /Type /FontDescriptor /FontName /{name} /Flags 32 /FontBBox [-1000 -400 2000 1100] /ItalicAngle 0 /Ascent 900 /Descent -250 /CapHeight 700 /StemV 80 /FontFile2 {fontFile} 0 R >>"));
            var widths = string.Join(" ", used.Select(g => $"{g} [{(face.Advance[g] * 1000.0 / face.UnitsPerEm).ToString("0", inv)}]"));
            var cid = Add(Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /W [{widths}] /CIDToGIDMap /Identity >>"));
            return Add(Ascii($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>"));
        }
        var fonts = new List<string>();
        string[] tags = ["ITAMAR", "ITAMEN", "ITAMBD"];
        for (var i = 0; i < used.Length; i++) if (used[i].Count > 0) fonts.Add($"/F{i + 1} {Font(Faces[i], used[i], tags[i])} 0 R");
        var fontDict = fonts.Count == 0 ? "" : $"/Font << {string.Join(" ", fonts)} >>";
        var images = pictures.Select(p => Add(Stream($"/Type /XObject /Subtype /Image /Width {p.Width} /Height {p.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Deflate(p.Gray)))).ToList();
        var xobjects = images.Count == 0 ? "" : $" /XObject << {string.Join(" ", images.Select((id, i) => $"/Im{i} {id} 0 R"))} >>";
        var pagesId = objects.Count + 1; Add([]);
        var kids = new List<int>();
        foreach (var p in pages)
        {
            var content = Add(Stream("/Filter /FlateDecode", Deflate(Ascii(p.ToString()))));
            kids.Add(Add(Ascii($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {pageW.ToString("0.##", inv)} {pageH.ToString("0.##", inv)}] /Resources << {fontDict}{xobjects} >> /Contents {content} 0 R >>")));
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
