using System;
using System.Collections.Generic;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Civil
{
    public sealed class DraftRoadRequest
    {
        /// <summary>The road name as used in CSD's string layers (e.g. "CL-01").</summary>
        public string Road { get; set; } = "";

        /// <summary>Handles of the alignment (one polyline, or lines/arcs to join). Empty: found automatically.</summary>
        public List<string> Alignment { get; set; } = new List<string>();

        /// <summary>Chainage at the start of the alignment.</summary>
        public double StartChainage { get; set; }

        public double? From { get; set; }
        public double? To { get; set; }

        /// <summary>Only draw these (centreline, line and area names). Empty: everything.</summary>
        public List<string> Only { get; set; } = new List<string>();

        /// <summary>Analyse and report, but draw nothing.</summary>
        public bool DryRun { get; set; }
    }

    /// <summary>
    /// Drafts a road to the office conventions from CSD's output. The geometry comes from the
    /// alignment (exact lines and arcs); CSD's code strings only supply the offsets, fitted
    /// as constant stretches and straight transitions, so the chords between CSD's sample
    /// sections never reach the drawing. Areas between two codes become one closed outline
    /// each, hatched as the conventions say.
    /// </summary>
    public sealed class RoadDrafter
    {
        private readonly IDrawingHost _host;
        private readonly RoadConventions _c;
        private readonly Dictionary<string, List<Path2>?> _strings = new Dictionary<string, List<Path2>?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _stringLayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public RoadDrafter(IDrawingHost host, RoadConventions conventions)
        {
            _host = host;
            _c = conventions;
        }

        public sealed class Report
        {
            public string Road { get; set; } = "";
            public List<string> AlignmentFrom { get; set; } = new List<string>();
            public double ChainageFrom { get; set; }
            public double ChainageTo { get; set; }
            public Dictionary<string, object> Codes { get; set; } = new Dictionary<string, object>();
            public List<object> Drawn { get; set; } = new List<object>();
            public List<string> Skipped { get; set; } = new List<string>();
            public List<string> Notes { get; set; } = new List<string>();
        }

        public Report Draft(DraftRoadRequest r)
        {
            var report = new Report { Road = r.Road };
            CurrentRoad = r.Road;
            _strings.Clear();
            _stringLayer.Clear();
            Path2 alignment = FindAlignment(r, report);
            double start = r.StartChainage;
            double from = Math.Max(start, r.From ?? start);
            double to = Math.Min(start + alignment.Length, r.To ?? start + alignment.Length);
            if (to - from < 0.01)
            {
                throw new ArgumentException($"Nothing to draft between ch {from:0.###} and {to:0.###}.");
            }

            report.ChainageFrom = Math.Round(from, 3);
            report.ChainageTo = Math.Round(to, 3);
            bool Wanted(string name) => r.Only.Count == 0 || r.Only.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase));

            var toCreate = new List<(string What, EntitySpec Spec, double From, double To)>();

            if (_c.Centreline != null && Wanted("centreline"))
            {
                EntitySpec cl = PathOps.ToSpec(PathOps.Sub(alignment, from - start, to - start));
                cl.Layer = _c.Centreline.Layer;
                cl.LinetypeScale = _c.Centreline.LinetypeScale;
                toCreate.Add(("centreline", cl, from, to));
            }

            foreach (RoadLine line in _c.Lines.Where(l => Wanted(l.Name)))
            {
                foreach (string code in line.Codes)
                {
                    List<List<OffsetStretch>>? groups = Offsets(alignment, start, code, from, to, report);
                    if (groups == null)
                    {
                        continue;
                    }

                    foreach (List<OffsetStretch> g in groups)
                    {
                        EntitySpec spec = PathOps.ToSpec(RoadGeometry.OffsetByChainage(alignment, start, g));
                        spec.Layer = line.Layer;
                        spec.LinetypeScale = line.LinetypeScale;
                        toCreate.Add(($"{line.Name} ({code})", spec, g[0].From, g[g.Count - 1].To));
                    }
                }
            }

            foreach (RoadArea area in _c.Areas.Where(a => Wanted(a.Name)))
            {
                if (area.Between.Count != 2)
                {
                    report.Notes.Add($"Area '{area.Name}' needs exactly two codes in 'between'.");
                    continue;
                }

                string? blocking = area.UnlessCodes?.FirstOrDefault(code => Strings(code) != null);
                if (blocking != null)
                {
                    report.Skipped.Add($"{area.Name}: this road has {blocking}");
                    continue;
                }

                List<List<OffsetStretch>>? a = Offsets(alignment, start, area.Between[0], from, to, report);
                List<List<OffsetStretch>>? b = Offsets(alignment, start, area.Between[1], from, to, report);
                if (a == null || b == null)
                {
                    report.Skipped.Add($"{area.Name}: no {(a == null ? area.Between[0] : area.Between[1])} strings");
                    continue;
                }

                foreach (List<OffsetStretch> ga in a)
                {
                    foreach (List<OffsetStretch> gb in b)
                    {
                        double f = Math.Max(ga[0].From, gb[0].From), t = Math.Min(ga[ga.Count - 1].To, gb[gb.Count - 1].To);
                        if (t - f < 0.05)
                        {
                            continue;
                        }

                        Path2 side1 = RoadGeometry.OffsetByChainage(alignment, start, Clip(ga, f, t));
                        Path2 side2 = RoadGeometry.OffsetByChainage(alignment, start, Clip(gb, f, t));
                        var segs = new List<Segment>(side1.Segments) { new LineSegment(side1.End, side2.End) };
                        segs.AddRange(PathOps.Reverse(side2).Segments);
                        segs.Add(new LineSegment(side2.Start, side1.Start));
                        var outline = new Path2(segs.Where(s => s.Length > 1e-6).ToList(), true);

                        if (area.OutlineLayer != null)
                        {
                            PolylineSpec pl = PathOps.ToPolyline(outline);
                            pl.Layer = area.OutlineLayer;
                            pl.LinetypeScale = area.LinetypeScale;
                            toCreate.Add(($"{area.Name} outline", pl, f, t));
                        }

                        if (area.Hatch != null)
                        {
                            var h = new HatchSpec
                            {
                                Loops = new List<PolylineSpec> { PathOps.ToPolyline(outline) },
                                Pattern = area.Hatch.Pattern,
                                Scale = area.Hatch.Scale,
                                AngleDeg = area.Hatch.Angle,
                                Layer = area.Hatch.Layer,
                            };
                            toCreate.Add(($"{area.Name} hatch", h, f, t));
                        }
                    }
                }
            }

            if (r.DryRun)
            {
                report.Notes.Add($"Dry run: would draw {toCreate.Count} object(s); nothing was drawn.");
                foreach (var item in toCreate)
                {
                    report.Drawn.Add(new { what = item.What, layer = item.Spec.Layer, from = Math.Round(item.From, 3), to = Math.Round(item.To, 3) });
                }

                return report;
            }

            EnsureLayers(toCreate.Select(x => x.Spec.Layer).Where(l => l != null).Select(l => l!).Distinct().ToList(), report);
            IReadOnlyList<ItemResult> results = _host.CreateEntities(toCreate.Select(x => x.Spec).ToList());
            for (int i = 0; i < results.Count; i++)
            {
                var item = toCreate[i];
                report.Drawn.Add(results[i].Ok
                    ? (object)new { what = item.What, handle = results[i].Handle, layer = item.Spec.Layer, from = Math.Round(item.From, 3), to = Math.Round(item.To, 3) }
                    : new { what = item.What, error = results[i].Error });
            }

            return report;
        }

        // ------------------------------------------------------------------ alignment

        private Path2 FindAlignment(DraftRoadRequest r, Report report)
        {
            List<Path2>? centre = Strings(_c.CentrelineCode);
            Path2 alignment;
            if (r.Alignment.Count > 0)
            {
                IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(r.Alignment);
                List<Path2> pieces = r.Alignment.Select(h => curves.TryGetValue(h, out EntitySpec? s) ? Path2.From(s) : null)
                    .Where(p => p != null).Select(p => p!).ToList();
                if (pieces.Count != r.Alignment.Count)
                {
                    throw new ArgumentException("Some alignment handles aren't lines, arcs or polylines.");
                }

                alignment = PathOps.Join(pieces, 0.002);
                report.AlignmentFrom.AddRange(r.Alignment);
            }
            else
            {
                if (centre == null)
                {
                    throw new ArgumentException($"No {_c.CentrelineCode} string found for road '{r.Road}' on {string.Join(" or ", _c.StringLayers.Select(p => Fill(p, r.Road, _c.CentrelineCode)))}; check the road name, or give the alignment handle.");
                }

                alignment = FromAlignmentLayers(centre, report) ?? FitToCentreline(centre, report);
            }

            // Run the same way as CSD's chainage (the centreline string's direction).
            if (centre != null)
            {
                Path2 first = centre.OrderBy(c => alignment.Closest(c.Start).Along).First();
                double a = alignment.Closest(first.Start).Along, b = alignment.Closest(first.End).Along;
                if (b < a)
                {
                    alignment = PathOps.Reverse(alignment);
                }
            }

            return alignment;
        }

        /// <summary>
        /// CSD's alignment lines and arcs that lie on the centreline string, if they join into one
        /// path covering the whole string. Those layers can hold leftovers of other alignments or
        /// earlier versions, so anything less is not trusted.
        /// </summary>
        private Path2? FromAlignmentLayers(List<Path2> centre, Report report)
        {
            var pieces = new List<(string Handle, Path2 Path)>();
            foreach (string layer in _c.AlignmentLayers)
            {
                EntityListResult list = _host.ListEntities(new EntityQuery { Layer = layer, Limit = 5000 });
                IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(list.Entities.Select(e => e.Handle).ToList());
                foreach (var kv in curves)
                {
                    Path2? p = Path2.From(kv.Value);
                    if (p != null && OnStrings(p, centre))
                    {
                        pieces.Add((kv.Key, p));
                    }
                }
            }

            if (pieces.Count == 0)
            {
                return null;
            }

            Path2 joined;
            try
            {
                joined = PathOps.Join(pieces.Select(x => x.Path).ToList(), 0.002);
            }
            catch (ArgumentException)
            {
                report.Notes.Add($"The lines/arcs on {string.Join(", ", _c.AlignmentLayers)} don't form one continuous alignment for this road, so it was fitted to the centreline string instead.");
                return null;
            }

            List<Point2> vertices = centre.SelectMany(Vertices).ToList();
            if (vertices.Any(v => joined.Closest(v).Distance > 0.01))
            {
                report.Notes.Add($"The lines/arcs on {string.Join(", ", _c.AlignmentLayers)} don't cover the whole centreline string, so the alignment was fitted to the string instead.");
                return null;
            }

            report.AlignmentFrom.AddRange(pieces.Select(x => x.Handle));
            return joined;
        }

        /// <summary>Rebuild the exact straights and arcs from the centreline string's vertices (which lie on the alignment).</summary>
        private Path2 FitToCentreline(List<Path2> centre, Report report)
        {
            Path2 longest = centre.OrderByDescending(c => c.Length).First();
            Path2 source = longest;
            if (centre.Count > 1)
            {
                try
                {
                    source = PathOps.Join(centre, 0.01);
                }
                catch (ArgumentException)
                {
                    report.Notes.Add("The centreline string is in several pieces that don't join end to end; the longest piece was used.");
                }
            }

            AlignmentFit.Result fit = AlignmentFit.Fit(Vertices(source).ToList());
            report.AlignmentFrom.Add($"fitted to the {_c.CentrelineCode} string: {fit.Lines} straight(s), {fit.Arcs} arc(s), largest vertex offset {fit.MaxDeviation:0.000} m");
            if (fit.MaxDeviation > 0.02)
            {
                report.Notes.Add($"The fitted alignment is up to {fit.MaxDeviation:0.000} m from the centreline string (transition spirals?). Check it, or give the alignment handle.");
            }

            return fit.Path;
        }

        private static IEnumerable<Point2> Vertices(Path2 p) => p.Segments.Select(s => s.Start).Concat(new[] { p.End });

        private static bool OnStrings(Path2 piece, List<Path2> strings)
        {
            foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                Point2 p = piece.PointAt(piece.Length * t);
                if (strings.Min(s => s.Closest(p).Distance) > 0.02)
                {
                    return false;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ strings and offsets

        private List<Path2>? Strings(string code)
        {
            if (_strings.TryGetValue(code, out List<Path2>? cached))
            {
                return cached;
            }

            List<Path2>? found = null;
            foreach (string pattern in _c.StringLayers)
            {
                string layer = Fill(pattern, CurrentRoad, code);
                EntityListResult list = _host.ListEntities(new EntityQuery { Layer = layer, Limit = 2000 });
                if (list.Entities.Count == 0)
                {
                    continue;
                }

                IReadOnlyDictionary<string, EntitySpec> curves = _host.GetCurves(list.Entities.Select(e => e.Handle).ToList());
                // Drop exact duplicates (CSD or an export can leave the same string twice).
                var paths = new List<Path2>();
                foreach (Path2 p in curves.Values.Select(Path2.From).Where(p => p != null && p.Length > 1e-6).Select(p => p!))
                {
                    bool duplicate = paths.Any(q => Math.Abs(q.Length - p.Length) < 0.01
                        && ((Segment.Dist(q.Start, p.Start) < 0.001 && Segment.Dist(q.End, p.End) < 0.001)
                            || (Segment.Dist(q.Start, p.End) < 0.001 && Segment.Dist(q.End, p.Start) < 0.001)));
                    if (!duplicate)
                    {
                        paths.Add(p);
                    }
                }

                if (paths.Count > 0)
                {
                    found = paths;
                    _stringLayer[code] = layer;
                    break;
                }
            }

            _strings[code] = found;
            return found;
        }

        private string CurrentRoad { get; set; } = "";

        /// <summary>Contiguous groups of offset stretches for a code within [from, to], or null if the road has no such strings.</summary>
        private List<List<OffsetStretch>>? Offsets(Path2 alignment, double start, string code, double from, double to, Report report)
        {
            List<Path2>? strings = Strings(code);
            if (strings == null)
            {
                return null;
            }

            var all = new List<OffsetStretch>();
            foreach (Path2 s in strings)
            {
                all.AddRange(RoadGeometry.AnalyseOffsets(alignment, s, start, 1, _c.OffsetTolerance));
            }

            all = all.OrderBy(s => s.From).ToList();
            double q = _c.OffsetRounding > 0 ? _c.OffsetRounding : 0.001;
            foreach (OffsetStretch s in all)
            {
                s.OffsetStart = Math.Round(s.OffsetStart / q) * q;
                s.OffsetEnd = Math.Round(s.OffsetEnd / q) * q;
            }

            // Group into contiguous runs; drop overlaps between separate polylines.
            var groups = new List<List<OffsetStretch>>();
            foreach (OffsetStretch s in all)
            {
                List<OffsetStretch>? g = groups.Count > 0 ? groups[groups.Count - 1] : null;
                OffsetStretch? last = g?[g.Count - 1];
                if (last != null && s.To <= last.To + 1e-6)
                {
                    continue; // inside the previous stretch
                }

                if (last != null && s.From < last.To)
                {
                    s.OffsetStart = Interp(s, last.To);
                    s.From = last.To;
                }

                if (g == null || s.From - last!.To > _c.GapTolerance)
                {
                    groups.Add(new List<OffsetStretch> { s });
                }
                else
                {
                    g.Add(s);
                }
            }

            var clipped = groups.Select(g => Clip(g, from, to)).Where(g => g.Count > 0).ToList();
            report.Codes[code] = new
            {
                layer = _stringLayer.TryGetValue(code, out string? l) ? l : null,
                stretches = clipped.SelectMany(g => g).Select(s => s.IsConstant
                    ? (object)new { from = Math.Round(s.From, 3), to = Math.Round(s.To, 3), offset = Math.Round(s.OffsetStart, 3) }
                    : new { from = Math.Round(s.From, 3), to = Math.Round(s.To, 3), offset_start = Math.Round(s.OffsetStart, 3), offset_end = Math.Round(s.OffsetEnd, 3) }).ToList(),
            };
            return clipped.Count > 0 ? clipped : null;
        }

        /// <summary>The part of a run of stretches between two chainages, with offsets interpolated at the cuts.</summary>
        internal static List<OffsetStretch> Clip(List<OffsetStretch> g, double from, double to)
        {
            var result = new List<OffsetStretch>();
            foreach (OffsetStretch s in g)
            {
                double f = Math.Max(s.From, from), t = Math.Min(s.To, to);
                if (t - f < 1e-6)
                {
                    continue;
                }

                result.Add(new OffsetStretch { From = f, To = t, OffsetStart = Interp(s, f), OffsetEnd = Interp(s, t), MaxDeviation = s.MaxDeviation });
            }

            return result;
        }

        private static double Interp(OffsetStretch s, double ch) =>
            s.To - s.From < 1e-12 ? s.OffsetStart : s.OffsetStart + (ch - s.From) / (s.To - s.From) * (s.OffsetEnd - s.OffsetStart);

        private static string Fill(string pattern, string road, string code) => pattern.Replace("{road}", road).Replace("{code}", code);

        // ------------------------------------------------------------------ layers

        /// <summary>Create the layers that don't exist yet, styled from the conventions. Existing layers are left alone.</summary>
        private void EnsureLayers(List<string> layers, Report report)
        {
            var existing = new HashSet<string>(_host.GetDrawingInfo().Layers.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
            var missing = layers.Where(l => !existing.Contains(l)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            var specs = missing.Select(name =>
            {
                var spec = new LayerSpec { Name = name };
                if (_c.Layers.TryGetValue(name, out LayerStyle? st))
                {
                    spec.ColorIndex = st.Color;
                    spec.Rgb = st.Rgb != null && st.Rgb.Length == 3 ? st.Rgb.Select(v => (byte)Math.Max(0, Math.Min(255, v))).ToArray() : null;
                    spec.Linetype = st.Linetype;
                    spec.LineweightMm = st.Lineweight;
                    spec.Plot = st.Plot;
                    spec.Description = st.Description;
                }

                return spec;
            }).ToList();
            _host.EnsureLayers(specs);
            report.Notes.Add("Created layers: " + string.Join(", ", missing));
        }
    }
}
