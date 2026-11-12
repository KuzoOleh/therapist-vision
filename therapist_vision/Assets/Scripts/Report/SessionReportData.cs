using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TherapistVision.Reports
{
    public sealed class InstrumentHit
    {
        public int Index;          // 1-based order of the hit within the session
        public string Instrument;
        public float Force;        // normalized 0..1 for drums/maracas (piano keys may report m/s)
        public float? Time;        // seconds since session start; null in legacy files
    }

    public sealed class ColorChoice
    {
        public int Position;       // 1 = chosen first (most preferred)
        public string Color;
        public float? Time;
    }

    public sealed class SequencerPress
    {
        public int Index;
        public string Button;
        public string Group;
        public string Mood;
        public string Action = "start"; // start / stop / resume
        public float? Time;

        public bool IsStart => Action == "start";
    }

    // Record button on the sequencer panel: armed → started → stopped, or armed → cancelled.
    public sealed class RecordingEvent
    {
        public string Event;
        public string FileName;
        public float? Time;
    }

    // Everything the report needs from one session CSV, independent of which CSV layout the
    // VR app used to write it.
    public sealed class SessionReportData
    {
        public readonly Dictionary<string, string> Meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<InstrumentHit> InstrumentHits = new List<InstrumentHit>();
        public readonly List<ColorChoice> LusherFirst = new List<ColorChoice>();
        public readonly List<ColorChoice> LusherSecond = new List<ColorChoice>();
        public readonly List<SequencerPress> SequencerPresses = new List<SequencerPress>();
        public readonly List<RecordingEvent> RecordingEvents = new List<RecordingEvent>();

        // Loop launches only (the stop / resume presses are kept in SequencerPresses too).
        public List<SequencerPress> SequencerStarts => SequencerPresses.Where(p => p.IsStart).ToList();

        public string SourceFileName = "";

        // Older VR builds wrote a human-readable, multi-section text file with no per-event
        // timestamps. Those are still accepted; the report then plots by event order instead
        // of by time and says so.
        public bool IsLegacyFormat;

        public string Get(string key)
        {
            return Meta.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }

        public bool HasEventTimes =>
            !IsLegacyFormat &&
            (InstrumentHits.Any(h => h.Time.HasValue) || SequencerPresses.Any(p => p.Time.HasValue) ||
             LusherFirst.Any(c => c.Time.HasValue) || LusherSecond.Any(c => c.Time.HasValue));

        public float DurationSeconds
        {
            get
            {
                if (float.TryParse(Get("duration_sec"), NumberStyles.Float, CultureInfo.InvariantCulture, out float d) && d > 0)
                    return d;

                float max = 0f;
                foreach (var h in InstrumentHits) if (h.Time > max) max = h.Time.Value;
                foreach (var p in SequencerPresses) if (p.Time > max) max = p.Time.Value;
                foreach (var r in RecordingEvents) if (r.Time > max) max = r.Time.Value;
                foreach (var c in LusherFirst) if (c.Time > max) max = c.Time.Value;
                foreach (var c in LusherSecond) if (c.Time > max) max = c.Time.Value;
                return max;
            }
        }
    }

    public static class SessionCsvParser
    {
        public static SessionReportData ParseFile(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8); // strips the UTF-8 BOM if present
            return Parse(text, Path.GetFileName(path));
        }

        public static SessionReportData Parse(string text, string sourceFileName = "")
        {
            text = (text ?? "").TrimStart('﻿');
            string firstLine = text.Split('\n')[0].Trim();

            var data = firstLine.StartsWith("section,", StringComparison.OrdinalIgnoreCase)
                ? ParseStructured(text)
                : ParseLegacy(text);

            data.SourceFileName = sourceFileName ?? "";
            return data;
        }

        // ------------------------------------------------------------ current format
        // section,index,item,detail,value,t_sec  (RFC 4180, invariant-culture numbers)

        private static SessionReportData ParseStructured(string text)
        {
            var data = new SessionReportData();
            List<List<string>> rows = ReadCsvRows(text);
            if (rows.Count == 0) return data;

            var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            int Col(string name) => header.IndexOf(name);
            int cSection = Col("section"), cIndex = Col("index"), cItem = Col("item"),
                cDetail = Col("detail"), cValue = Col("value"), cTime = Col("t_sec");

            string Cell(List<string> row, int col) => col >= 0 && col < row.Count ? UnguardText(row[col].Trim()) : "";

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                string section = Cell(row, cSection).ToLowerInvariant();
                if (section.Length == 0) continue;

                int index = ParseInt(Cell(row, cIndex)) ?? 0;
                string item = Cell(row, cItem);
                string detail = Cell(row, cDetail);
                string value = Cell(row, cValue);
                float? time = ParseFloat(Cell(row, cTime));

                switch (section)
                {
                    case "meta":
                        data.Meta[item] = value;
                        break;
                    case "instrument_hit":
                        data.InstrumentHits.Add(new InstrumentHit
                        {
                            Index = index > 0 ? index : data.InstrumentHits.Count + 1,
                            Instrument = item,
                            Force = ParseFloat(value) ?? 0f,
                            Time = time
                        });
                        break;
                    case "lusher_1":
                        data.LusherFirst.Add(new ColorChoice { Position = index > 0 ? index : data.LusherFirst.Count + 1, Color = item, Time = time });
                        break;
                    case "lusher_2":
                        data.LusherSecond.Add(new ColorChoice { Position = index > 0 ? index : data.LusherSecond.Count + 1, Color = item, Time = time });
                        break;
                    case "sequencer":
                    case "sequencer_stop":
                    case "sequencer_resume":
                        data.SequencerPresses.Add(new SequencerPress
                        {
                            Index = index > 0 ? index : data.SequencerPresses.Count + 1,
                            Button = item,
                            Group = detail,
                            Mood = value,
                            Action = section == "sequencer" ? "start" : section.Substring("sequencer_".Length),
                            Time = time
                        });
                        break;
                    case "sequencer_record":
                        data.RecordingEvents.Add(new RecordingEvent { Event = item.ToLowerInvariant(), FileName = value, Time = time });
                        break;
                    // instrument_summary is derived data — the report recomputes it from the hits.
                }
            }

            return data;
        }

        // The VR app prefixes text starting with = + - @ with an apostrophe so spreadsheets
        // don't run it as a formula; undo that for display.
        private static string UnguardText(string text)
        {
            if (text.Length >= 2 && text[0] == '\'' && "=+-@".IndexOf(text[1]) >= 0)
                return text.Substring(1);
            return text;
        }

        private static List<List<string>> ReadCsvRows(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { /* handled by \n */ }
                else if (c == '\n')
                {
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                }
                else cell.Append(c);
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // ------------------------------------------------------------- legacy format
        // Пацієнт: X / Спеціаліст: Y / Загальний проведений час: 26,32 sec
        // Інструмент: Drum / "1, 0,53" ... / Перше тестування Люшера: / "1, Red" ...
        // Values are separated by ", " (comma + space) and decimals may use a comma.

        private static SessionReportData ParseLegacy(string text)
        {
            var data = new SessionReportData { IsLegacyFormat = true };
            string section = "";
            string currentInstrument = null;
            TimeSpan? firstSequencerClock = null;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (TryValueAfter(line, "Пацієнт:", out string v)) { data.Meta["patient"] = v; continue; }
                if (TryValueAfter(line, "Спеціаліст:", out v)) { data.Meta["therapist"] = v; continue; }
                if (TryValueAfter(line, "Загальний проведений час:", out v))
                {
                    float? seconds = ParseFloat(v.Replace("sec", "").Trim());
                    if (seconds.HasValue) data.Meta["duration_sec"] = seconds.Value.ToString(CultureInfo.InvariantCulture);
                    continue;
                }
                if (TryValueAfter(line, "Інструмент:", out v)) { section = "instrument"; currentInstrument = v; continue; }
                if (line.StartsWith("Перше тестування Люшера", StringComparison.Ordinal) || line.StartsWith("Button Press Order", StringComparison.Ordinal)) { section = "lusher_1"; continue; }
                if (line.StartsWith("Друге тестування Люшера", StringComparison.Ordinal)) { section = "lusher_2"; continue; }
                if (line.StartsWith("Секвенсор", StringComparison.Ordinal)) { section = "sequencer"; continue; }
                if (line.StartsWith("Кількість ударів", StringComparison.Ordinal) || line.StartsWith("Група,", StringComparison.Ordinal)) continue;

                string[] parts = line.Split(new[] { ", " }, StringSplitOptions.None);

                switch (section)
                {
                    case "instrument" when parts.Length >= 2 && currentInstrument != null:
                        data.InstrumentHits.Add(new InstrumentHit
                        {
                            Index = data.InstrumentHits.Count + 1,
                            Instrument = currentInstrument,
                            Force = ParseFloat(parts[1]) ?? 0f
                        });
                        break;
                    case "lusher_1" when parts.Length >= 2:
                        data.LusherFirst.Add(new ColorChoice { Position = ParseInt(parts[0]) ?? data.LusherFirst.Count + 1, Color = parts[1].Trim() });
                        break;
                    case "lusher_2" when parts.Length >= 2:
                        data.LusherSecond.Add(new ColorChoice { Position = ParseInt(parts[0]) ?? data.LusherSecond.Count + 1, Color = parts[1].Trim() });
                        break;
                    case "sequencer" when parts.Length >= 3:
                        float? relative = null;
                        if (parts.Length >= 4 && TimeSpan.TryParse(parts[3].Trim(), CultureInfo.InvariantCulture, out TimeSpan clock))
                        {
                            if (!firstSequencerClock.HasValue) firstSequencerClock = clock;
                            relative = (float)(clock - firstSequencerClock.Value).TotalSeconds;
                        }
                        data.SequencerPresses.Add(new SequencerPress
                        {
                            Index = data.SequencerPresses.Count + 1,
                            Group = parts[0].Trim(),
                            Button = parts[1].Trim(),
                            Mood = parts[2].Trim(),
                            Time = relative
                        });
                        break;
                }
            }

            return data;
        }

        private static bool TryValueAfter(string line, string prefix, out string value)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = line.Substring(prefix.Length).Trim();
                return true;
            }
            value = null;
            return false;
        }

        private static float? ParseFloat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace(',', '.');
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
        }

        private static int? ParseInt(string s)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;
        }
    }
}
