using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TherapistVision.Reports
{
    public struct PdfColor
    {
        public readonly float R, G, B;

        public PdfColor(float r, float g, float b)
        {
            R = r; G = g; B = b;
        }

        // "#2a78d6" or "2a78d6"
        public static PdfColor Hex(string hex)
        {
            hex = hex.TrimStart('#');
            int v = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new PdfColor(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }

        // Blends toward white: amount 0 = unchanged, 1 = white. Used instead of transparency.
        public PdfColor Tint(float amount)
        {
            return new PdfColor(R + (1 - R) * amount, G + (1 - G) * amount, B + (1 - B) * amount);
        }

        // Relative luminance, for choosing black/white text on a colored swatch.
        public float Luminance => 0.2126f * R + 0.7152f * G + 0.0722f * B;
    }

    public enum TextAlign { Left, Center, Right }

    // One page. All coordinates are in points with the origin at the TOP-LEFT corner and y
    // growing downwards (the PDF's own bottom-left origin is converted internally), which
    // keeps the report layout code readable.
    public sealed class PdfCanvas
    {
        private readonly PdfDocument document;
        internal readonly StringBuilder Content = new StringBuilder();

        public float Width { get; }
        public float Height { get; }

        internal PdfCanvas(PdfDocument document, float width, float height)
        {
            this.document = document;
            Width = width;
            Height = height;
        }

        public TrueTypeFont Font => document.Font;

        public float MeasureText(string text, float size) => document.Font.MeasureText(text, size);

        public void FillRect(float x, float y, float w, float h, PdfColor color)
        {
            if (w <= 0 || h <= 0) return;
            Content.Append("q ").Append(Fill(color)).Append(' ')
                   .Append(N(x)).Append(' ').Append(N(Height - y - h)).Append(' ')
                   .Append(N(w)).Append(' ').Append(N(h)).Append(" re f Q\n");
        }

        public void StrokeRect(float x, float y, float w, float h, PdfColor color, float lineWidth)
        {
            Content.Append("q ").Append(Stroke(color)).Append(' ').Append(N(lineWidth)).Append(" w ")
                   .Append(N(x)).Append(' ').Append(N(Height - y - h)).Append(' ')
                   .Append(N(w)).Append(' ').Append(N(h)).Append(" re S Q\n");
        }

        // Rectangle with all four corners rounded by `radius`.
        public void FillRoundedRect(float x, float y, float w, float h, float radius, PdfColor color)
        {
            if (w <= 0 || h <= 0) return;
            radius = Math.Min(radius, Math.Min(w, h) / 2f);
            Content.Append("q ").Append(Fill(color)).Append('\n');
            AppendRoundedRectPath(x, y, w, h, radius, radius);
            Content.Append("f Q\n");
        }

        // Horizontal bar anchored at its left edge: only the right (data) end is rounded.
        public void FillBarRightRounded(float x, float y, float w, float h, float radius, PdfColor color)
        {
            if (w <= 0 || h <= 0) return;
            radius = Math.Min(radius, Math.Min(w, h) / 2f);
            Content.Append("q ").Append(Fill(color)).Append('\n');
            AppendRoundedRectPath(x, y, w, h, 0, radius);
            Content.Append("f Q\n");
        }

        // Vertical bar anchored at its bottom: only the top (data) end is rounded.
        public void FillBarTopRounded(float x, float y, float w, float h, float radius, PdfColor color)
        {
            if (w <= 0 || h <= 0) return;
            radius = Math.Min(radius, Math.Min(w / 2f, h));
            const float k = 0.5523f;
            float top = Height - y;
            float bottom = Height - y - h;
            Content.Append("q ").Append(Fill(color)).Append('\n')
                   .Append(N(x)).Append(' ').Append(N(bottom)).Append(" m ")
                   .Append(N(x + w)).Append(' ').Append(N(bottom)).Append(" l ")
                   .Append(N(x + w)).Append(' ').Append(N(top - radius)).Append(" l ")
                   .Append(N(x + w)).Append(' ').Append(N(top - radius + radius * k)).Append(' ')
                   .Append(N(x + w - radius + radius * k)).Append(' ').Append(N(top)).Append(' ')
                   .Append(N(x + w - radius)).Append(' ').Append(N(top)).Append(" c ")
                   .Append(N(x + radius)).Append(' ').Append(N(top)).Append(" l ")
                   .Append(N(x + radius - radius * k)).Append(' ').Append(N(top)).Append(' ')
                   .Append(N(x)).Append(' ').Append(N(top - radius + radius * k)).Append(' ')
                   .Append(N(x)).Append(' ').Append(N(top - radius)).Append(" c ")
                   .Append("h f Q\n");
        }

        public void Line(float x1, float y1, float x2, float y2, PdfColor color, float lineWidth, bool dashed = false)
        {
            Content.Append("q ").Append(Stroke(color)).Append(' ').Append(N(lineWidth)).Append(" w 1 J ");
            if (dashed) Content.Append("[2 2] 0 d ");
            Content.Append(N(x1)).Append(' ').Append(N(Height - y1)).Append(" m ")
                   .Append(N(x2)).Append(' ').Append(N(Height - y2)).Append(" l S Q\n");
        }

        public void Polyline(IList<float> xs, IList<float> ys, PdfColor color, float lineWidth)
        {
            if (xs.Count < 2) return;
            Content.Append("q ").Append(Stroke(color)).Append(' ').Append(N(lineWidth)).Append(" w 1 J 1 j ");
            for (int i = 0; i < xs.Count; i++)
            {
                Content.Append(N(xs[i])).Append(' ').Append(N(Height - ys[i])).Append(i == 0 ? " m " : " l ");
            }
            Content.Append("S Q\n");
        }

        public void FillCircle(float cx, float cy, float r, PdfColor color)
        {
            Content.Append("q ").Append(Fill(color)).Append('\n');
            AppendCirclePath(cx, cy, r);
            Content.Append("f Q\n");
        }

        // Filled circle with a contrasting ring (keeps overlapping markers distinguishable).
        public void FillCircleWithRing(float cx, float cy, float r, PdfColor color, PdfColor ring, float ringWidth)
        {
            Content.Append("q ").Append(Fill(color)).Append(' ').Append(Stroke(ring)).Append(' ')
                   .Append(N(ringWidth)).Append(" w\n");
            AppendCirclePath(cx, cy, r);
            Content.Append("B Q\n");
        }

        // `y` is the text BASELINE.
        public void Text(string text, float x, float y, float size, PdfColor color, bool bold = false, TextAlign align = TextAlign.Left)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (align != TextAlign.Left)
            {
                float width = MeasureText(text, size);
                x -= align == TextAlign.Center ? width / 2f : width;
            }

            Content.Append("q BT ").Append(Fill(color)).Append(' ');
            if (bold)
            {
                // Synthetic bold: fill + thin stroke of the same color (the bundled font has
                // no bold face).
                Content.Append(Stroke(color)).Append(" 2 Tr ").Append(N(size * 0.035f)).Append(" w ");
            }
            Content.Append("/F1 ").Append(N(size)).Append(" Tf ")
                   .Append(N(x)).Append(' ').Append(N(Height - y)).Append(" Td <");

            foreach (char c in text)
            {
                ushort glyph = document.UseGlyph(c);
                Content.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
            }

            Content.Append("> Tj ET Q\n");
        }

        // Shortens `text` with an ellipsis so it fits into `maxWidth`.
        public string Fit(string text, float size, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || MeasureText(text, size) <= maxWidth) return text;
            const string ellipsis = "…";
            for (int len = text.Length - 1; len > 0; len--)
            {
                string candidate = text.Substring(0, len).TrimEnd() + ellipsis;
                if (MeasureText(candidate, size) <= maxWidth) return candidate;
            }
            return ellipsis;
        }

        // Greedy word wrap.
        public List<string> Wrap(string text, float size, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            foreach (string paragraph in text.Split('\n'))
            {
                var line = new StringBuilder();
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && MeasureText(candidate, size) > maxWidth)
                    {
                        lines.Add(line.ToString());
                        line.Clear().Append(word);
                    }
                    else
                    {
                        line.Clear().Append(candidate);
                    }
                }
                lines.Add(line.ToString());
            }
            return lines;
        }

        private void AppendRoundedRectPath(float x, float y, float w, float h, float leftRadius, float rightRadius)
        {
            const float k = 0.5523f; // cubic Bézier circle approximation
            float bottom = Height - y - h;
            float top = Height - y;
            float l = leftRadius, r = rightRadius;

            Content.Append(N(x + l)).Append(' ').Append(N(bottom)).Append(" m ");
            Content.Append(N(x + w - r)).Append(' ').Append(N(bottom)).Append(" l ");
            if (r > 0)
                Content.Append(N(x + w - r + r * k)).Append(' ').Append(N(bottom)).Append(' ')
                       .Append(N(x + w)).Append(' ').Append(N(bottom + r - r * k)).Append(' ')
                       .Append(N(x + w)).Append(' ').Append(N(bottom + r)).Append(" c ");
            Content.Append(N(x + w)).Append(' ').Append(N(top - r)).Append(" l ");
            if (r > 0)
                Content.Append(N(x + w)).Append(' ').Append(N(top - r + r * k)).Append(' ')
                       .Append(N(x + w - r + r * k)).Append(' ').Append(N(top)).Append(' ')
                       .Append(N(x + w - r)).Append(' ').Append(N(top)).Append(" c ");
            Content.Append(N(x + l)).Append(' ').Append(N(top)).Append(" l ");
            if (l > 0)
                Content.Append(N(x + l - l * k)).Append(' ').Append(N(top)).Append(' ')
                       .Append(N(x)).Append(' ').Append(N(top - l + l * k)).Append(' ')
                       .Append(N(x)).Append(' ').Append(N(top - l)).Append(" c ");
            Content.Append(N(x)).Append(' ').Append(N(bottom + l)).Append(" l ");
            if (l > 0)
                Content.Append(N(x)).Append(' ').Append(N(bottom + l - l * k)).Append(' ')
                       .Append(N(x + l - l * k)).Append(' ').Append(N(bottom)).Append(' ')
                       .Append(N(x + l)).Append(' ').Append(N(bottom)).Append(" c ");
            Content.Append("h\n");
        }

        private void AppendCirclePath(float cx, float cy, float r)
        {
            const float k = 0.5523f;
            float y = Height - cy;
            Content.Append(N(cx + r)).Append(' ').Append(N(y)).Append(" m ")
                   .Append(N(cx + r)).Append(' ').Append(N(y + r * k)).Append(' ').Append(N(cx + r * k)).Append(' ').Append(N(y + r)).Append(' ').Append(N(cx)).Append(' ').Append(N(y + r)).Append(" c ")
                   .Append(N(cx - r * k)).Append(' ').Append(N(y + r)).Append(' ').Append(N(cx - r)).Append(' ').Append(N(y + r * k)).Append(' ').Append(N(cx - r)).Append(' ').Append(N(y)).Append(" c ")
                   .Append(N(cx - r)).Append(' ').Append(N(y - r * k)).Append(' ').Append(N(cx - r * k)).Append(' ').Append(N(y - r)).Append(' ').Append(N(cx)).Append(' ').Append(N(y - r)).Append(" c ")
                   .Append(N(cx + r * k)).Append(' ').Append(N(y - r)).Append(' ').Append(N(cx + r)).Append(' ').Append(N(y - r * k)).Append(' ').Append(N(cx + r)).Append(' ').Append(N(y)).Append(" c h\n");
        }

        private static string Fill(PdfColor c) => $"{N(c.R)} {N(c.G)} {N(c.B)} rg";
        private static string Stroke(PdfColor c) => $"{N(c.R)} {N(c.G)} {N(c.B)} RG";

        internal static string N(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    // Writes a PDF 1.4 file with vector graphics and one embedded TrueType font
    // (Type0 / Identity-H, so any glyph in the font — Cyrillic included — can be used, and a
    // ToUnicode map keeps the text searchable and copyable in PDF viewers).
    public sealed class PdfDocument
    {
        public const float A4Width = 595.28f;
        public const float A4Height = 841.89f;

        public TrueTypeFont Font { get; }
        public string Title { get; set; } = "";
        public string Subject { get; set; } = "";

        private readonly List<PdfCanvas> pages = new List<PdfCanvas>();
        private readonly SortedDictionary<ushort, char> usedGlyphs = new SortedDictionary<ushort, char>();

        public PdfDocument(TrueTypeFont font)
        {
            Font = font ?? throw new ArgumentNullException(nameof(font));
        }

        public IReadOnlyList<PdfCanvas> Pages => pages;

        public PdfCanvas AddPage(float width = A4Width, float height = A4Height)
        {
            var page = new PdfCanvas(this, width, height);
            pages.Add(page);
            return page;
        }

        internal ushort UseGlyph(char c)
        {
            ushort glyph = Font.GetGlyph(c);
            if (!usedGlyphs.ContainsKey(glyph)) usedGlyphs[glyph] = c;
            return glyph;
        }

        public byte[] Save()
        {
            if (pages.Count == 0) AddPage();

            var output = new MemoryStream();
            var offsets = new List<long>();

            void Write(string s)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(s);
                output.Write(bytes, 0, bytes.Length);
            }

            int BeginObject()
            {
                offsets.Add(output.Position);
                int id = offsets.Count;
                Write($"{id} 0 obj\n");
                return id;
            }

            void WriteStreamObject(string dictionaryExtras, byte[] raw)
            {
                byte[] compressed = ZlibCompress(raw);
                BeginObject();
                Write($"<< /Length {compressed.Length} /Filter /FlateDecode{dictionaryExtras} >>\nstream\n");
                output.Write(compressed, 0, compressed.Length);
                Write("\nendstream\nendobj\n");
            }

            Write("%PDF-1.4\n");
            output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);

            // Fixed object numbers: 1 catalog, 2 pages, 3 font, 4 CID font, 5 descriptor,
            // 6 font file, 7 ToUnicode, 8 info, then (page, content) pairs.
            int firstPageObject = 9;
            string fontName = Font.PostScriptName;
            float scale = 1000f / Font.UnitsPerEm;

            BeginObject();
            Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            BeginObject();
            var kids = new StringBuilder();
            for (int i = 0; i < pages.Count; i++) kids.Append(firstPageObject + 2 * i).Append(" 0 R ");
            Write($"<< /Type /Pages /Kids [ {kids}] /Count {pages.Count} >>\nendobj\n");

            BeginObject();
            Write($"<< /Type /Font /Subtype /Type0 /BaseFont /{fontName} /Encoding /Identity-H /DescendantFonts [4 0 R] /ToUnicode 7 0 R >>\nendobj\n");

            BeginObject();
            var widths = new StringBuilder();
            foreach (ushort glyph in usedGlyphs.Keys)
            {
                widths.Append(glyph).Append(" [").Append(Math.Round(Font.GetAdvance(glyph) * scale)).Append("] ");
            }
            Write($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{fontName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                  $"/FontDescriptor 5 0 R /DW {Math.Round(Font.GetAdvance(0) * scale)} /W [ {widths}] /CIDToGIDMap /Identity >>\nendobj\n");

            BeginObject();
            Write($"<< /Type /FontDescriptor /FontName /{fontName} /Flags 32 " +
                  $"/FontBBox [{Math.Round(Font.XMin * scale)} {Math.Round(Font.YMin * scale)} {Math.Round(Font.XMax * scale)} {Math.Round(Font.YMax * scale)}] " +
                  $"/ItalicAngle {PdfCanvas.N(Font.ItalicAngle)} /Ascent {Math.Round(Font.Ascender * scale)} /Descent {Math.Round(Font.Descender * scale)} " +
                  $"/CapHeight {Math.Round(Font.CapHeight * scale)} /StemV 80 /FontFile2 6 0 R >>\nendobj\n");

            WriteStreamObject($" /Length1 {Font.Data.Length}", Font.Data);
            WriteStreamObject("", Encoding.ASCII.GetBytes(BuildToUnicodeCMap()));

            BeginObject();
            Write($"<< /Title {PdfTextString(Title)} /Subject {PdfTextString(Subject)} /Producer (TherapistVision) " +
                  $"/CreationDate (D:{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>\nendobj\n");

            for (int i = 0; i < pages.Count; i++)
            {
                PdfCanvas page = pages[i];
                int contentObject = firstPageObject + 2 * i + 1;
                BeginObject();
                Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PdfCanvas.N(page.Width)} {PdfCanvas.N(page.Height)}] " +
                      $"/Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>\nendobj\n");
                WriteStreamObject("", Encoding.ASCII.GetBytes(page.Content.ToString()));
            }

            long xrefOffset = output.Position;
            var xref = new StringBuilder();
            xref.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (long offset in offsets)
            {
                xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }
            Write(xref.ToString());
            Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 8 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

            return output.ToArray();
        }

        private string BuildToUnicodeCMap()
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
              .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
              .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
              .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

            var entries = new List<KeyValuePair<ushort, char>>(usedGlyphs);
            for (int start = 0; start < entries.Count; start += 100)
            {
                int count = Math.Min(100, entries.Count - start);
                sb.Append(count).Append(" beginbfchar\n");
                for (int i = start; i < start + count; i++)
                {
                    sb.Append('<').Append(entries[i].Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                      .Append(((int)entries[i].Value).ToString("X4", CultureInfo.InvariantCulture)).Append(">\n");
                }
                sb.Append("endbfchar\n");
            }

            sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return sb.ToString();
        }

        // Non-ASCII metadata must be written as UTF-16BE with a byte-order mark.
        private static string PdfTextString(string text)
        {
            var sb = new StringBuilder("<FEFF");
            foreach (char c in text ?? "")
            {
                sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.Append('>').ToString();
        }

        // PDF's FlateDecode expects a zlib stream (header + deflate + Adler-32), while
        // DeflateStream produces raw deflate — so wrap it by hand.
        private static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                {
                    deflate.Write(data, 0, data.Length);
                }

                uint a = 1, b = 0;
                foreach (byte d in data)
                {
                    a = (a + d) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }
    }
}
