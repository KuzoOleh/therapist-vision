using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TherapistVision.Reports
{
    // Entry point used by the UI: CSV in, PDF out.
    public static class SessionReportGenerator
    {
        // Returns the path of the written PDF (next to the CSV, same name, unless pdfPath is given).
        // `fontPath` should point at a .ttf with Cyrillic glyphs; when null or missing, common
        // system fonts are tried.
        public static string GenerateFromCsv(string csvPath, string pdfPath = null, string fontPath = null)
        {
            SessionReportData data = SessionCsvParser.ParseFile(csvPath);
            TrueTypeFont font = ReportFonts.Load(fontPath);
            byte[] pdf = new SessionReportBuilder(data, font).Build();

            if (string.IsNullOrEmpty(pdfPath))
                pdfPath = Path.ChangeExtension(csvPath, ".pdf");

            string directory = Path.GetDirectoryName(pdfPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(pdfPath, pdf);
            return pdfPath;
        }
    }

    public static class ReportFonts
    {
        private static readonly string[] SystemFallbacks =
        {
            @"C:\Windows\Fonts\arial.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/Library/Fonts/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        };

        public static TrueTypeFont Load(string preferredPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(preferredPath)) candidates.Add(preferredPath);
            candidates.AddRange(SystemFallbacks);

            var errors = new List<string>();
            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var font = TrueTypeFont.Load(path);
                    if (font.HasGlyph('Ж')) return font; // must be able to render Ukrainian
                    errors.Add($"{path}: no Cyrillic glyphs");
                }
                catch (Exception ex)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }

            throw new FileNotFoundException("No usable TrueType font with Cyrillic glyphs was found for the PDF report. " +
                                            "Expected StreamingAssets/Fonts/LiberationSans.ttf. " + string.Join("; ", errors));
        }
    }

    // Lays out the session report. Coordinates are in points (A4 = 595 x 842), origin top-left.
    // `y` is always the top of the next free line on the current page.
    internal sealed class SessionReportBuilder
    {
        // ---------------------------------------------------------------- style
        private const float PageW = PdfDocument.A4Width;
        private const float PageH = PdfDocument.A4Height;
        private const float Margin = 40f;
        private const float ContentW = PageW - 2 * Margin;
        private const float Bottom = PageH - 52f;
        private const float LabelColumnW = 122f;

        private static readonly PdfColor Ink = PdfColor.Hex("#0b0b0b");
        private static readonly PdfColor Ink2 = PdfColor.Hex("#52514e");
        private static readonly PdfColor Muted = PdfColor.Hex("#8a8984");
        private static readonly PdfColor Grid = PdfColor.Hex("#e6e5e1");
        private static readonly PdfColor Axis = PdfColor.Hex("#b9b8b2");
        private static readonly PdfColor Band = PdfColor.Hex("#f6f5f2");
        private static readonly PdfColor TileBg = PdfColor.Hex("#f3f2ee");
        private static readonly PdfColor White = PdfColor.Hex("#ffffff");
        private static readonly PdfColor OtherColor = PdfColor.Hex("#a3a29c");
        private static readonly PdfColor Accent = PdfColor.Hex("#2a78d6");

        // Categorical slots in fixed order (validated CVD-safe order — never cycled; a 9th+
        // entity folds into "Інші").
        private static readonly PdfColor[] Palette =
        {
            PdfColor.Hex("#2a78d6"), PdfColor.Hex("#eb6834"), PdfColor.Hex("#1baf7a"), PdfColor.Hex("#eda100"),
            PdfColor.Hex("#e87ba4"), PdfColor.Hex("#008300"), PdfColor.Hex("#6250d6"), PdfColor.Hex("#e34948"),
        };

        private const string OtherLabel = "Інші";

        // ---------------------------------------------------------------- state
        private readonly SessionReportData data;
        private readonly PdfDocument doc;
        private PdfCanvas page;
        private float y;

        private readonly List<string> instruments;                       // most-hit first
        private readonly Dictionary<string, PdfColor> instrumentColors;
        private readonly List<string> sequencerGroups;
        private readonly Dictionary<string, PdfColor> groupColors;
        private readonly bool timed;
        private readonly float duration;

        public SessionReportBuilder(SessionReportData data, TrueTypeFont font)
        {
            this.data = data;
            doc = new PdfDocument(font);

            instruments = data.InstrumentHits.GroupBy(h => h.Instrument)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).ToList();
            instrumentColors = AssignColors(instruments);

            sequencerGroups = data.SequencerPresses.GroupBy(p => GroupName(p))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).ToList();
            groupColors = AssignColors(sequencerGroups);

            timed = data.HasEventTimes;
            duration = Math.Max(data.DurationSeconds, 1f);
        }

        public byte[] Build()
        {
            string patient = data.Get("patient") ?? "невідомий пацієнт";
            doc.Title = $"Звіт сесії музикотерапії — {patient}";
            doc.Subject = "TherapistVision session report";

            NewPage();
            DrawHeader(patient);
            DrawKpiTiles();
            DrawSessionTimelineSection();
            DrawInstrumentsSection();
            DrawSequencerSection();
            DrawLusherSection();
            DrawFooters(patient);

            return doc.Save();
        }

        // ================================================================ header

        private void DrawHeader(string patient)
        {
            page.FillRect(Margin, y, 4, 34, Accent);
            page.Text("Звіт сесії музикотерапії", Margin + 14, y + 17, 20, Ink, bold: true);
            page.Text($"Сформовано TherapistVision {DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}",
                Margin + 14, y + 32, 8.5f, Muted);
            y += 50;

            var rows = new List<(string, string)>
            {
                ("Пацієнт", patient),
                ("Спеціаліст", data.Get("therapist") ?? "—"),
                ("Дата сесії", data.Get("session_date") ?? "—"),
                ("Початок сесії", FormatSessionStart(data.Get("session_start"))),
                ("Тривалість", data.DurationSeconds > 0 ? FormatDurationLong(data.DurationSeconds) : "—"),
                ("ID сесії", data.Get("session_id") ?? "—"),
                ("Версія VR-застосунку", data.Get("app_version") ?? "—"),
                ("Файл даних", string.IsNullOrEmpty(data.SourceFileName) ? "—" : data.SourceFileName),
            };

            float colW = ContentW / 2f;
            for (int i = 0; i < rows.Count; i++)
            {
                float x = Margin + (i % 2) * colW;
                float rowTop = y + (i / 2) * 26;
                page.Text(rows[i].Item1, x, rowTop + 8, 7.5f, Muted);
                page.Text(page.Fit(rows[i].Item2, 10, colW - 12), x, rowTop + 20, 10, Ink);
            }
            y += (rows.Count + 1) / 2 * 26 + 6;

            if (data.IsLegacyFormat)
            {
                Note("Файл записаний старою версією VR-застосунку: у ньому немає часу окремих подій, " +
                     "тому графіки побудовано за порядком дій, а не за часом.");
            }

            Rule();
        }

        private void DrawKpiTiles()
        {
            var tiles = new List<(string value, string label)>
            {
                (data.DurationSeconds > 0 ? FormatClock(data.DurationSeconds) : "—", "тривалість сесії"),
                (data.InstrumentHits.Count.ToString(CultureInfo.InvariantCulture), "ударів / струшувань"),
                (instruments.Count.ToString(CultureInfo.InvariantCulture), "інструментів задіяно"),
                (data.SequencerPresses.Count.ToString(CultureInfo.InvariantCulture), "запусків у секвенсорі"),
                ($"{(data.LusherFirst.Count > 0 ? 1 : 0) + (data.LusherSecond.Count > 0 ? 1 : 0)} з 2", "тести Люшера"),
            };

            const float gap = 8f;
            const float tileH = 54f;
            float tileW = (ContentW - gap * (tiles.Count - 1)) / tiles.Count;
            Ensure(tileH + 16);
            for (int i = 0; i < tiles.Count; i++)
            {
                float x = Margin + i * (tileW + gap);
                page.FillRoundedRect(x, y, tileW, tileH, 6, TileBg);
                page.Text(tiles[i].value, x + 10, y + 26, 18, Ink, bold: true);
                page.Text(page.Fit(tiles[i].label, 8, tileW - 16), x + 10, y + 43, 8, Ink2);
            }
            y += tileH + 18;
        }

        // ======================================================= 1. session timeline

        private void DrawSessionTimelineSection()
        {
            SectionTitle("1. Хронологія сесії",
                timed
                    ? "Кожна дія пацієнта на спільній шкалі часу. Висота риски — сила удару; квадрати — вибір кольору в тесті Люшера (у кольорі вибору)."
                    : "Дії пацієнта за порядком виконання (у файлі немає часу подій). Висота риски — сила удару.");

            var lanes = new List<Lane>();
            float forceScale = ForceScaleMax();

            if (timed)
            {
                foreach (string instrument in instruments)
                {
                    lanes.Add(new Lane
                    {
                        Label = instrument,
                        Swatch = instrumentColors[instrument],
                        Events = data.InstrumentHits.Where(h => h.Instrument == instrument && h.Time.HasValue)
                            .Select(h => new LaneEvent { X = h.Time.Value, Color = instrumentColors[instrument], Strength = h.Force / forceScale })
                            .ToList()
                    });
                }

                if (data.SequencerPresses.Count > 0)
                {
                    lanes.Add(new Lane
                    {
                        Label = "Секвенсор",
                        Swatch = Ink2,
                        Events = data.SequencerPresses.Where(p => p.Time.HasValue)
                            .Select(p => new LaneEvent { X = p.Time.Value, Color = Ink2, Strength = 1f })
                            .ToList()
                    });
                }

                AddLusherLane(lanes, "Тест Люшера 1", data.LusherFirst);
                AddLusherLane(lanes, "Тест Люшера 2", data.LusherSecond);

                if (lanes.All(l => l.Events.Count == 0))
                {
                    Note("У сесії не зафіксовано жодної дії.");
                    return;
                }

                DrawLaneChart(lanes, 0f, duration, isTime: true, axisCaption: "час від початку сесії, хв:с");
            }
            else
            {
                // No timestamps: show the order of hits per instrument.
                int maxHits = 0;
                foreach (string instrument in instruments)
                {
                    var hits = data.InstrumentHits.Where(h => h.Instrument == instrument).ToList();
                    maxHits = Math.Max(maxHits, hits.Count);
                    lanes.Add(new Lane
                    {
                        Label = instrument,
                        Swatch = instrumentColors[instrument],
                        Events = hits.Select((h, i) => new LaneEvent { X = i + 1, Color = instrumentColors[instrument], Strength = h.Force / forceScale }).ToList()
                    });
                }

                if (lanes.Count == 0)
                {
                    Note("У файлі немає подій з інструментами; результати тесту Люшера та секвенсора — у розділах нижче.");
                    return;
                }

                DrawLaneChart(lanes, 0f, Math.Max(maxHits, 1), isTime: false, axisCaption: "номер удару по інструменту");
            }
        }

        private void AddLusherLane(List<Lane> lanes, string label, List<ColorChoice> choices)
        {
            if (choices.Count == 0) return;
            lanes.Add(new Lane
            {
                Label = label,
                Swatch = Ink2,
                Events = choices.Where(c => c.Time.HasValue)
                    .Select(c => new LaneEvent { X = c.Time.Value, Color = LusherColor(c.Color).Color, Square = true })
                    .ToList()
            });
        }

        // ============================================================ 2. instruments

        private void DrawInstrumentsSection()
        {
            SectionTitle("2. Інструменти",
                "Барабани, тарілки, маракаси та клавіші. Сила — відносна (0 — ледь торкнувся, 1 — максимально сильний удар).");

            if (data.InstrumentHits.Count == 0)
            {
                Note("Пацієнт не грав на інструментах у цій сесії.");
                return;
            }

            // 2.1 hits per instrument
            ChartTitle("Кількість ударів по кожному інструменту", null);
            DrawHorizontalBars(instruments.Select(i => new BarItem
            {
                Label = i,
                Value = data.InstrumentHits.Count(h => h.Instrument == i),
                Color = instrumentColors[i]
            }).ToList(), v => v.ToString("0", CultureInfo.InvariantCulture), fixedMax: null);

            // 2.2 average vs max force
            ChartTitle("Сила ударів: середня і максимальна",
                "Смуга — середня сила, темна риска — найсильніший удар по цьому інструменту.");
            DrawAverageMaxChart();

            // 2.3 every hit
            ChartTitle(timed ? "Кожен удар у часі" : "Кожен удар за порядком",
                timed
                    ? "Точка — окремий удар: по горизонталі час від початку сесії, по вертикалі сила."
                    : "Точка — окремий удар: по горизонталі номер удару по цьому інструменту, по вертикалі сила.");
            DrawForceSmallMultiples();

            // 2.4 activity over time
            if (timed)
            {
                ChartTitle("Активність протягом сесії",
                    "Скільки ударів зроблено за кожен проміжок часу, з розбивкою за інструментами.", keepWith: 200f);
                DrawActivityHistogram();
            }
        }

        private void DrawAverageMaxChart()
        {
            var rows = instruments.Select(i =>
            {
                var forces = data.InstrumentHits.Where(h => h.Instrument == i).Select(h => h.Force).ToList();
                return (name: i, avg: forces.Average(), max: forces.Max());
            }).ToList();

            float scaleMax = NiceCeiling(Math.Max(1f, rows.Max(r => r.max)));
            const float rowH = 20f;
            const float barH = 10f;
            float plotX = Margin + LabelColumnW;
            float plotW = ContentW - LabelColumnW - 92;

            DrawLegend(new List<(string, PdfColor, bool)> { ("середня сила", Ink2, false), ("максимальна сила", Ink, true) });

            Chunked(rows.Count, rowH, 22, (start, count, top) =>
            {
                float chartBottom = top + count * rowH;
                DrawValueGrid(plotX, top, plotW, chartBottom - top, scaleMax, v => v.ToString("0.##", CultureInfo.InvariantCulture), integerTicks: false);

                for (int r = 0; r < count; r++)
                {
                    var row = rows[start + r];
                    float rowTop = top + r * rowH;
                    PdfColor color = instrumentColors[row.name];
                    page.FillRect(Margin + 2, rowTop + rowH / 2 - 3, 6, 6, color);
                    page.Text(page.Fit(row.name, 8.5f, LabelColumnW - 18), Margin + 12, rowTop + rowH / 2 + 3, 8.5f, Ink);

                    float barTop = rowTop + (rowH - barH) / 2;
                    page.FillBarRightRounded(plotX, barTop, plotW * row.avg / scaleMax, barH, 3, color);
                    float maxX = plotX + plotW * row.max / scaleMax;
                    page.Line(maxX, rowTop + 2, maxX, rowTop + rowH - 2, Ink, 2f);
                    page.Text($"сер. {row.avg.ToString("0.00", CultureInfo.InvariantCulture)} · макс. {row.max.ToString("0.00", CultureInfo.InvariantCulture)}",
                        plotX + plotW + 8, rowTop + rowH / 2 + 3, 7.5f, Ink2);
                }
            });
        }

        private void DrawForceSmallMultiples()
        {
            float yMax = ForceScaleMax();
            const float rowH = 46f;
            float plotX = Margin + LabelColumnW;
            float plotW = ContentW - LabelColumnW - 8;

            float xMax = timed
                ? duration
                : Math.Max(1, instruments.Max(i => data.InstrumentHits.Count(h => h.Instrument == i)));

            Chunked(instruments.Count, rowH, 24, (start, count, top) =>
            {
                for (int r = 0; r < count; r++)
                {
                    string instrument = instruments[start + r];
                    PdfColor color = instrumentColors[instrument];
                    var hits = data.InstrumentHits.Where(h => h.Instrument == instrument).ToList();

                    float rowTop = top + r * rowH;
                    float plotTop = rowTop + 6;
                    float plotH = rowH - 14;

                    if (r % 2 == 0) page.FillRect(Margin, rowTop, ContentW, rowH, Band);

                    page.FillRect(Margin + 2, rowTop + 12, 6, 6, color);
                    page.Text(page.Fit(instrument, 8.5f, LabelColumnW - 40), Margin + 12, rowTop + 18, 8.5f, Ink);
                    page.Text($"{hits.Count} {Plural(hits.Count, "удар", "удари", "ударів")}", Margin + 12, rowTop + 30, 7.5f, Ink2);

                    // mini y-axis: 0 and max
                    page.Line(plotX, plotTop, plotX + plotW, plotTop, Grid, 0.5f, dashed: true);
                    page.Line(plotX, plotTop + plotH, plotX + plotW, plotTop + plotH, Axis, 0.6f);
                    page.Text(yMax.ToString("0.##", CultureInfo.InvariantCulture), plotX - 4, plotTop + 3, 6.5f, Muted, align: TextAlign.Right);
                    page.Text("0", plotX - 4, plotTop + plotH + 2, 6.5f, Muted, align: TextAlign.Right);

                    var xs = new List<float>();
                    var ys = new List<float>();
                    for (int i = 0; i < hits.Count; i++)
                    {
                        float xValue = timed ? (hits[i].Time ?? 0f) : i + 1;
                        xs.Add(MapX(xValue, 0f, xMax, plotX, plotW, timed));
                        ys.Add(plotTop + plotH - plotH * Math.Min(hits[i].Force, yMax) / yMax);
                    }

                    page.Polyline(xs, ys, color.Tint(0.6f), 0.8f);
                    for (int i = 0; i < xs.Count; i++)
                    {
                        page.FillCircleWithRing(xs[i], ys[i], 2.3f, color, White, 0.6f);
                    }
                }

                float axisTop = top + count * rowH;
                DrawXAxis(plotX, plotW, axisTop, 0f, xMax, timed, timed ? "час від початку сесії, хв:с" : "номер удару");
            });
        }

        private void DrawActivityHistogram()
        {
            float binSize = new[] { 5f, 10f, 15f, 20f, 30f, 60f, 120f, 300f, 600f }.FirstOrDefault(b => duration / b <= 24f);
            if (binSize <= 0) binSize = (float)Math.Ceiling(duration / 24f);
            int binCount = Math.Max(1, (int)Math.Ceiling(duration / binSize));

            // Fold the tail past 8 instruments into "Інші" so colors are never cycled.
            List<string> series = instruments.Count <= Palette.Length
                ? instruments.ToList()
                : instruments.Take(Palette.Length - 1).Concat(new[] { OtherLabel }).ToList();
            string SeriesOf(string instrument) => series.Contains(instrument) ? instrument : OtherLabel;
            PdfColor SeriesColor(string s) => s == OtherLabel ? OtherColor : instrumentColors[s];

            var counts = new int[binCount, series.Count];
            foreach (var hit in data.InstrumentHits.Where(h => h.Time.HasValue))
            {
                int bin = Math.Min(binCount - 1, (int)(hit.Time.Value / binSize));
                counts[bin, series.IndexOf(SeriesOf(hit.Instrument))]++;
            }

            int maxTotal = 1;
            for (int b = 0; b < binCount; b++)
            {
                int total = 0;
                for (int s = 0; s < series.Count; s++) total += counts[b, s];
                maxTotal = Math.Max(maxTotal, total);
            }

            const float chartH = 140f;
            Ensure(chartH + 52);
            y += 12; // room for the "ударів" unit label
            float plotX = Margin + 30;
            float plotW = ContentW - 34;
            float top = y;
            float baseline = top + chartH;

            float step = NiceStep(maxTotal, 5, integer: true);
            float yMax = (float)Math.Ceiling(maxTotal / step) * step;
            for (float v = 0; v <= yMax + 0.001f; v += step)
            {
                float gy = baseline - chartH * v / yMax;
                page.Line(plotX, gy, plotX + plotW, gy, v == 0 ? Axis : Grid, v == 0 ? 0.8f : 0.5f);
                page.Text(v.ToString("0", CultureInfo.InvariantCulture), plotX - 5, gy + 2.5f, 7, Muted, align: TextAlign.Right);
            }

            float slot = plotW / binCount;
            float barW = Math.Max(2f, Math.Min(26f, slot - 3f));
            for (int b = 0; b < binCount; b++)
            {
                float x = plotX + b * slot + (slot - barW) / 2;
                float cursor = baseline;
                int topSeries = -1;
                for (int s = 0; s < series.Count; s++) if (counts[b, s] > 0) topSeries = s;

                for (int s = 0; s < series.Count; s++)
                {
                    int c = counts[b, s];
                    if (c == 0) continue;
                    float h = chartH * c / yMax;
                    float segmentH = Math.Max(0.5f, h - 1f); // 1pt surface gap between stacked segments
                    if (s == topSeries)
                        page.FillBarTopRounded(x, cursor - h, barW, segmentH, 2.5f, SeriesColor(series[s]));
                    else
                        page.FillRect(x, cursor - h, barW, segmentH, SeriesColor(series[s]));
                    cursor -= h;
                }
            }

            // x labels: bin start times, thinned so they never collide
            float labelEvery = (float)Math.Ceiling(28f / slot);
            for (int b = 0; b <= binCount; b += (int)Math.Max(1, labelEvery))
            {
                float x = plotX + b * slot;
                page.Line(x, baseline, x, baseline + 3, Axis, 0.6f);
                page.Text(FormatClock(b * binSize), x, baseline + 12, 7, Muted, align: TextAlign.Center);
            }
            page.Text($"хв:с · один стовпчик = {FormatBin(binSize)}", plotX + plotW, baseline + 24, 7, Muted, align: TextAlign.Right);
            page.Text("ударів", plotX - 26, top - 4, 7, Muted);

            y = baseline + 32;
            DrawLegend(series.Select(s => (s, SeriesColor(s), false)).ToList());
        }

        // ============================================================== 3. sequencer

        private void DrawSequencerSection()
        {
            SectionTitle("3. Секвенсор",
                "Запуски музичних петель на панелі секвенсора (фіксуються, коли ввімкнено запис кнопкою Record).");

            if (data.SequencerPresses.Count == 0)
            {
                Note("Секвенсор у цій сесії не використовувався або запис не було ввімкнено.");
                return;
            }

            // 3.1 per button
            var buttons = data.SequencerPresses.GroupBy(p => (button: p.Button, group: GroupName(p)))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.group, StringComparer.Ordinal).ThenBy(g => g.Key.button, StringComparer.Ordinal)
                .Select(g => g.Key).ToList();

            ChartTitle("Запуски кожної кнопки", "Колір — група звуків.");
            DrawHorizontalBars(buttons.Select(b => new BarItem
            {
                Label = b.group == b.button ? b.button : $"{b.button} ({b.group})",
                Value = data.SequencerPresses.Count(p => p.Button == b.button && GroupName(p) == b.group),
                Color = groupColors[b.group]
            }).ToList(), v => v.ToString("0", CultureInfo.InvariantCulture), fixedMax: null);
            DrawLegend(sequencerGroups.Select(g => (g, groupColors[g], false)).ToList());

            // 3.2 per group
            if (sequencerGroups.Count > 1)
            {
                ChartTitle("Запуски за групами звуків", null);
                DrawHorizontalBars(sequencerGroups.Select(g => new BarItem
                {
                    Label = g,
                    Value = data.SequencerPresses.Count(p => GroupName(p) == g),
                    Color = groupColors[g]
                }).ToList(), v => v.ToString("0", CultureInfo.InvariantCulture), fixedMax: null);
            }

            // 3.3 per mood
            var moods = data.SequencerPresses.GroupBy(p => string.IsNullOrWhiteSpace(p.Mood) ? "без позначки" : p.Mood)
                .OrderByDescending(g => g.Count()).ToList();
            if (moods.Count > 0)
            {
                ChartTitle("Настрій обраних звуків", "Скільки разів запускалися звуки з кожною позначкою настрою.");
                DrawHorizontalBars(moods.Select(g => new BarItem { Label = g.Key, Value = g.Count(), Color = Accent }).ToList(),
                    v => v.ToString("0", CultureInfo.InvariantCulture), fixedMax: null);
            }

            // 3.4 timeline per button
            bool sequencerTimed = data.SequencerPresses.Any(p => p.Time.HasValue);
            ChartTitle(sequencerTimed ? "Коли запускалася кожна кнопка" : "Порядок запусків",
                data.IsLegacyFormat && sequencerTimed ? "Час відраховано від першого натискання на секвенсорі." : null);

            // Zoom into the part of the session where the sequencer was actually used — on the
            // full-session scale a few minutes of presses collapse into one cluster.
            float xMin = 0f;
            float xMax = data.SequencerPresses.Count;
            if (sequencerTimed)
            {
                float first = data.SequencerPresses.Where(p => p.Time.HasValue).Min(p => p.Time.Value);
                float last = data.SequencerPresses.Where(p => p.Time.HasValue).Max(p => p.Time.Value);
                float pad = Math.Max(5f, (last - first) * 0.08f);
                xMin = Math.Max(0f, first - pad);
                xMax = Math.Max(xMin + 20f, last + pad);
                if (!data.IsLegacyFormat) xMax = Math.Min(xMax, Math.Max(duration, last));
            }

            var lanes = buttons.Select(b => new Lane
            {
                Label = b.group == b.button ? b.button : $"{b.button} ({b.group})",
                Swatch = groupColors[b.group],
                Events = data.SequencerPresses
                    .Where(p => p.Button == b.button && GroupName(p) == b.group)
                    .Select(p => new LaneEvent { X = sequencerTimed ? (p.Time ?? 0f) : p.Index, Color = groupColors[b.group], Strength = 1f })
                    .ToList()
            }).ToList();

            DrawLaneChart(lanes, xMin, xMax, isTime: sequencerTimed,
                axisCaption: sequencerTimed ? (data.IsLegacyFormat ? "час від першого натискання, хв:с" : "час від початку сесії, хв:с") : "порядковий номер запуску");
        }

        // ================================================================ 4. Lüscher

        private void DrawLusherSection()
        {
            SectionTitle("4. Колірний тест Люшера",
                "Порядок вибору кольорів: 1 — найприємніший для пацієнта, 8 — найменш приємний. Тест проходять на початку і в кінці сесії.");

            if (data.LusherFirst.Count == 0 && data.LusherSecond.Count == 0)
            {
                Note("Тест Люшера в цій сесії не проходили.");
                return;
            }

            ChartTitle("Послідовність вибору кольорів", timed ? "Під кольором — скільки секунд пацієнт думав над вибором (від попереднього вибору)." : null, keepWith: 140f);
            DrawLusherRow("Тест 1 (початок)", data.LusherFirst);
            DrawLusherRow("Тест 2 (кінець)", data.LusherSecond);

            if (data.LusherFirst.Count > 0 && data.LusherSecond.Count > 0)
            {
                ChartTitle("Як змінилися позиції кольорів", "Лінія з'єднує позицію кольору в першому та другому тесті. ↑ — колір став приємнішим, ↓ — менш приємним.", keepWith: 220f);
                DrawLusherSlopeChart();
            }

            if (timed && (data.LusherFirst.Any(c => c.Time.HasValue) || data.LusherSecond.Any(c => c.Time.HasValue)))
            {
                ChartTitle("Час на кожен вибір", "Секунди між сусідніми виборами. Перший вибір кожного тесту не має попереднього, тому не показаний.", keepWith: 170f);
                DrawLusherReactionChart();
            }
        }

        private void DrawLusherRow(string label, List<ColorChoice> choices)
        {
            const float rowH = 66f;
            Ensure(rowH);
            float labelW = 92f;
            page.Text(label, Margin, y + 22, 9, Ink, bold: true);

            if (choices.Count == 0)
            {
                page.Text("тест не проходили", Margin + labelW, y + 22, 9, Muted);
                y += 34;
                return;
            }

            var ordered = choices.OrderBy(c => c.Position).ToList();
            const float gap = 6f;
            int slots = Math.Max(8, ordered.Count);
            float swatchW = (ContentW - labelW - gap * (slots - 1)) / slots;
            const float swatchH = 32f;

            for (int i = 0; i < ordered.Count; i++)
            {
                var choice = ordered[i];
                var info = LusherColor(choice.Color);
                float x = Margin + labelW + i * (swatchW + gap);

                page.FillRoundedRect(x, y + 2, swatchW, swatchH, 4, info.Color);
                if (info.Color.Luminance > 0.85f) page.StrokeRect(x, y + 2, swatchW, swatchH, Axis, 0.5f);
                PdfColor numberColor = info.Color.Luminance > 0.55f ? Ink : White;
                page.Text(choice.Position.ToString(CultureInfo.InvariantCulture), x + swatchW / 2, y + 23, 13, numberColor, bold: true, align: TextAlign.Center);
                page.Text(page.Fit(info.Name, 7.5f, swatchW + gap - 1), x + swatchW / 2, y + swatchH + 13, 7.5f, Ink, align: TextAlign.Center);

                if (timed && i > 0 && choice.Time.HasValue && ordered[i - 1].Time.HasValue)
                {
                    float think = choice.Time.Value - ordered[i - 1].Time.Value;
                    page.Text($"{think.ToString("0.0", CultureInfo.InvariantCulture)} с", x + swatchW / 2, y + swatchH + 24, 7, Ink2, align: TextAlign.Center);
                }
            }
            y += rowH + 4;
        }

        private void DrawLusherSlopeChart()
        {
            int positions = Math.Max(8, Math.Max(data.LusherFirst.Count, data.LusherSecond.Count));
            const float rowH = 21f;
            float chartH = 22 + positions * rowH;
            Ensure(chartH + 10);

            float leftX = Margin + 170;
            float rightX = Margin + ContentW - 170;
            float top = y + 18;

            page.Text("Тест 1", leftX, y + 8, 9, Ink, bold: true, align: TextAlign.Center);
            page.Text("Тест 2", rightX, y + 8, 9, Ink, bold: true, align: TextAlign.Center);

            float RowY(int position) => top + (position - 1) * rowH + rowH / 2;

            for (int p = 1; p <= positions; p++)
            {
                page.Line(leftX, RowY(p), rightX, RowY(p), Grid, 0.4f, dashed: true);
            }

            var second = data.LusherSecond.GroupBy(c => Normalize(c.Color)).ToDictionary(g => g.Key, g => g.First().Position);
            var first = data.LusherFirst.GroupBy(c => Normalize(c.Color)).ToDictionary(g => g.Key, g => g.First().Position);

            foreach (var choice in data.LusherFirst.OrderBy(c => c.Position))
            {
                var info = LusherColor(choice.Color);
                float y1 = RowY(choice.Position);
                page.Text($"{choice.Position}. {info.Name}", leftX - 12, y1 + 3, 8.5f, Ink, align: TextAlign.Right);

                if (second.TryGetValue(Normalize(choice.Color), out int pos2))
                {
                    page.Line(leftX, y1, rightX, RowY(pos2), info.Color, 2.4f);
                }
                page.FillCircleWithRing(leftX, y1, 4, info.Color, White, 1f);
            }

            foreach (var choice in data.LusherSecond.OrderBy(c => c.Position))
            {
                var info = LusherColor(choice.Color);
                float y2 = RowY(choice.Position);
                page.FillCircleWithRing(rightX, y2, 4, info.Color, White, 1f);

                string change = "нове";
                if (first.TryGetValue(Normalize(choice.Color), out int pos1))
                {
                    int delta = pos1 - choice.Position; // positive = moved up (more preferred)
                    change = delta > 0 ? $"↑{delta}" : delta < 0 ? $"↓{-delta}" : "без змін";
                }
                page.Text($"{choice.Position}. {info.Name}", rightX + 12, y2 + 3, 8.5f, Ink);
                page.Text(change, rightX + 12 + page.MeasureText($"{choice.Position}. {info.Name}", 8.5f) + 6, y2 + 3, 8, Ink2);
            }

            y += chartH + 6;

            // Plain-language summary of the biggest moves (description only, no interpretation).
            var moves = data.LusherSecond
                .Where(c => first.ContainsKey(Normalize(c.Color)))
                .Select(c => (name: LusherColor(c.Color).Name, delta: first[Normalize(c.Color)] - c.Position))
                .ToList();
            if (moves.Count > 0)
            {
                var up = moves.OrderByDescending(m => m.delta).First();
                var down = moves.OrderBy(m => m.delta).First();
                int unchanged = moves.Count(m => m.delta == 0);
                string summary = up.delta > 0
                    ? $"Найбільше піднявся: {up.name} (на {up.delta} {Plural(up.delta, "позицію", "позиції", "позицій")}). "
                    : "Жоден колір не піднявся. ";
                summary += down.delta < 0
                    ? $"Найбільше опустився: {down.name} (на {-down.delta} {Plural(-down.delta, "позицію", "позиції", "позицій")}). "
                    : "Жоден колір не опустився. ";
                summary += $"Без змін: {unchanged} з {moves.Count}.";
                Paragraph(summary, 8.5f, Ink2);
            }
        }

        private void DrawLusherReactionChart()
        {
            List<float?> Deltas(List<ColorChoice> choices)
            {
                var ordered = choices.OrderBy(c => c.Position).ToList();
                var result = new List<float?>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    result.Add(i > 0 && ordered[i].Time.HasValue && ordered[i - 1].Time.HasValue
                        ? ordered[i].Time.Value - ordered[i - 1].Time.Value
                        : (float?)null);
                }
                return result;
            }

            var d1 = Deltas(data.LusherFirst);
            var d2 = Deltas(data.LusherSecond);
            int positions = Math.Max(d1.Count, d2.Count);
            float maxValue = Math.Max(0.1f, d1.Concat(d2).Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(0.1f).Max());

            const float chartH = 110f;
            Ensure(chartH + 50);
            DrawLegend(new List<(string, PdfColor, bool)> { ("Тест 1", Palette[0], false), ("Тест 2", Palette[1], false) });
            y += 12; // room for the "секунд" unit label

            float plotX = Margin + 34;
            float plotW = ContentW - 38;
            float top = y;
            float baseline = top + chartH;

            float step = NiceStep(maxValue, 4, integer: false);
            float yMax = (float)Math.Ceiling(maxValue / step) * step;
            for (float v = 0; v <= yMax + step * 0.01f; v += step)
            {
                float gy = baseline - chartH * v / yMax;
                page.Line(plotX, gy, plotX + plotW, gy, v == 0 ? Axis : Grid, v == 0 ? 0.8f : 0.5f);
                page.Text(v.ToString("0.#", CultureInfo.InvariantCulture), plotX - 5, gy + 2.5f, 7, Muted, align: TextAlign.Right);
            }
            page.Text("секунд", plotX - 30, top - 4, 7, Muted);

            float slot = plotW / Math.Max(1, positions);
            float barW = Math.Min(16f, (slot - 8f) / 2f);
            for (int p = 0; p < positions; p++)
            {
                float center = plotX + p * slot + slot / 2;
                if (p < d1.Count && d1[p].HasValue)
                    page.FillBarTopRounded(center - barW - 1, baseline - chartH * d1[p].Value / yMax, barW, chartH * d1[p].Value / yMax, 2.5f, Palette[0]);
                if (p < d2.Count && d2[p].HasValue)
                    page.FillBarTopRounded(center + 1, baseline - chartH * d2[p].Value / yMax, barW, chartH * d2[p].Value / yMax, 2.5f, Palette[1]);
                page.Text($"вибір {p + 1}", center, baseline + 12, 7, Muted, align: TextAlign.Center);
            }

            y = baseline + 22;
        }

        // ============================================================ chart helpers

        private sealed class Lane
        {
            public string Label;
            public PdfColor Swatch;
            public List<LaneEvent> Events = new List<LaneEvent>();
        }

        private sealed class LaneEvent
        {
            public float X;
            public PdfColor Color;
            public float Strength = 1f; // 0..1 → tick height
            public bool Square;
        }

        private sealed class BarItem
        {
            public string Label;
            public float Value;
            public PdfColor Color;
        }

        private void DrawLaneChart(List<Lane> lanes, float xMin, float xMax, bool isTime, string axisCaption)
        {
            const float laneH = 20f;
            float plotX = Margin + LabelColumnW;
            float plotW = ContentW - LabelColumnW - 8;

            Chunked(lanes.Count, laneH, 26, (start, count, top) =>
            {
                float chartBottom = top + count * laneH;

                for (int i = 0; i < count; i++)
                {
                    if (i % 2 == 0) page.FillRect(Margin, top + i * laneH, ContentW, laneH, Band);
                }
                foreach (float tick in Ticks(xMin, xMax, isTime))
                {
                    float gx = MapX(tick, xMin, xMax, plotX, plotW, isTime);
                    page.Line(gx, top, gx, chartBottom, Grid, 0.5f);
                }

                for (int i = 0; i < count; i++)
                {
                    Lane lane = lanes[start + i];
                    float laneTop = top + i * laneH;
                    page.FillRect(Margin + 2, laneTop + laneH / 2 - 3, 6, 6, lane.Swatch);
                    page.Text(page.Fit(lane.Label, 8.5f, LabelColumnW - 34), Margin + 12, laneTop + laneH / 2 + 3, 8.5f, Ink);
                    page.Text(lane.Events.Count.ToString(CultureInfo.InvariantCulture), Margin + LabelColumnW - 6, laneTop + laneH / 2 + 3, 7.5f, Muted, align: TextAlign.Right);

                    float laneBottom = laneTop + laneH - 3;
                    foreach (LaneEvent e in lane.Events)
                    {
                        float ex = MapX(e.X, xMin, xMax, plotX, plotW, isTime);
                        if (e.Square)
                        {
                            page.FillRect(ex - 3.5f, laneTop + laneH / 2 - 3.5f, 7, 7, e.Color);
                            page.StrokeRect(ex - 3.5f, laneTop + laneH / 2 - 3.5f, 7, 7, Ink2, 0.4f);
                        }
                        else
                        {
                            float strength = Math.Max(0f, Math.Min(1f, e.Strength));
                            float h = (laneH - 6) * (0.3f + 0.7f * strength);
                            page.Line(ex, laneBottom, ex, laneBottom - h, e.Color, 1.6f);
                        }
                    }
                }

                page.Line(plotX, chartBottom, plotX + plotW, chartBottom, Axis, 0.6f);
                DrawXAxis(plotX, plotW, chartBottom, xMin, xMax, isTime, axisCaption);
            });
        }

        private void DrawHorizontalBars(List<BarItem> items, Func<float, string> format, float? fixedMax)
        {
            if (items.Count == 0) return;
            const float rowH = 18f;
            const float barH = 10f;
            float plotX = Margin + LabelColumnW;
            float plotW = ContentW - LabelColumnW - 40;
            float max = fixedMax ?? NiceCeiling(Math.Max(1f, items.Max(i => i.Value)));

            Chunked(items.Count, rowH, 20, (start, count, top) =>
            {
                float chartBottom = top + count * rowH;
                DrawValueGrid(plotX, top, plotW, chartBottom - top, max, format, integerTicks: true); // bar charts here are all counts

                for (int r = 0; r < count; r++)
                {
                    BarItem item = items[start + r];
                    float rowTop = top + r * rowH;
                    page.Text(page.Fit(item.Label, 8.5f, LabelColumnW - 10), Margin, rowTop + rowH / 2 + 3, 8.5f, Ink);
                    float w = plotW * item.Value / max;
                    page.FillBarRightRounded(plotX, rowTop + (rowH - barH) / 2, w, barH, 3, item.Color);
                    page.Text(format(item.Value), plotX + w + 5, rowTop + rowH / 2 + 3, 8, Ink2);
                }
            });
        }

        // Vertical gridlines + labels for a horizontal value axis (bars start at 0).
        private void DrawValueGrid(float plotX, float top, float plotW, float height, float max, Func<float, string> format, bool integerTicks)
        {
            float step = NiceStep(max, 5, integer: integerTicks);
            for (float v = 0; v <= max + step * 0.01f; v += step)
            {
                float gx = plotX + plotW * v / max;
                page.Line(gx, top, gx, top + height, v == 0 ? Axis : Grid, v == 0 ? 0.8f : 0.5f);
                page.Text(format(v), gx, top + height + 10, 7, Muted, align: TextAlign.Center);
            }
        }

        private void DrawXAxis(float plotX, float plotW, float axisTop, float xMin, float xMax, bool isTime, string caption)
        {
            foreach (float tick in Ticks(xMin, xMax, isTime))
            {
                float gx = MapX(tick, xMin, xMax, plotX, plotW, isTime);
                page.Line(gx, axisTop, gx, axisTop + 3, Axis, 0.6f);
                page.Text(isTime ? FormatClock(tick) : tick.ToString("0", CultureInfo.InvariantCulture), gx, axisTop + 11, 7, Muted, align: TextAlign.Center);
            }
            if (!string.IsNullOrEmpty(caption))
                page.Text(caption, plotX + plotW, axisTop + 21, 7, Muted, align: TextAlign.Right);
        }

        private static float MapX(float value, float xMin, float xMax, float plotX, float plotW, bool isTime)
        {
            if (isTime)
            {
                float span = Math.Max(0.001f, xMax - xMin);
                return plotX + plotW * (Math.Max(xMin, Math.Min(value, xMax)) - xMin) / span;
            }
            // ordinal positions 1..xMax, padded half a step on each side
            float slots = Math.Max(1f, xMax);
            return plotX + plotW * (value - 0.5f) / slots;
        }

        private static IEnumerable<float> Ticks(float xMin, float xMax, bool isTime)
        {
            if (isTime)
            {
                float span = Math.Max(0.001f, xMax - xMin);
                float[] steps = { 1, 2, 5, 10, 15, 20, 30, 60, 120, 180, 300, 600, 900, 1200, 1800, 3600 };
                float step = steps.FirstOrDefault(s => span / s <= 8f);
                if (step <= 0) step = (float)Math.Ceiling(span / 8f);
                for (float t = (float)Math.Ceiling(xMin / step - 1e-4) * step; t <= xMax + 0.001f; t += step) yield return t;
            }
            else
            {
                float step = Math.Max(1f, NiceStep(xMax, 10, integer: true));
                yield return 1;
                for (float t = step; t <= xMax + 0.001f; t += step) if (t > 1) yield return t;
            }
        }

        private void DrawLegend(List<(string label, PdfColor color, bool isTick)> entries)
        {
            if (entries.Count == 0) return;
            Ensure(16);
            float x = Margin;
            foreach (var entry in entries)
            {
                float w = 14 + page.MeasureText(entry.label, 8) + 14;
                if (x + w > Margin + ContentW)
                {
                    x = Margin;
                    y += 13;
                    Ensure(16);
                }
                if (entry.isTick) page.Line(x + 4, y + 2, x + 4, y + 10, entry.color, 2f);
                else page.FillRoundedRect(x, y + 2, 9, 8, 2, entry.color);
                page.Text(entry.label, x + 14, y + 9, 8, Ink2);
                x += w;
            }
            y += 20;
        }

        // Splits a row-based chart over pages when needed. `extra` = space for axis/labels
        // below the rows. Calls draw(firstRow, rowCount, topY) once per page chunk.
        private void Chunked(int total, float rowH, float extra, Action<int, int, float> draw)
        {
            int index = 0;
            while (index < total)
            {
                float available = Bottom - y - extra;
                int fit = (int)Math.Floor(available / rowH);
                int remaining = total - index;
                if (fit < Math.Min(3, remaining))
                {
                    NewPage();
                    continue;
                }

                int count = Math.Min(fit, remaining);
                float top = y;
                draw(index, count, top);
                y = top + count * rowH + extra;
                index += count;
            }
            y += 6;
        }

        // ============================================================ text helpers

        private void SectionTitle(string title, string description)
        {
            Ensure(180); // never leave a section title alone at the bottom of a page
            y += 6;
            page.Text(title, Margin, y + 14, 14, Ink, bold: true);
            y += 22;
            if (!string.IsNullOrEmpty(description)) Paragraph(description, 8.5f, Ink2);
            y += 4;
        }

        // keepWith = minimum height of the chart that follows, so a title is never left
        // alone at the bottom of a page.
        private void ChartTitle(string title, string caption, float keepWith = 70f)
        {
            Ensure(36 + keepWith);
            y += 4;
            page.Text(title, Margin, y + 10, 10.5f, Ink, bold: true);
            y += 16;
            if (!string.IsNullOrEmpty(caption)) Paragraph(caption, 8, Ink2);
            y += 4;
        }

        private void Paragraph(string text, float size, PdfColor color)
        {
            foreach (string line in page.Wrap(text, size, ContentW))
            {
                Ensure(size + 4);
                page.Text(line, Margin, y + size, size, color);
                y += size + 3.5f;
            }
            y += 2;
        }

        private void Note(string text)
        {
            var lines = page.Wrap(text, 8.5f, ContentW - 24);
            float h = lines.Count * 12 + 12;
            Ensure(h + 6);
            page.FillRoundedRect(Margin, y, ContentW, h, 4, TileBg);
            page.FillRect(Margin, y, 3, h, Axis);
            float ty = y + 15;
            foreach (string line in lines)
            {
                page.Text(line, Margin + 14, ty, 8.5f, Ink2);
                ty += 12;
            }
            y += h + 10;
        }

        private void Rule()
        {
            page.Line(Margin, y, Margin + ContentW, y, Grid, 0.8f);
            y += 14;
        }

        // ============================================================ pages

        private void NewPage()
        {
            page = doc.AddPage();
            y = Margin;
        }

        private void Ensure(float height)
        {
            if (y + height > Bottom) NewPage();
        }

        private void DrawFooters(string patient)
        {
            int total = doc.Pages.Count;
            string sessionId = data.Get("session_id");
            string left = sessionId != null ? $"Звіт сесії · {patient} · {sessionId}" : $"Звіт сесії · {patient}";
            for (int i = 0; i < total; i++)
            {
                PdfCanvas p = doc.Pages[i];
                p.Line(Margin, PageH - 34, Margin + ContentW, PageH - 34, Grid, 0.6f);
                p.Text(p.Fit(left, 7.5f, ContentW - 90), Margin, PageH - 22, 7.5f, Muted);
                p.Text($"Сторінка {i + 1} з {total}", Margin + ContentW, PageH - 22, 7.5f, Muted, align: TextAlign.Right);
            }
        }

        // ============================================================ data helpers

        private static Dictionary<string, PdfColor> AssignColors(List<string> orderedKeys)
        {
            var colors = new Dictionary<string, PdfColor>();
            bool fold = orderedKeys.Count > Palette.Length;
            for (int i = 0; i < orderedKeys.Count; i++)
            {
                colors[orderedKeys[i]] = !fold || i < Palette.Length - 1 ? Palette[i] : OtherColor;
            }
            return colors;
        }

        private static string GroupName(SequencerPress press)
        {
            return string.IsNullOrWhiteSpace(press.Group) ? "без групи" : press.Group;
        }

        // Upper end of the force axis: 1.0 for normalized data, larger if a source (e.g. piano
        // keys reporting speed) exceeds it.
        private float ForceScaleMax()
        {
            float max = data.InstrumentHits.Count > 0 ? data.InstrumentHits.Max(h => h.Force) : 1f;
            return max <= 1f ? 1f : NiceCeiling(max);
        }

        private static string Normalize(string color) => (color ?? "").Trim().ToLowerInvariant();

        private static (string Name, PdfColor Color) LusherColor(string raw)
        {
            switch (Normalize(raw))
            {
                case "grey": case "gray": case "сірий": return ("Сірий", PdfColor.Hex("#8f8f8c"));
                case "blue": case "синій": return ("Синій", PdfColor.Hex("#1f3f9b"));
                case "green": case "зелений": return ("Зелений", PdfColor.Hex("#1d7a5c"));
                case "red": case "червоний": return ("Червоний", PdfColor.Hex("#e2412f"));
                case "yellow": case "жовтий": return ("Жовтий", PdfColor.Hex("#f3d23a"));
                case "violet": case "purple": case "фіолетовий": return ("Фіолетовий", PdfColor.Hex("#7d3c98"));
                case "brown": case "коричневий": return ("Коричневий", PdfColor.Hex("#7a4b2a"));
                case "black": case "чорний": return ("Чорний", PdfColor.Hex("#151515"));
                default: return (string.IsNullOrWhiteSpace(raw) ? "?" : raw.Trim(), PdfColor.Hex("#cfcfcb"));
            }
        }

        // ============================================================ formatting

        private static float NiceStep(float max, int maxTicks, bool integer)
        {
            if (max <= 0) return 1;
            double raw = max / Math.Max(1, maxTicks);
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
            if (integer) step = Math.Max(1, Math.Ceiling(step));
            return (float)step;
        }

        private static float NiceCeiling(float max)
        {
            float step = NiceStep(max, 5, integer: false);
            return (float)Math.Ceiling(max / step - 1e-6) * step;
        }

        private static string FormatClock(float seconds)
        {
            int total = (int)Math.Round(seconds);
            int h = total / 3600, m = total % 3600 / 60, s = total % 60;
            return h > 0
                ? $"{h}:{m:00}:{s:00}"
                : $"{m}:{s:00}";
        }

        private static string FormatDurationLong(float seconds)
        {
            int total = (int)Math.Round(seconds);
            int m = total / 60, s = total % 60;
            return m > 0 ? $"{m} хв {s:00} с" : $"{s} с";
        }

        private static string FormatBin(float seconds)
        {
            return seconds >= 60 && seconds % 60 == 0
                ? $"{seconds / 60:0} хв"
                : $"{seconds:0} с";
        }

        private static string FormatSessionStart(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "—";
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)
                ? t.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                : iso;
        }

        // Ukrainian plural forms: 1 удар, 2 удари, 5 ударів.
        private static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n);
            int mod10 = n % 10, mod100 = n % 100;
            if (mod10 == 1 && mod100 != 11) return one;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
            return many;
        }
    }
}
