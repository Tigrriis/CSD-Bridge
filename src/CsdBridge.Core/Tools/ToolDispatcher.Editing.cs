using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;

namespace CsdBridge.Core.Tools
{
    /// <summary>
    /// Hatching and exact curve editing: trim/extend, fillet, join, and road lines from an
    /// alignment by chainage. The geometry is computed here on exact lines and arcs
    /// (<see cref="PathOps"/>, <see cref="RoadGeometry"/>); the host only reads curves and
    /// creates or erases entities, so both the BricsCAD and the offline host behave the same.
    /// </summary>
    public sealed partial class ToolDispatcher
    {
        private ToolOutcome? ExecuteEditing(string toolName, JsonElement input)
        {
            switch (toolName)
            {
                case ToolCatalog.Hatch: return DoHatch(input);
                case ToolCatalog.TrimExtend: return DoTrimExtend(input);
                case ToolCatalog.Fillet: return DoFillet(input);
                case ToolCatalog.JoinCurves: return DoJoin(input);
                case ToolCatalog.OffsetByChainage: return DoOffsetByChainage(input);
                case ToolCatalog.AnalyseOffsets: return DoAnalyseOffsets(input);
                default: return null;
            }
        }

        // ------------------------------------------------------------------ hatch

        private ToolOutcome DoHatch(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement> items = JsonArgs.RequireArray(input, "hatches", "", 1, 100);
            var specs = new List<EntitySpec>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("hatches", i);
                JsonElement e = items[i];
                JsonArgs.RequireObject(e, path);
                var h = new HatchSpec
                {
                    Pattern = (JsonArgs.OptionalString(e, "pattern", path) ?? "SOLID").Trim(),
                    Scale = JsonArgs.OptionalPositive(e, "scale", path) ?? 1,
                    AngleDeg = JsonArgs.OptionalNumber(e, "angle", path) ?? 0,
                    Associative = JsonArgs.OptionalBool(e, "associative", path, false),
                    Layer = JsonArgs.OptionalString(e, "layer", path),
                    ColorIndex = JsonArgs.OptionalColor(e, "color", path),
                };

                int ways = 0;
                List<JsonElement>? handles = JsonArgs.OptionalArray(e, "boundary_handles", path, 1, 100);
                if (handles != null)
                {
                    h.BoundaryHandles = JsonArgs.ReadStringList(handles, JsonArgs.Path(path, "boundary_handles"));
                    ways++;
                }

                List<JsonElement>? seeds = JsonArgs.OptionalArray(e, "seed_points", path, 1, 100);
                if (seeds != null)
                {
                    h.SeedPoints = seeds.Select((s, k) => JsonArgs.ReadPoint(s, JsonArgs.Index(JsonArgs.Path(path, "seed_points"), k))).ToList();
                    ways++;
                }

                List<JsonElement>? points = JsonArgs.OptionalArray(e, "points", path, 3, 5000);
                if (points != null)
                {
                    var loop = new PolylineSpec
                    {
                        Points = points.Select((p, k) => JsonArgs.ReadPoint(p, JsonArgs.Index(JsonArgs.Path(path, "points"), k))).ToList(),
                        Closed = true,
                    };
                    List<JsonElement>? bulges = JsonArgs.OptionalArray(e, "bulges", path, 0, 5000);
                    if (bulges != null)
                    {
                        if (bulges.Count != loop.Points.Count)
                        {
                            throw new ToolInputException($"{path}.bulges needs one value per point ({loop.Points.Count})");
                        }

                        loop.Bulges = bulges.Select((b, k) => JsonArgs.ReadNumber(b, JsonArgs.Index(JsonArgs.Path(path, "bulges"), k))).ToList();
                    }

                    h.Loops.Add(loop);
                    ways++;
                }

                if (ways != 1)
                {
                    throw new ToolInputException($"{path} needs exactly one of boundary_handles, seed_points or points");
                }

                if (h.Associative && h.BoundaryHandles.Count == 0)
                {
                    throw new ToolInputException($"{path}: associative hatches need boundary_handles");
                }

                specs.Add(h);
            }

            IReadOnlyList<ItemResult> results = _host.CreateEntities(specs);
            return Items(results, $"hatched {specs.Count} area(s)");
        }

        // ------------------------------------------------------------------ trim / extend

        private ToolOutcome DoTrimExtend(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement> ops = JsonArgs.RequireArray(input, "operations", "", 1, 100);
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<ItemResult>();
            for (int i = 0; i < ops.Count; i++)
            {
                string path = JsonArgs.Index("operations", i);
                try
                {
                    JsonElement e = ops[i];
                    JsonArgs.RequireObject(e, path);
                    string handle = Current(renamed, JsonArgs.RequireString(e, "handle", path));
                    string mode = JsonArgs.RequireString(e, "mode", path).ToLowerInvariant();
                    List<string> boundaries = JsonArgs.ReadStringList(JsonArgs.RequireArray(e, "boundaries", path, 1, 50), JsonArgs.Path(path, "boundaries"))
                        .Select(b => Current(renamed, b)).ToList();

                    var curves = Curves(new[] { handle }.Concat(boundaries));
                    (EntitySpec spec, Path2 p) = curves[handle];
                    List<Path2> bounds = boundaries.Select(b => curves[b].Path).ToList();

                    Path2 result;
                    if (mode == "trim")
                    {
                        Point2 keep = JsonArgs.OptionalPoint(e, "keep_point", path)
                            ?? throw new ToolInputException($"{path}.keep_point is required for trim (a point on the part to keep)");
                        result = PathOps.Trim(p, bounds, keep);
                    }
                    else if (mode == "extend")
                    {
                        string end = (JsonArgs.OptionalString(e, "end", path) ?? "end").ToLowerInvariant();
                        result = p;
                        if (end == "start" || end == "both")
                        {
                            result = PathOps.Extend(result, atEnd: false, bounds);
                        }

                        if (end == "end" || end == "both")
                        {
                            result = PathOps.Extend(result, atEnd: true, bounds);
                        }
                    }
                    else
                    {
                        throw new ToolInputException($"{path}.mode must be trim or extend");
                    }

                    string newHandle = Replace(handle, spec, result);
                    renamed[handle] = newHandle;
                    results.Add(ItemResult.Success(i, newHandle, $"{(mode == "trim" ? "trimmed" : "extended")}; length {JsonArgs.Fmt(result.Length)}"));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is ToolInputException || ex is KeyNotFoundException)
                {
                    results.Add(ItemResult.Failure(i, ex.Message));
                }
            }

            return Items(results, $"trim/extend x{ops.Count}");
        }

        // ------------------------------------------------------------------ fillet

        private ToolOutcome DoFillet(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement> items = JsonArgs.RequireArray(input, "fillets", "", 1, 100);
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<object>();
            int failed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("fillets", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    string a = Current(renamed, JsonArgs.RequireString(e, "a", path));
                    string b = Current(renamed, JsonArgs.RequireString(e, "b", path));
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ToolInputException($"{path}: a and b must be different curves");
                    }

                    double radius = JsonArgs.RequirePositive(e, "radius", path);
                    Point2 pickA = JsonArgs.RequirePoint(e, "pick_a", path);
                    Point2 pickB = JsonArgs.RequirePoint(e, "pick_b", path);
                    bool join = JsonArgs.OptionalBool(e, "join", path, false);
                    string? arcLayer = JsonArgs.OptionalString(e, "layer", path);

                    var curves = Curves(new[] { a, b });
                    FilletResult f = PathOps.Fillet(curves[a].Path, pickA, curves[b].Path, pickB, radius);
                    EntitySpec specA = curves[a].Spec, specB = curves[b].Spec;

                    if (join)
                    {
                        Path2 joined = PathOps.Join(new[] { f.A, new Path2(new List<Segment> { f.Arc }, false), f.B }, 1e-6);
                        string h = Replace(a, specA, joined);
                        Erase(b);
                        renamed[a] = h;
                        renamed[b] = h;
                        results.Add(new { index = i, ok = true, joined = h });
                    }
                    else
                    {
                        string ha = Replace(a, specA, f.A);
                        string hb = Replace(b, specB, f.B);
                        EntitySpec arc = PathOps.ToSpec(new Path2(new List<Segment> { f.Arc }, false));
                        arc.Layer = arcLayer ?? specA.Layer;
                        arc.ColorIndex = arcLayer == null ? specA.ColorIndex : null;
                        string harc = Create(arc);
                        renamed[a] = ha;
                        renamed[b] = hb;
                        results.Add(new { index = i, ok = true, a = ha, b = hb, arc = harc, arc_length = Math.Round(f.Arc.Length, 4) });
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is ToolInputException || ex is KeyNotFoundException)
                {
                    failed++;
                    results.Add(new { index = i, ok = false, error = ex.Message });
                }
            }

            string json = JsonSerializer.Serialize(new { succeeded = items.Count - failed, failed, results }, JsonOptions);
            return new ToolOutcome(json, failed == items.Count) { Summary = $"filleted {items.Count - failed} of {items.Count}" };
        }

        // ------------------------------------------------------------------ join

        private ToolOutcome DoJoin(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            List<JsonElement> items = JsonArgs.RequireArray(input, "joins", "", 1, 50);
            var results = new List<ItemResult>();
            for (int i = 0; i < items.Count; i++)
            {
                string path = JsonArgs.Index("joins", i);
                try
                {
                    JsonElement e = items[i];
                    JsonArgs.RequireObject(e, path);
                    List<string> handles = JsonArgs.ReadStringList(JsonArgs.RequireArray(e, "handles", path, 2, 500), JsonArgs.Path(path, "handles"));
                    double tol = JsonArgs.OptionalPositive(e, "tolerance", path) ?? 0.001;
                    string? layer = JsonArgs.OptionalString(e, "layer", path);
                    bool keep = JsonArgs.OptionalBool(e, "keep_originals", path, false);

                    var curves = Curves(handles);
                    Path2 joined = PathOps.Join(handles.Select(h => curves[h].Path).ToList(), tol);
                    EntitySpec spec = PathOps.ToPolyline(joined);
                    EntitySpec first = curves[handles[0]].Spec;
                    spec.Layer = layer ?? first.Layer;
                    spec.ColorIndex = layer == null ? first.ColorIndex : null;
                    string h = Create(spec);
                    if (!keep)
                    {
                        foreach (string old in handles)
                        {
                            Erase(old);
                        }
                    }

                    results.Add(ItemResult.Success(i, h, joined.Closed ? $"closed outline, area {JsonArgs.Fmt(joined.Area)}" : $"open, length {JsonArgs.Fmt(joined.Length)}"));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is ToolInputException || ex is KeyNotFoundException)
                {
                    results.Add(ItemResult.Failure(i, ex.Message));
                }
            }

            return Items(results, $"joined {items.Count} set(s)");
        }

        // ------------------------------------------------------------------ road lines

        private ToolOutcome DoOffsetByChainage(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            string alignmentHandle = JsonArgs.RequireString(input, "alignment", "");
            double start = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0;
            Path2 alignment = Curves(new[] { alignmentHandle })[alignmentHandle].Path;
            List<JsonElement> lines = JsonArgs.RequireArray(input, "lines", "", 1, 50);

            var results = new List<ItemResult>();
            for (int i = 0; i < lines.Count; i++)
            {
                string path = JsonArgs.Index("lines", i);
                try
                {
                    JsonElement e = lines[i];
                    JsonArgs.RequireObject(e, path);
                    List<JsonElement> raw = JsonArgs.RequireArray(e, "stretches", path, 1, 200);
                    var stretches = new List<OffsetStretch>();
                    for (int k = 0; k < raw.Count; k++)
                    {
                        string sp = JsonArgs.Index(JsonArgs.Path(path, "stretches"), k);
                        double? offset = JsonArgs.OptionalNumber(raw[k], "offset", sp);
                        double? os = JsonArgs.OptionalNumber(raw[k], "offset_start", sp);
                        double? oe = JsonArgs.OptionalNumber(raw[k], "offset_end", sp);
                        if (offset == null && (os == null || oe == null))
                        {
                            throw new ToolInputException($"{sp} needs offset, or offset_start and offset_end");
                        }

                        stretches.Add(new OffsetStretch
                        {
                            From = JsonArgs.RequireNumber(raw[k], "from", sp),
                            To = JsonArgs.RequireNumber(raw[k], "to", sp),
                            OffsetStart = offset ?? os!.Value,
                            OffsetEnd = offset ?? oe!.Value,
                        });
                    }

                    Path2 line = RoadGeometry.OffsetByChainage(alignment, start, stretches);
                    EntitySpec spec = PathOps.ToSpec(line);
                    spec.Layer = JsonArgs.OptionalString(e, "layer", path);
                    spec.ColorIndex = JsonArgs.OptionalColor(e, "color", path);
                    results.Add(ItemResult.Success(i, Create(spec), $"length {JsonArgs.Fmt(line.Length)}"));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is ToolInputException)
                {
                    results.Add(ItemResult.Failure(i, ex.Message));
                }
            }

            return Items(results, $"drew {lines.Count} road line(s)");
        }

        private ToolOutcome DoAnalyseOffsets(JsonElement input)
        {
            JsonArgs.RequireObject(input, "");
            string alignmentHandle = JsonArgs.RequireString(input, "alignment", "");
            double start = JsonArgs.OptionalNumber(input, "start_chainage", "") ?? 0;
            double tol = JsonArgs.OptionalPositive(input, "tolerance", "") ?? 0.01;
            double interval = JsonArgs.OptionalPositive(input, "arc_interval", "") ?? 1;
            List<string> handles = JsonArgs.ReadStringList(JsonArgs.RequireArray(input, "lines", "", 1, 50), "lines");

            var curves = Curves(new[] { alignmentHandle }.Concat(handles));
            Path2 alignment = curves[alignmentHandle].Path;
            var lines = handles.Select(h =>
            {
                List<OffsetStretch> s = RoadGeometry.AnalyseOffsets(alignment, curves[h].Path, start, interval, tol);
                return new
                {
                    handle = h,
                    layer = curves[h].Spec.Layer,
                    stretches = s.Select(x => x.IsConstant
                        ? (object)new { from = R(x.From), to = R(x.To), offset = R(x.OffsetStart), max_dev = R(x.MaxDeviation ?? 0) }
                        : new { from = R(x.From), to = R(x.To), offset_start = R(x.OffsetStart), offset_end = R(x.OffsetEnd), max_dev = R(x.MaxDeviation ?? 0) }).ToList(),
                    note = s.Count == 0 ? "No part of this line lies alongside the alignment." : null,
                };
            }).ToList();

            return Ok(new { offsets = "positive to the right of the alignment", lines }, $"analysed {handles.Count} line(s)");

            static double R(double v) => Math.Round(v, 4);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Curves by handle, as specs and exact paths. Throws naming any that aren't curves.</summary>
        private Dictionary<string, (EntitySpec Spec, Path2 Path)> Curves(IEnumerable<string> handles)
        {
            List<string> list = handles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            IReadOnlyDictionary<string, EntitySpec> specs = _host.GetCurves(list);
            var result = new Dictionary<string, (EntitySpec, Path2)>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (string h in list)
            {
                EntitySpec? spec = specs.FirstOrDefault(kv => string.Equals(kv.Key, h, StringComparison.OrdinalIgnoreCase)).Value;
                Path2? p = spec == null ? null : Path2.From(spec);
                if (spec == null || p == null)
                {
                    missing.Add(h);
                }
                else
                {
                    result[h] = (spec, p);
                }
            }

            if (missing.Count > 0)
            {
                throw new ToolInputException($"Not a line, arc, circle or polyline (or not found): {string.Join(", ", missing)}");
            }

            return result;
        }

        private static string Current(Dictionary<string, string> renamed, string handle)
        {
            string h = handle;
            for (int guard = 0; guard < 100 && renamed.TryGetValue(h, out string? next); guard++)
            {
                h = next;
            }

            return h;
        }

        private string Create(EntitySpec spec)
        {
            ItemResult r = _host.CreateEntities(new[] { spec })[0];
            if (!r.Ok || r.Handle == null)
            {
                throw new ArgumentException("Couldn't create the result: " + r.Error);
            }

            return r.Handle;
        }

        private void Erase(string handle)
        {
            ItemResult r = _host.ModifyEntities(new[] { new ModifyOperation { Action = ModifyAction.Erase, Handles = new List<string> { handle } } })[0];
            if (!r.Ok)
            {
                throw new ArgumentException($"Couldn't erase {handle}: {r.Error}");
            }
        }

        /// <summary>Draw the result with the old entity's layer and colour, then erase the old one.</summary>
        private string Replace(string oldHandle, EntitySpec old, Path2 path)
        {
            EntitySpec spec = PathOps.ToSpec(path);
            spec.Layer = old.Layer;
            spec.ColorIndex = old.ColorIndex;
            string h = Create(spec);
            Erase(oldHandle);
            return h;
        }
    }
}
