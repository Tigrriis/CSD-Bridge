using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;

// Porting note: this file uses the AutoCAD-compatible API. To build it for AutoCAD,
// swap Bricscad.* / Teigha.* for Autodesk.AutoCAD.* namespaces.

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Executes the drafting tools against a BricsCAD document. Every method runs on
    /// BricsCAD's main thread, inside the AIDRAFT command (see <see cref="AgentRunner"/>),
    /// so the whole AI request is one UNDO step.
    /// </summary>
    internal sealed class BricsCadDrawingHost : IDrawingHost
    {
        private const int MaxListedNames = 200;
        private const int MaxPolylinePoints = 500;

        private static readonly int[] ValidLineweights =
            { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

        private readonly Document _doc;

        public BricsCadDrawingHost(Document doc)
        {
            _doc = doc;
        }

        private Database Db => _doc.Database;

        private Editor Ed => _doc.Editor;

        // ================================================================ read

        public DrawingInfo GetDrawingInfo()
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                var info = new DrawingInfo
                {
                    FileName = Path.GetFileName(_doc.Name),
                    Units = Db.Insunits.ToString(),
                    CurrentSpace = SpaceName(tr),
                    CurrentLayer = ((LayerTableRecord)tr.GetObject(Db.Clayer, OpenMode.ForRead)).Name,
                    CurrentTextStyle = ((TextStyleTableRecord)tr.GetObject(Db.Textstyle, OpenMode.ForRead)).Name,
                    CurrentDimStyle = ((DimStyleTableRecord)tr.GetObject(Db.Dimstyle, OpenMode.ForRead)).Name,
                    DefaultTextHeight = Db.Textsize,
                };

                if (Db.Extmin.X <= Db.Extmax.X && Db.Extmin.Y <= Db.Extmax.Y)
                {
                    info.ExtentsMin = Arr(Db.Extmin);
                    info.ExtentsMax = Arr(Db.Extmax);
                }

                var space = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId _ in space)
                {
                    info.EntityCount++;
                }

                var layers = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in layers)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (info.Layers.Count >= MaxListedNames)
                    {
                        break;
                    }

                    info.Layers.Add(new LayerInfo
                    {
                        Name = ltr.Name,
                        Color = ltr.Color.ColorIndex,
                        Linetype = ((LinetypeTableRecord)tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead)).Name,
                        LineweightMm = (int)ltr.LineWeight >= 0 ? (int)ltr.LineWeight / 100.0 : (double?)null,
                        Off = ltr.IsOff ? true : (bool?)null,
                        Frozen = ltr.IsFrozen ? true : (bool?)null,
                        Locked = ltr.IsLocked ? true : (bool?)null,
                    });
                }

                info.Linetypes = Names<LinetypeTableRecord>(tr, Db.LinetypeTableId);
                info.TextStyles = Names<TextStyleTableRecord>(tr, Db.TextStyleTableId);
                info.DimStyles = Names<DimStyleTableRecord>(tr, Db.DimStyleTableId);

                var blocks = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in blocks)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsLayout && !btr.IsAnonymous && !btr.IsFromExternalReference && info.Blocks.Count < MaxListedNames)
                    {
                        info.Blocks.Add(btr.Name);
                    }
                }

                tr.Commit();
                return info;
            }
        }

        public EntityListResult ListEntities(EntityQuery query)
        {
            var result = new EntityListResult();
            HashSet<string>? handles = query.Handles == null
                ? null
                : new HashSet<string>(query.Handles, StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null)
                    {
                        continue;
                    }

                    if (query.Layer != null && !string.Equals(ent.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (handles != null && !handles.Contains(ent.Handle.ToString()))
                    {
                        continue;
                    }

                    if (query.HasKindFilter)
                    {
                        EntityKind? kind = KindOf(ent);
                        bool match = kind == null
                            ? query.IncludeOtherKinds
                            : query.Kinds != null && (query.Kinds.Contains(kind.Value)
                                || (ent is Dimension && (query.Kinds.Contains(EntityKind.LinearDimension) || query.Kinds.Contains(EntityKind.AlignedDimension))));
                        if (!match)
                        {
                            continue;
                        }
                    }

                    if (query.WindowMin is Point2 wmin && query.WindowMax is Point2 wmax && !Overlaps(ent, wmin, wmax))
                    {
                        continue;
                    }

                    result.TotalMatched++;
                    if (result.Entities.Count < query.Limit)
                    {
                        result.Entities.Add(Summarize(ent, tr));
                    }
                }

                tr.Commit();
            }

            result.Truncated = result.TotalMatched > result.Entities.Count;
            return result;
        }

        // ================================================================ write

        public IReadOnlyList<ItemResult> EnsureLayers(IReadOnlyList<LayerSpec> layers)
        {
            var results = new List<ItemResult>();
            using (_doc.LockDocument())
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    LayerSpec spec = layers[i];
                    try
                    {
                        var notes = new List<string>();
                        bool created = GetOrCreateLayer(tr, spec.Name, out LayerTableRecord ltr);
                        notes.Add(created ? "created" : "updated");
                        ltr.UpgradeOpen();

                        if (spec.ColorIndex != null)
                        {
                            ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, spec.ColorIndex.Value);
                        }

                        if (spec.Linetype != null)
                        {
                            ObjectId ltId = FindOrLoadLinetype(tr, spec.Linetype);
                            if (ltId.IsNull)
                            {
                                notes.Add($"linetype {spec.Linetype} not found, left unchanged");
                            }
                            else
                            {
                                ltr.LinetypeObjectId = ltId;
                            }
                        }

                        if (spec.LineweightMm != null)
                        {
                            int target = (int)Math.Round(spec.LineweightMm.Value * 100);
                            int nearest = ValidLineweights.OrderBy(v => Math.Abs(v - target)).First();
                            ltr.LineWeight = (LineWeight)nearest;
                        }

                        if (spec.Description != null)
                        {
                            ltr.Description = spec.Description;
                        }

                        results.Add(ItemResult.Success(i, ltr.Handle.ToString(), string.Join("; ", notes)));
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(ItemResult.Failure(i, ex.Message));
                    }
                }

                tr.Commit();
            }

            Flush();
            return results;
        }

        public IReadOnlyList<ItemResult> CreateEntities(IReadOnlyList<EntitySpec> entities)
        {
            var results = new List<ItemResult>();
            using (_doc.LockDocument())
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForWrite);
                for (int i = 0; i < entities.Count; i++)
                {
                    try
                    {
                        results.Add(CreateOne(i, entities[i], space, tr));
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(ItemResult.Failure(i, ex.Message));
                    }
                }

                tr.Commit();
            }

            Flush();
            return results;
        }

        private ItemResult CreateOne(int index, EntitySpec spec, BlockTableRecord space, Transaction tr)
        {
            // Resolve anything that can fail before creating objects.
            ObjectId blockId = ObjectId.Null;
            if (spec is BlockInsertSpec bi)
            {
                var bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(bi.BlockName))
                {
                    return ItemResult.Failure(index, $"Block '{bi.BlockName}' is not defined in this drawing.");
                }

                blockId = bt[bi.BlockName];
            }

            Entity ent = Build(spec, blockId);
            ent.SetDatabaseDefaults(Db);

            string? note = null;
            if (spec.Layer != null)
            {
                if (GetOrCreateLayer(tr, spec.Layer, out _))
                {
                    note = $"created layer {spec.Layer}";
                }

                ent.Layer = spec.Layer;
            }

            ent.ColorIndex = spec.ColorIndex ?? 256; // 256 = ByLayer
            ApplyTextDefaults(ent, spec);

            space.AppendEntity(ent);
            tr.AddNewlyCreatedDBObject(ent, true);

            if (ent is Hatch hatchEnt && spec is HatchSpec hatchSpec)
            {
                try
                {
                    note = Join(note, FinishHatch(hatchEnt, hatchSpec, tr));
                }
                catch (System.Exception)
                {
                    hatchEnt.Erase(); // don't leave an empty hatch behind
                    throw;
                }
            }

            if (ent is DBText text && text.HorizontalMode != TextHorizontalMode.TextLeft)
            {
                text.AdjustAlignment(Db);
            }

            if (ent is BlockReference br && spec is BlockInsertSpec insert)
            {
                note = Join(note, AddAttributes(br, blockId, insert.Attributes, tr));
            }

            return ItemResult.Success(index, ent.Handle.ToString(), note);
        }

        private Entity Build(EntitySpec spec, ObjectId blockId)
        {
            switch (spec)
            {
                case LineSpec l:
                    return new Line(P3(l.Start), P3(l.End));

                case PolylineSpec p:
                {
                    var pl = new Polyline(p.Points.Count);
                    for (int i = 0; i < p.Points.Count; i++)
                    {
                        double bulge = p.Bulges != null ? p.Bulges[i] : 0;
                        pl.AddVertexAt(i, new Point2d(p.Points[i].X, p.Points[i].Y), bulge, 0, 0);
                    }

                    pl.Closed = p.Closed;
                    return pl;
                }

                case CircleSpec c:
                    return new Circle(P3(c.Center), Vector3d.ZAxis, c.Radius);

                case HatchSpec h:
                {
                    // Loops are added once the hatch is in the drawing (FinishHatch).
                    var hatch = new Hatch();
                    hatch.PatternScale = h.Scale;
                    hatch.PatternAngle = Rad(h.AngleDeg);
                    return hatch;
                }

                case ArcSpec a:
                    return new Arc(P3(a.Center), a.Radius, Rad(a.StartAngleDeg), Rad(a.EndAngleDeg));

                case TextSpec _:
                    return new DBText();

                case MTextSpec _:
                    return new MText();

                case LinearDimensionSpec d:
                    return new RotatedDimension(Rad(d.RotationDeg), P3(d.P1), P3(d.P2), P3(d.DimLinePoint), d.TextOverride ?? "", Db.Dimstyle);

                case AlignedDimensionSpec d:
                    return new AlignedDimension(P3(d.P1), P3(d.P2), P3(d.DimLinePoint), d.TextOverride ?? "", Db.Dimstyle);

                case BlockInsertSpec b:
                    return new BlockReference(P3(b.Position), blockId)
                    {
                        ScaleFactors = new Scale3d(b.Scale),
                        Rotation = Rad(b.RotationDeg),
                    };

                default:
                    throw new NotSupportedException(spec.Kind.ToString());
            }
        }

        /// <summary>Text properties are set after SetDatabaseDefaults so they are not overwritten.</summary>
        private void ApplyTextDefaults(Entity ent, EntitySpec spec)
        {
            if (ent is DBText text && spec is TextSpec t)
            {
                text.TextString = t.Text;
                text.Height = t.Height ?? Db.Textsize;
                text.Rotation = Rad(t.RotationDeg);
                text.Position = P3(t.Position);
                switch (t.Align)
                {
                    case TextAlign.Center:
                        text.HorizontalMode = TextHorizontalMode.TextCenter;
                        break;
                    case TextAlign.Right:
                        text.HorizontalMode = TextHorizontalMode.TextRight;
                        break;
                    case TextAlign.MiddleCenter:
                        text.HorizontalMode = TextHorizontalMode.TextCenter;
                        text.VerticalMode = TextVerticalMode.TextVerticalMid;
                        break;
                }

                if (t.Align != TextAlign.Left)
                {
                    text.AlignmentPoint = P3(t.Position);
                }
            }
            else if (ent is MText mtext && spec is MTextSpec m)
            {
                mtext.Location = P3(m.Position);
                mtext.Attachment = AttachmentPoint.TopLeft;
                mtext.TextHeight = m.Height ?? Db.Textsize;
                mtext.Width = m.Width ?? 0;
                mtext.Rotation = Rad(m.RotationDeg);
                mtext.Contents = ToMTextContents(m.Text);
            }
        }

        private static string? AddAttributes(BlockReference br, ObjectId blockId, Dictionary<string, string>? values, Transaction tr)
        {
            var btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (!btr.HasAttributeDefinitions)
            {
                return values != null && values.Count > 0 ? "block has no attributes; values ignored" : null;
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in btr)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition def) || def.Constant)
                {
                    continue;
                }

                var attr = new AttributeReference();
                attr.SetAttributeFromBlock(def, br.BlockTransform);
                if (values != null && values.TryGetValue(def.Tag, out string? value))
                {
                    attr.TextString = value;
                    used.Add(def.Tag);
                }
                else
                {
                    attr.TextString = def.TextString;
                }

                br.AttributeCollection.AppendAttribute(attr);
                tr.AddNewlyCreatedDBObject(attr, true);
            }

            var unknown = values?.Keys.Where(k => !used.Contains(k)).ToList();
            return unknown != null && unknown.Count > 0 ? "unknown attribute tag(s): " + string.Join(", ", unknown) : null;
        }

        public IReadOnlyList<ItemResult> ModifyEntities(IReadOnlyList<ModifyOperation> operations)
        {
            var results = new List<ItemResult>();
            using (_doc.LockDocument())
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < operations.Count; i++)
                {
                    try
                    {
                        results.Add(ModifyOne(i, operations[i], tr));
                    }
                    catch (System.Exception ex)
                    {
                        string msg = ex.Message.IndexOf("Locked", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "an entity is on a locked layer (" + ex.Message + ")"
                            : ex.Message;
                        results.Add(ItemResult.Failure(i, msg));
                    }
                }

                tr.Commit();
            }

            Flush();
            return results;
        }

        private ItemResult ModifyOne(int index, ModifyOperation op, Transaction tr)
        {
            var ids = new List<ObjectId>();
            var missing = new List<string>();
            foreach (string h in op.Handles)
            {
                ObjectId id = ResolveHandle(h);
                if (id.IsNull)
                {
                    missing.Add(h);
                }
                else
                {
                    ids.Add(id);
                }
            }

            if (missing.Count > 0)
            {
                return ItemResult.Failure(index, "Unknown or erased handle(s): " + string.Join(", ", missing));
            }

            var result = ItemResult.Success(index);
            switch (op.Action)
            {
                case ModifyAction.Move:
                    Transform(ids, Matrix3d.Displacement(new Vector3d(op.Dx, op.Dy, 0)), tr);
                    break;

                case ModifyAction.Rotate:
                    Transform(ids, Matrix3d.Rotation(Rad(op.AngleDeg), Vector3d.ZAxis, P3(op.BasePoint)), tr);
                    break;

                case ModifyAction.Scale:
                    Transform(ids, Matrix3d.Scaling(op.Factor, P3(op.BasePoint)), tr);
                    break;

                case ModifyAction.Copy:
                    result.NewHandles = new List<string>();
                    foreach (var group in ids.GroupBy(id => ((Entity)tr.GetObject(id, OpenMode.ForRead)).OwnerId))
                    {
                        var source = new ObjectIdCollection(group.ToArray());
                        for (int k = 1; k <= op.Count; k++)
                        {
                            // DeepClone (not Entity.Clone) so block attributes come along.
                            var map = new IdMapping();
                            Db.DeepCloneObjects(source, group.Key, map, false);
                            Matrix3d shift = Matrix3d.Displacement(new Vector3d(op.Dx * k, op.Dy * k, 0));
                            foreach (ObjectId src in source)
                            {
                                var clone = (Entity)tr.GetObject(map[src].Value, OpenMode.ForWrite);
                                clone.TransformBy(shift);
                                result.NewHandles.Add(clone.Handle.ToString());
                            }
                        }
                    }

                    break;

                case ModifyAction.Erase:
                    foreach (ObjectId id in ids)
                    {
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
                    }

                    break;

                case ModifyAction.SetLayer:
                    if (GetOrCreateLayer(tr, op.Layer!, out _))
                    {
                        result.Note = $"created layer {op.Layer}";
                    }

                    foreach (ObjectId id in ids)
                    {
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Layer = op.Layer;
                    }

                    break;

                case ModifyAction.SetColor:
                    foreach (ObjectId id in ids)
                    {
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).ColorIndex = op.ColorIndex ?? 256;
                    }

                    break;
            }

            return result;
        }

        // ================================================================ exact geometry
        // All computed by BricsCAD's geometry engine, so results match BricsCAD exactly.

        public IReadOnlyList<ItemResult> OffsetEntities(IReadOnlyList<OffsetOperation> operations)
        {
            var results = new List<ItemResult>();
            using (_doc.LockDocument())
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < operations.Count; i++)
                {
                    OffsetOperation op = operations[i];

                    // Build every offset first; only add them if the whole operation works.
                    var pending = new List<(Curve Curve, ObjectId Owner)>();
                    string? error = null;
                    try
                    {
                        foreach (string h in op.Handles)
                        {
                            error = OffsetOne(h, op, tr, pending);
                            if (error != null)
                            {
                                break;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        error = ex.Message;
                    }

                    if (error != null)
                    {
                        pending.ForEach(p => p.Curve.Dispose());
                        results.Add(ItemResult.Failure(i, error));
                        continue;
                    }

                    var item = ItemResult.Success(i);
                    item.NewHandles = new List<string>();
                    if (op.Layer != null && GetOrCreateLayer(tr, op.Layer, out _))
                    {
                        item.Note = $"created layer {op.Layer}";
                    }

                    foreach (var (curve, owner) in pending)
                    {
                        if (op.Layer != null)
                        {
                            curve.Layer = op.Layer;
                        }

                        var space = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForWrite);
                        space.AppendEntity(curve);
                        tr.AddNewlyCreatedDBObject(curve, true);
                        item.NewHandles.Add(curve.Handle.ToString());
                    }

                    results.Add(item);
                }

                tr.Commit();
            }

            Flush();
            return results;
        }

        private string? OffsetOne(string handle, OffsetOperation op, Transaction tr, List<(Curve Curve, ObjectId Owner)> pending)
        {
            ObjectId id = ResolveHandle(handle);
            if (id.IsNull)
            {
                return $"Unknown or erased handle {handle}";
            }

            if (!(tr.GetObject(id, OpenMode.ForRead) is Curve source))
            {
                return $"{handle} is not a curve; only lines, polylines, arcs, circles, ellipses and splines can be offset";
            }

            if (op.Side != OffsetSide.TowardsPoint && !source.Closed)
            {
                return $"{handle} is not closed, so 'inside'/'outside' doesn't apply; give a side_point instead";
            }

            for (int k = 1; k <= op.Count; k++)
            {
                List<Curve> plus = OffsetCurves(source, op.Distance * k);
                List<Curve> minus = OffsetCurves(source, -op.Distance * k);
                List<Curve>? chosen = ChooseSide(source, plus, minus, op, out string? why);
                foreach (Curve c in plus.Concat(minus).Where(c => chosen == null || !chosen.Contains(c)))
                {
                    c.Dispose();
                }

                if (chosen == null)
                {
                    return $"{handle}: {why}";
                }

                pending.AddRange(chosen.Select(c => (c, source.OwnerId)));
            }

            return null;
        }

        private static List<Curve> OffsetCurves(Curve curve, double distance)
        {
            var list = new List<Curve>();
            try
            {
                foreach (DBObject o in curve.GetOffsetCurves(distance))
                {
                    if (o is Curve c)
                    {
                        list.Add(c);
                    }
                    else
                    {
                        o.Dispose();
                    }
                }
            }
            catch (System.Exception)
            {
                // No offset on this side (e.g. too large to fit inside); the caller decides.
            }

            return list;
        }

        /// <summary>Same rules as the offline host: the side containing side_point, or by enclosed area.</summary>
        private static List<Curve>? ChooseSide(Curve original, List<Curve> plus, List<Curve> minus, OffsetOperation op, out string? error)
        {
            error = null;
            if (op.Side == OffsetSide.TowardsPoint)
            {
                Point3d side = P3(op.SidePoint);
                Point3d q = original.GetClosestPointTo(side, false);
                double distance = q.DistanceTo(side);
                if (distance < 1e-9)
                {
                    error = "side_point is on the curve; give a point clearly on one side";
                    return null;
                }

                Vector3d v = side - q;
                List<Curve>? best = null;
                double bestDot = 0;
                foreach (List<Curve> candidate in new[] { plus, minus })
                {
                    if (candidate.Count == 0)
                    {
                        continue;
                    }

                    Point3d c = candidate
                        .Select(x => x.GetClosestPointTo(q, false))
                        .OrderBy(x => x.DistanceTo(q))
                        .First();
                    double dot = (c - q).DotProduct(v) / distance;
                    if (dot > bestDot + 1e-9)
                    {
                        best = candidate;
                        bestDot = dot;
                    }
                }

                if (best == null)
                {
                    error = plus.Count == 0 || minus.Count == 0
                        ? "the offset distance is too large for this shape on that side"
                        : "side_point is in line with the curve; give a point clearly to one side of it";
                }

                return best;
            }

            if (plus.Count == 0 && minus.Count == 0)
            {
                error = "this shape can't be offset by that distance";
                return null;
            }

            if (plus.Count == 0 || minus.Count == 0)
            {
                if (op.Side == OffsetSide.Inside)
                {
                    error = "the offset distance is too large to fit inside this shape";
                    return null;
                }

                return plus.Count > 0 ? plus : minus;
            }

            bool plusIsInside = plus.Sum(c => c.Area) < minus.Sum(c => c.Area);
            return (op.Side == OffsetSide.Inside) == plusIsInside ? plus : minus;
        }

        public IntersectionResult Intersect(IntersectQuery query)
        {
            var result = new IntersectionResult();
            var skipped = new List<string>();
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                var curves = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
                foreach (string h in query.Handles.Concat(query.With ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ObjectId id = ResolveHandle(h);
                    if (id.IsNull)
                    {
                        skipped.Add($"{h}: not found");
                    }
                    else if (tr.GetObject(id, OpenMode.ForRead) is Curve c)
                    {
                        curves[h] = c;
                    }
                    else
                    {
                        skipped.Add($"{h}: not a curve");
                    }
                }

                IEnumerable<(string A, string B)> pairs = query.With == null
                    ? query.Handles.SelectMany((a, i) => query.Handles.Skip(i + 1).Select(b => (a, b)))
                    : query.Handles.SelectMany(a => query.With.Where(b => !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).Select(b => (a, b)));

                foreach (var (a, b) in pairs)
                {
                    if (!curves.TryGetValue(a, out Curve? ca) || !curves.TryGetValue(b, out Curve? cb))
                    {
                        continue;
                    }

                    result.PairsChecked++;
                    var points = new Point3dCollection();
                    ca.IntersectWith(cb, query.Extend ? Teigha.DatabaseServices.Intersect.ExtendBoth : Teigha.DatabaseServices.Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero);
                    var unique = new List<Point3d>();
                    foreach (Point3d p in points)
                    {
                        if (!unique.Any(u => u.DistanceTo(p) < 1e-6))
                        {
                            unique.Add(p);
                        }
                    }

                    if (unique.Count > 0)
                    {
                        result.Intersections.Add(new IntersectionPair { A = a, B = b, Points = unique.Select(Arr).ToList() });
                    }
                }

                tr.Commit();
            }

            result.Skipped = skipped.Count > 0 ? skipped : null;
            return result;
        }

        public MeasureResult Measure(MeasureQuery query)
        {
            var result = new MeasureResult();
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                foreach (string h in query.Handles)
                {
                    ObjectId id = ResolveHandle(h);
                    if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                    {
                        result.Entities.Add(new EntityMeasurement { Handle = h, Error = "not found" });
                        continue;
                    }

                    EntitySummary summary = Summarize(ent, tr);
                    var m = new EntityMeasurement
                    {
                        Handle = summary.Handle,
                        Kind = summary.Kind,
                        BoundsMin = summary.BoundsMin,
                        BoundsMax = summary.BoundsMax,
                    };

                    try
                    {
                        if (ent is Curve c)
                        {
                            double length = CurveLength(c);
                            m.Length = Math.Round(length, 6);
                            m.Closed = c.Closed;
                            if (c.Closed)
                            {
                                m.Area = Math.Round(c.Area, 6);
                            }
                            else
                            {
                                m.Start = Arr(c.StartPoint);
                                m.End = Arr(c.EndPoint);
                            }

                            m.Mid = Arr(PointAt(c, length / 2, length));
                            if (query.Point is Point2 p)
                            {
                                Point3d closest = c.GetClosestPointTo(P3(p), false);
                                m.ClosestPoint = Arr(closest);
                                m.DistanceToPoint = Math.Round(closest.DistanceTo(P3(p)), 6);
                            }
                        }
                        else if (ent is Dimension d)
                        {
                            m.Measurement = Math.Round(d.Measurement, 6);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        m.Error = ex.Message;
                    }

                    result.Entities.Add(m);
                }

                tr.Commit();
            }

            return result;
        }

        public IReadOnlyList<CurvePoints> PointsOnCurves(IReadOnlyList<PointsRequest> requests)
        {
            var results = new List<CurvePoints>();
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    PointsRequest r = requests[i];
                    var item = new CurvePoints { Index = i, Handle = r.Handle };
                    ObjectId id = ResolveHandle(r.Handle);
                    if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Curve c))
                    {
                        item.Error = id.IsNull ? "not found" : "not a curve";
                        results.Add(item);
                        continue;
                    }

                    try
                    {
                        double length = CurveLength(c);
                        item.Length = Math.Round(length, 6);
                        item.Points = SetOut.Distances(r, length, c.Closed)
                            .Select(d =>
                            {
                                Point3d p = PointAt(c, d, length);
                                Vector3d t = c.GetFirstDerivative(p);
                                double angle = Deg(Math.Atan2(t.Y, t.X));
                                return new PointOnCurve
                                {
                                    Point = Arr(p),
                                    Distance = Math.Round(d, 6),
                                    Angle = Math.Round(angle < 0 ? angle + 360 : angle, 6),
                                };
                            })
                            .ToList();
                        item.Ok = true;
                    }
                    catch (System.Exception ex)
                    {
                        item.Error = ex.Message;
                    }

                    results.Add(item);
                }

                tr.Commit();
            }

            return results;
        }

        private static double CurveLength(Curve c) =>
            c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);

        private static Point3d PointAt(Curve c, double distance, double length)
        {
            try
            {
                return c.GetPointAtDist(Math.Max(0, Math.Min(length, distance)));
            }
            catch (System.Exception)
            {
                // Rounding right at an end can be rejected; fall back to that end.
                return distance >= length / 2 ? c.EndPoint : c.StartPoint;
            }
        }

        // ================================================================ user input

        /// <summary>Set the pattern and add the boundary loops of a hatch that is already in the drawing.</summary>
        private string? FinishHatch(Hatch hatch, HatchSpec h, Transaction tr)
        {
            hatch.SetHatchPattern(HatchPatternType.PreDefined, h.Pattern.ToUpperInvariant());
            int loops = 0;

            foreach (PolylineSpec loop in h.Loops)
            {
                AppendPointLoop(hatch, loop.Points, loop.Bulges, loops++ == 0);
            }

            if (h.BoundaryHandles.Count > 0)
            {
                hatch.Associative = h.Associative;
                foreach (string handle in h.BoundaryHandles)
                {
                    ObjectId id = ResolveHandle(handle);
                    if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Curve c) || !c.Closed)
                    {
                        throw new InvalidOperationException($"{handle} is not a closed curve, so it can't bound a hatch.");
                    }

                    hatch.AppendLoop(loops++ == 0 ? HatchLoopTypes.External : HatchLoopTypes.Default, new ObjectIdCollection { id });
                }
            }

            foreach (Point2 seed in h.SeedPoints)
            {
                DBObjectCollection found = Ed.TraceBoundary(new Point3d(seed.X, seed.Y, 0), true);
                if (found == null || found.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"No closed boundary was found around {seed}. The area must be fully enclosed by linework and visible on screen.");
                }

                bool outer = true;
                foreach (DBObject o in found)
                {
                    using (o)
                    {
                        switch (o)
                        {
                            case Polyline pl:
                            {
                                var pts = new List<Point2>();
                                var bulges = new List<double>();
                                for (int i = 0; i < pl.NumberOfVertices; i++)
                                {
                                    Point3d v = pl.GetPoint3dAt(i);
                                    pts.Add(new Point2(v.X, v.Y));
                                    bulges.Add(pl.Normal.Z < 0 ? -pl.GetBulgeAt(i) : pl.GetBulgeAt(i));
                                }

                                AppendPointLoop(hatch, pts, bulges, outer);
                                break;
                            }

                            case Circle circle:
                            {
                                Point2 c = new Point2(circle.Center.X, circle.Center.Y);
                                var pts = new List<Point2> { new Point2(c.X + circle.Radius, c.Y), new Point2(c.X - circle.Radius, c.Y) };
                                AppendPointLoop(hatch, pts, new List<double> { 1, 1 }, outer);
                                break;
                            }

                            default:
                                throw new InvalidOperationException($"The boundary around {seed} came back as a {o.GetType().Name}, which can't be hatched here; draw it with polylines.");
                        }
                    }

                    outer = false;
                    loops++;
                }
            }

            if (loops == 0)
            {
                throw new InvalidOperationException("The hatch has no boundary.");
            }

            hatch.EvaluateHatch(true);
            double area = 0;
            try
            {
                area = hatch.Area;
            }
            catch (System.Exception)
            {
                // Area isn't available for every hatch.
            }

            return area > 0 ? $"area {Round(area)}" : null;
        }

        private static void AppendPointLoop(Hatch hatch, IReadOnlyList<Point2> points, IReadOnlyList<double>? bulges, bool outer)
        {
            var pts = new Point2dCollection();
            var bs = new DoubleCollection();
            for (int i = 0; i < points.Count; i++)
            {
                pts.Add(new Point2d(points[i].X, points[i].Y));
                bs.Add(bulges != null && i < bulges.Count ? bulges[i] : 0);
            }

            // Close the loop explicitly: hatch polyline loops repeat the first point at the end.
            if (points.Count > 0 && (points[0].X != points[points.Count - 1].X || points[0].Y != points[points.Count - 1].Y))
            {
                pts.Add(new Point2d(points[0].X, points[0].Y));
                bs.Add(0);
            }

            hatch.AppendLoop((outer ? HatchLoopTypes.External : HatchLoopTypes.Default) | HatchLoopTypes.Polyline, pts, bs);
        }

        public IReadOnlyDictionary<string, EntitySpec> GetCurves(IReadOnlyList<string> handles)
        {
            var result = new Dictionary<string, EntitySpec>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                foreach (string h in handles)
                {
                    ObjectId id = ResolveHandle(h);
                    if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
                    {
                        continue;
                    }

                    EntitySpec? spec = CurveSpec(ent);
                    if (spec != null)
                    {
                        spec.Layer = ent.Layer;
                        spec.ColorIndex = ent.ColorIndex == 256 ? (short?)null : (short)ent.ColorIndex;
                        result[ent.Handle.ToString()] = spec;
                    }
                }

                tr.Commit();
            }

            return result;
        }

        /// <summary>Exact plan geometry of a curve in world X/Y, or null if it isn't a line, arc, circle or polyline.</summary>
        private static EntitySpec? CurveSpec(Entity ent)
        {
            switch (ent)
            {
                case Line l:
                    return new LineSpec { Start = P2(l.StartPoint), End = P2(l.EndPoint) };

                case Arc a:
                    if (a.Normal.Z >= 0)
                    {
                        return new ArcSpec { Center = P2(a.Center), Radius = a.Radius, StartAngleDeg = Deg(a.StartAngle), EndAngleDeg = Deg(a.EndAngle) };
                    }

                    // Seen from below: it runs clockwise in plan, so swap the ends.
                    return new ArcSpec
                    {
                        Center = P2(a.Center),
                        Radius = a.Radius,
                        StartAngleDeg = Deg(Math.Atan2(a.EndPoint.Y - a.Center.Y, a.EndPoint.X - a.Center.X)),
                        EndAngleDeg = Deg(Math.Atan2(a.StartPoint.Y - a.Center.Y, a.StartPoint.X - a.Center.X)),
                    };

                case Circle c:
                    return new CircleSpec { Center = P2(c.Center), Radius = c.Radius };

                case Polyline p:
                {
                    var spec = new PolylineSpec { Closed = p.Closed, Bulges = new List<double>() };
                    for (int i = 0; i < p.NumberOfVertices; i++)
                    {
                        spec.Points.Add(P2(p.GetPoint3dAt(i)));
                        spec.Bulges.Add(p.Normal.Z < 0 ? -p.GetBulgeAt(i) : p.GetBulgeAt(i));
                    }

                    return spec;
                }

                case Polyline2d p2:
                {
                    var spec = new PolylineSpec { Closed = p2.Closed, Bulges = new List<double>() };
                    foreach (Vertex2d v in p2)
                    {
                        spec.Points.Add(P2(v.Position));
                        spec.Bulges.Add(p2.Normal.Z < 0 ? -v.Bulge : v.Bulge);
                    }

                    return spec;
                }

                case Polyline3d p3:
                {
                    var spec = new PolylineSpec { Closed = p3.Closed };
                    foreach (ObjectId vid in p3)
                    {
                        if (vid.GetObject(OpenMode.ForRead) is PolylineVertex3d v)
                        {
                            spec.Points.Add(P2(v.Position));
                        }
                    }

                    return spec;
                }

                default:
                    return null;
            }
        }

        private static Point2 P2(Point3d p) => new Point2(p.X, p.Y);

        public PickPointResult PickPoint(string prompt)
        {
            var options = new PromptPointOptions("\n" + prompt.TrimEnd(':', ' ') + ": ") { AllowNone = true };
            PromptPointResult res = Ed.GetPoint(options);
            if (res.Status != PromptStatus.OK)
            {
                return new PickPointResult { Cancelled = true };
            }

            // GetPoint returns UCS coordinates; tools work in WCS.
            Point3d wcs = res.Value.TransformBy(Ed.CurrentUserCoordinateSystem);
            return new PickPointResult { Point = Arr(wcs) };
        }

        public SelectionResult SelectEntities(string prompt)
        {
            var options = new PromptSelectionOptions { MessageForAdding = "\n" + prompt.TrimEnd(':', ' ') + ": " };
            PromptSelectionResult res = Ed.GetSelection(options);
            if (res.Status != PromptStatus.OK)
            {
                return new SelectionResult { Cancelled = true };
            }

            return new SelectionResult { Entities = SummarizeIds(res.Value.GetObjectIds(), 500) };
        }

        /// <summary>Summaries of the objects the user had selected before starting the request.</summary>
        public List<EntitySummary> SummarizeIds(IEnumerable<ObjectId> ids, int max)
        {
            var list = new List<EntitySummary>();
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Take(max))
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                    {
                        list.Add(Summarize(ent, tr));
                    }
                }

                tr.Commit();
            }

            return list;
        }

        // ================================================================ helpers

        private static EntityKind? KindOf(Entity ent)
        {
            switch (ent)
            {
                case Line _: return EntityKind.Line;
                case Polyline _: return EntityKind.Polyline;
                case Arc _: return EntityKind.Arc;
                case Circle _: return EntityKind.Circle;
                case DBText _: return EntityKind.Text;
                case MText _: return EntityKind.MText;
                case AlignedDimension _: return EntityKind.AlignedDimension;
                case Dimension _: return EntityKind.LinearDimension;
                case Table _: return null;
                case BlockReference _: return EntityKind.BlockInsert;
                case Hatch _: return EntityKind.Hatch;
                default: return null;
            }
        }

        private EntitySummary Summarize(Entity ent, Transaction tr)
        {
            var s = new EntitySummary
            {
                Handle = ent.Handle.ToString(),
                Layer = ent.Layer,
                Color = ent.ColorIndex == 256 ? (short?)null : (short)ent.ColorIndex,
            };

            switch (ent)
            {
                case Line l:
                    s.Kind = "line";
                    s.Start = Arr(l.StartPoint);
                    s.End = Arr(l.EndPoint);
                    break;

                case Polyline p:
                {
                    s.Kind = "polyline";
                    int n = Math.Min(p.NumberOfVertices, MaxPolylinePoints);
                    s.Points = Enumerable.Range(0, n).Select(i => Arr(p.GetPoint2dAt(i))).ToArray();
                    if (Enumerable.Range(0, n).Any(i => Math.Abs(p.GetBulgeAt(i)) > 1e-12))
                    {
                        s.Bulges = Enumerable.Range(0, n).Select(i => Math.Round(p.GetBulgeAt(i), 6)).ToArray();
                    }

                    s.Closed = p.Closed;
                    break;
                }

                case Hatch h:
                    s.Kind = "hatch";
                    s.Pattern = h.PatternName;
                    try
                    {
                        s.Area = Round(h.Area);
                    }
                    catch (System.Exception)
                    {
                        // Not every hatch can report its area.
                    }

                    break;

                case Arc a:
                    s.Kind = "arc";
                    s.Center = Arr(a.Center);
                    s.Radius = Round(a.Radius);
                    s.StartAngle = Round(Deg(a.StartAngle));
                    s.EndAngle = Round(Deg(a.EndAngle));
                    break;

                case Circle c:
                    s.Kind = "circle";
                    s.Center = Arr(c.Center);
                    s.Radius = Round(c.Radius);
                    break;

                case DBText t:
                    s.Kind = "text";
                    s.Position = Arr(t.HorizontalMode == TextHorizontalMode.TextLeft ? t.Position : t.AlignmentPoint);
                    s.Text = t.TextString;
                    s.Height = Round(t.Height);
                    s.Rotation = Round(Deg(t.Rotation));
                    break;

                case MText m:
                    s.Kind = "mtext";
                    s.Position = Arr(m.Location);
                    s.Text = m.Text;
                    s.Height = Round(m.TextHeight);
                    s.Rotation = Round(Deg(m.Rotation));
                    break;

                case Dimension d:
                    s.Kind = "dimension";
                    s.Measurement = Round(d.Measurement);
                    s.Text = string.IsNullOrEmpty(d.DimensionText) ? null : d.DimensionText;
                    if (d is RotatedDimension rd)
                    {
                        s.Start = Arr(rd.XLine1Point);
                        s.End = Arr(rd.XLine2Point);
                        s.Rotation = Round(Deg(rd.Rotation));
                    }
                    else if (d is AlignedDimension ad)
                    {
                        s.Start = Arr(ad.XLine1Point);
                        s.End = Arr(ad.XLine2Point);
                    }

                    break;

                case Table _:
                    s.Kind = "table";
                    break;

                case BlockReference br:
                {
                    s.Kind = "block_insert";
                    ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    s.Block = ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
                    s.Position = Arr(br.Position);
                    s.Rotation = Round(Deg(br.Rotation));
                    break;
                }

                default:
                    s.Kind = ent.GetType().Name.ToLowerInvariant();
                    break;
            }

            try
            {
                Extents3d ext = ent.GeometricExtents;
                s.BoundsMin = Arr(ext.MinPoint);
                s.BoundsMax = Arr(ext.MaxPoint);
            }
            catch (System.Exception)
            {
                // Some entities (e.g. empty text) have no extents.
            }

            return s;
        }

        private static bool Overlaps(Entity ent, Point2 a, Point2 b)
        {
            try
            {
                Extents3d e = ent.GeometricExtents;
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                double y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                return e.MinPoint.X <= x1 && e.MaxPoint.X >= x0 && e.MinPoint.Y <= y1 && e.MaxPoint.Y >= y0;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private void Transform(List<ObjectId> ids, Matrix3d matrix, Transaction tr)
        {
            foreach (ObjectId id in ids)
            {
                ((Entity)tr.GetObject(id, OpenMode.ForWrite)).TransformBy(matrix);
            }
        }

        private ObjectId ResolveHandle(string hex)
        {
            try
            {
                var handle = new Handle(Convert.ToInt64(hex, 16));
                return Db.TryGetObjectId(handle, out ObjectId id) && !id.IsErased ? id : ObjectId.Null;
            }
            catch (FormatException)
            {
                return ObjectId.Null;
            }
        }

        /// <summary>Returns true if the layer had to be created.</summary>
        private bool GetOrCreateLayer(Transaction tr, string name, out LayerTableRecord layer)
        {
            var table = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
            if (table.Has(name))
            {
                layer = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForRead);
                return false;
            }

            table.UpgradeOpen();
            layer = new LayerTableRecord { Name = name };
            table.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return true;
        }

        private ObjectId FindOrLoadLinetype(Transaction tr, string name)
        {
            var table = (LinetypeTable)tr.GetObject(Db.LinetypeTableId, OpenMode.ForRead);
            if (table.Has(name))
            {
                return table[name];
            }

            // BricsCAD ships iso.lin (metric) and default.lin (imperial).
            foreach (string file in new[] { "iso.lin", "default.lin" })
            {
                try
                {
                    Db.LoadLineTypeFile(name, file);
                }
                catch (System.Exception)
                {
                    continue;
                }

                table = (LinetypeTable)tr.GetObject(Db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name))
                {
                    return table[name];
                }
            }

            return ObjectId.Null;
        }

        private string SpaceName(Transaction tr)
        {
            var space = (BlockTableRecord)tr.GetObject(Db.CurrentSpaceId, OpenMode.ForRead);
            if (string.Equals(space.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
            {
                return "Model";
            }

            return space.IsLayout ? "Layout: " + ((Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead)).LayoutName : space.Name;
        }

        private static List<string> Names<T>(Transaction tr, ObjectId tableId)
            where T : SymbolTableRecord
        {
            var names = new List<string>();
            foreach (ObjectId id in (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead))
            {
                if (names.Count >= MaxListedNames)
                {
                    break;
                }

                names.Add(((T)tr.GetObject(id, OpenMode.ForRead)).Name);
            }

            return names;
        }

        /// <summary>Escape MText formatting characters and turn newlines into paragraph breaks.</summary>
        private static string ToMTextContents(string text) =>
            text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
                .Replace("\r\n", "\n").Replace("\n", "\\P");

        private void Flush()
        {
            _doc.TransactionManager.FlushGraphics();
            Ed.UpdateScreen();
        }

        private static string? Join(string? a, string? b) =>
            a == null ? b : b == null ? a : a + "; " + b;

        private static Point3d P3(Point2 p) => new Point3d(p.X, p.Y, 0);

        private static double Rad(double deg) => deg * Math.PI / 180.0;

        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        private static double Round(double v) => Math.Round(v, 4);

        private static double[] Arr(Point3d p) => new[] { Round(p.X), Round(p.Y) };

        private static double[] Arr(Point2d p) => new[] { Round(p.X), Round(p.Y) };
    }
}
