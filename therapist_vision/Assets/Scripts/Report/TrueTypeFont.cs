using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TherapistVision.Reports
{
    // Minimal TrueType (.ttf) reader — just enough to embed a font into a PDF and lay out
    // text: Unicode -> glyph lookup (cmap format 4, i.e. the whole Basic Multilingual Plane,
    // which covers Latin and Cyrillic), advance widths and vertical metrics.
    //
    // Deliberately dependency-free (no UnityEngine) so the report code can be compiled and
    // tested outside the Editor, and runs the same on Windows and Linux builds.
    public sealed class TrueTypeFont
    {
        public byte[] Data { get; }
        public string PostScriptName { get; private set; } = "EmbeddedFont";
        public int UnitsPerEm { get; private set; } = 1000;
        public int Ascender { get; private set; }
        public int Descender { get; private set; }
        public int CapHeight { get; private set; }
        public int XMin { get; private set; }
        public int YMin { get; private set; }
        public int XMax { get; private set; }
        public int YMax { get; private set; }
        public float ItalicAngle { get; private set; }

        private ushort[] advanceWidths = new ushort[0];
        private readonly Dictionary<int, ushort> cmap = new Dictionary<int, ushort>();

        public static TrueTypeFont Load(string path)
        {
            return new TrueTypeFont(File.ReadAllBytes(path));
        }

        public TrueTypeFont(byte[] data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            if (data.Length < 12)
                throw new InvalidDataException("Font file is too small to be a TrueType font.");

            uint version = U32(0);
            if (version != 0x00010000 && version != 0x74727565) // 1.0 or 'true'
                throw new InvalidDataException("Only TrueType-outline fonts (.ttf) are supported, not OpenType/CFF (.otf) or collections (.ttc).");

            var tables = new Dictionary<string, int>();
            int numTables = U16(4);
            for (int i = 0; i < numTables; i++)
            {
                int record = 12 + 16 * i;
                string tag = Encoding.ASCII.GetString(data, record, 4);
                tables[tag] = (int)U32(record + 8);
            }

            foreach (string required in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
            {
                if (!tables.ContainsKey(required))
                    throw new InvalidDataException($"Font is missing the required '{required}' table.");
            }

            int head = tables["head"];
            UnitsPerEm = U16(head + 18);
            XMin = S16(head + 36);
            YMin = S16(head + 38);
            XMax = S16(head + 40);
            YMax = S16(head + 42);

            int hhea = tables["hhea"];
            Ascender = S16(hhea + 4);
            Descender = S16(hhea + 6);
            int numberOfHMetrics = U16(hhea + 34);

            int numGlyphs = U16(tables["maxp"] + 4);
            ReadAdvanceWidths(tables["hmtx"], numberOfHMetrics, numGlyphs);

            CapHeight = (int)(Ascender * 0.7f);
            if (tables.TryGetValue("OS/2", out int os2) && U16(os2) >= 2)
            {
                CapHeight = S16(os2 + 88);
            }

            if (tables.TryGetValue("post", out int post))
            {
                ItalicAngle = (int)U32(post + 4) / 65536f;
            }

            if (tables.TryGetValue("name", out int name))
            {
                ReadPostScriptName(name);
            }

            ReadCmap(tables["cmap"]);
            if (cmap.Count == 0)
                throw new InvalidDataException("Font has no Unicode (format 4) character map.");
        }

        public bool HasGlyph(char c) => cmap.ContainsKey(c);

        public ushort GetGlyph(char c)
        {
            return cmap.TryGetValue(c, out ushort gid) ? gid : (ushort)0;
        }

        public int GetAdvance(ushort glyph)
        {
            if (advanceWidths.Length == 0) return UnitsPerEm / 2;
            return advanceWidths[Math.Min(glyph, advanceWidths.Length - 1)];
        }

        // Width of `text` in points when set at `size` points.
        public float MeasureText(string text, float size)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            long units = 0;
            foreach (char c in text)
            {
                units += GetAdvance(GetGlyph(c));
            }
            return units * size / UnitsPerEm;
        }

        private void ReadAdvanceWidths(int hmtx, int numberOfHMetrics, int numGlyphs)
        {
            advanceWidths = new ushort[Math.Max(numGlyphs, numberOfHMetrics)];
            ushort last = 0;
            for (int i = 0; i < advanceWidths.Length; i++)
            {
                if (i < numberOfHMetrics)
                {
                    last = (ushort)U16(hmtx + 4 * i);
                }
                advanceWidths[i] = last;
            }
        }

        private void ReadCmap(int cmapTable)
        {
            int numSubtables = U16(cmapTable + 2);
            int best = -1;
            int bestScore = -1;
            for (int i = 0; i < numSubtables; i++)
            {
                int record = cmapTable + 4 + 8 * i;
                int platform = U16(record);
                int encoding = U16(record + 2);
                int offset = cmapTable + (int)U32(record + 4);
                if (U16(offset) != 4) continue;

                int score = platform == 3 && encoding == 1 ? 2 : platform == 0 ? 1 : -1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = offset;
                }
            }

            if (best < 0) return;

            int segCount = U16(best + 6) / 2;
            int endCodes = best + 14;
            int startCodes = endCodes + 2 * segCount + 2;
            int idDeltas = startCodes + 2 * segCount;
            int idRangeOffsets = idDeltas + 2 * segCount;

            for (int s = 0; s < segCount; s++)
            {
                int end = U16(endCodes + 2 * s);
                int start = U16(startCodes + 2 * s);
                int delta = S16(idDeltas + 2 * s);
                int rangeOffsetPos = idRangeOffsets + 2 * s;
                int rangeOffset = U16(rangeOffsetPos);

                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                    {
                        glyph = (c + delta) & 0xFFFF;
                    }
                    else
                    {
                        int address = rangeOffsetPos + rangeOffset + 2 * (c - start);
                        if (address + 1 >= Data.Length) continue;
                        glyph = U16(address);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }

                    if (glyph != 0) cmap[c] = (ushort)glyph;
                }
            }
        }

        private void ReadPostScriptName(int nameTable)
        {
            int count = U16(nameTable + 2);
            int stringsStart = nameTable + U16(nameTable + 4);
            for (int i = 0; i < count; i++)
            {
                int record = nameTable + 6 + 12 * i;
                int platform = U16(record);
                int nameId = U16(record + 6);
                if (nameId != 6) continue;

                int length = U16(record + 8);
                int offset = stringsStart + U16(record + 10);
                string value = platform == 3 || platform == 0
                    ? Encoding.BigEndianUnicode.GetString(Data, offset, length)
                    : Encoding.ASCII.GetString(Data, offset, length);

                var clean = new StringBuilder();
                foreach (char c in value)
                {
                    if (c > 32 && c < 127 && "[](){}<>/%#".IndexOf(c) < 0) clean.Append(c);
                }
                if (clean.Length > 0)
                {
                    PostScriptName = clean.ToString();
                    return;
                }
            }
        }

        private int U16(int offset) => (Data[offset] << 8) | Data[offset + 1];
        private int S16(int offset) => (short)U16(offset);
        private uint U32(int offset) => ((uint)Data[offset] << 24) | ((uint)Data[offset + 1] << 16) | ((uint)Data[offset + 2] << 8) | Data[offset + 3];
    }
}
