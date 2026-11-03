using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using CsdBridge.Core.Drawing;
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

        // ================================================================ user input

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
                    s.Closed = p.Closed;
                    break;
                }

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
