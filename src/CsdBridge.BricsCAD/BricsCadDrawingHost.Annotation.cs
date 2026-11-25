using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsdBridge.Core.Drawing;
using CsdBridge.Core.Geometry;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Annotation in BricsCAD: named styles, the radius, diameter, angular, arc length and
    /// ordinate dimensions, multileaders, tables, editing annotation in place, and the
    /// text boxes the view tool uses to find clashing labels.
    /// </summary>
    internal sealed partial class BricsCadDrawingHost
    {
        private const string MLeaderStyleDictionary = "ACAD_MLEADERSTYLE";
        private const string TableStyleDictionary = "ACAD_TABLESTYLE";
        private const int MaxTableRowsListed = 40;
        private const int MaxTableColumnsListed = 50;
        private const int MaxAttributesListed = 60;

        /// <summary>The styles an entity will use, looked up before anything is created.</summary>
        private sealed class Styles
        {
            /// <summary>Text style of text and mtext, or of a multileader's or table's text. Null = leave as is.</summary>
            public ObjectId TextStyle = ObjectId.Null;

            /// <summary>Fixed height of <see cref="TextStyle"/>, when it has one.</summary>
            public double? TextHeight;

            public ObjectId DimStyle = ObjectId.Null;

            /// <summary>Text height of <see cref="DimStyle"/> in drawing units (text height x overall scale).</summary>
            public double DimTextSize;

            public ObjectId MLeaderStyle = ObjectId.Null;
            public ObjectId TableStyle = ObjectId.Null;
        }

        // ================================================================ styles

        private Styles ResolveStyles(EntitySpec spec, Transaction tr, out string? error)
        {
            error = null;
            var styles = new Styles { DimStyle = Db.Dimstyle };

            string? textStyle = spec is TextSpec || spec is MTextSpec ? spec.Style
                : spec is MLeaderSpec leader ? leader.TextStyle
                : spec is TableSpec tableSpec ? tableSpec.TextStyle
                : null;
            if (textStyle != null)
            {
                styles.TextStyle = FindSymbol(tr, Db.TextStyleTableId, textStyle);
                if (styles.TextStyle.IsNull)
                {
                    error = UnknownStyle("Text style", textStyle, Names<TextStyleTableRecord>(tr, Db.TextStyleTableId));
                    return styles;
                }

                double fixedHeight = ((TextStyleTableRecord)tr.GetObject(styles.TextStyle, OpenMode.ForRead)).TextSize;
                styles.TextHeight = fixedHeight > 0 ? fixedHeight : (double?)null;
            }

            if (spec is DimensionSpec)
            {
                if (spec.Style != null)
                {
                    styles.DimStyle = FindSymbol(tr, Db.DimStyleTableId, spec.Style);
                    if (styles.DimStyle.IsNull)
                    {
                        error = UnknownStyle("Dimension style", spec.Style, Names<DimStyleTableRecord>(tr, Db.DimStyleTableId));
                        return styles;
                    }
                }

                var record = (DimStyleTableRecord)tr.GetObject(styles.DimStyle, OpenMode.ForRead);
                styles.DimTextSize = record.Dimtxt * (record.Dimscale > 0 ? record.Dimscale : 1);
            }
            else if (spec is MLeaderSpec)
            {
                styles.MLeaderStyle = NamedOrCurrent(tr, MLeaderStyleDictionary, "CMLEADERSTYLE", spec.Style, "Multileader style", out error);
            }
            else if (spec is TableSpec)
            {
                styles.TableStyle = NamedOrCurrent(tr, TableStyleDictionary, "CTABLESTYLE", spec.Style, "Table style", out error);
            }

            return styles;
        }

        /// <summary>
        /// The multileader or table style asked for, or the drawing's current one (from its
        /// system variable, falling back to Standard). Null id only if the drawing has none.
        /// </summary>
        private ObjectId NamedOrCurrent(Transaction tr, string dictionary, string currentVariable, string? name, string what, out string? error)
        {
            error = null;
            Dictionary<string, ObjectId> known = StyleDictionary(tr, dictionary);
            if (name != null)
            {
                if (known.TryGetValue(name, out ObjectId asked))
                {
                    return asked;
                }

                error = UnknownStyle(what, name, known.Keys);
                return ObjectId.Null;
            }

            string? current = CurrentStyleName(currentVariable);
            if (current != null && known.TryGetValue(current, out ObjectId id))
            {
                return id;
            }

            return known.TryGetValue("Standard", out id) ? id : known.Values.FirstOrDefault();
        }

        private static string? CurrentStyleName(string variable)
        {
            try
            {
                return Bricscad.ApplicationServices.Application.GetSystemVariable(variable) as string;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>Styles are set after SetDatabaseDefaults, which puts the current ones back.</summary>
        private static void ApplyStyles(Entity ent, EntitySpec spec, Styles styles, Transaction tr)
        {
            if (ent is DBText text && !styles.TextStyle.IsNull)
            {
                var record = (TextStyleTableRecord)tr.GetObject(styles.TextStyle, OpenMode.ForRead);
                text.TextStyleId = styles.TextStyle;
                text.WidthFactor = record.XScale > 0 ? record.XScale : 1;
                text.Oblique = record.ObliquingAngle;
            }
            else if (ent is MText mtext && !styles.TextStyle.IsNull)
            {
                mtext.TextStyleId = styles.TextStyle;
            }
            else if (ent is Dimension dimension && spec.Style != null)
            {
                dimension.DimensionStyle = styles.DimStyle;
            }
        }

        private static ObjectId FindSymbol(Transaction tr, ObjectId tableId, string name)
        {
            var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            return table.Has(name) ? table[name] : ObjectId.Null;
        }

        /// <summary>Multileader and table styles live in dictionaries under the named objects dictionary.</summary>
        private Dictionary<string, ObjectId> StyleDictionary(Transaction tr, string key)
        {
            var result = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            var named = (DBDictionary)tr.GetObject(Db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!named.Contains(key))
            {
                return result;
            }

            var styles = (DBDictionary)tr.GetObject(named.GetAt(key), OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in styles)
            {
                result[entry.Key] = entry.Value;
            }

            return result;
        }

        private static string UnknownStyle(string what, string name, IEnumerable<string> known)
        {
            List<string> names = known.ToList();
            string list = string.Join(", ", names.Take(40)) + (names.Count > 40 ? ", ..." : "");
            return $"{what} '{name}' is not in this drawing. Available: {list}.";
        }

        private static string? NameOf(Transaction tr, ObjectId id)
        {
            if (id.IsNull)
            {
                return null;
            }

            switch (tr.GetObject(id, OpenMode.ForRead))
            {
                case SymbolTableRecord record: return record.Name;
                case MLeaderStyle leaderStyle: return leaderStyle.Name;
                case TableStyle tableStyle: return tableStyle.Name;
                default: return null;
            }
        }

        /// <summary>What each style looks like, so Claude can choose one that suits the sheet scale.</summary>
        private void AddStyleDetails(DrawingInfo info, Transaction tr)
        {
            info.TextStyleDetails = new List<StyleInfo>();
            foreach (ObjectId id in (SymbolTable)tr.GetObject(Db.TextStyleTableId, OpenMode.ForRead))
            {
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (style.Name.Length > 0 && info.TextStyleDetails.Count < MaxListedNames)
                {
                    string font = string.IsNullOrEmpty(style.FileName) ? style.Font.TypeFace : style.FileName;
                    info.TextStyleDetails.Add(new StyleInfo { Name = style.Name, Font = font, TextHeight = Round(style.TextSize) });
                }
            }

            info.DimStyleDetails = new List<StyleInfo>();
            foreach (ObjectId id in (SymbolTable)tr.GetObject(Db.DimStyleTableId, OpenMode.ForRead))
            {
                var style = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (info.DimStyleDetails.Count < MaxListedNames)
                {
                    info.DimStyleDetails.Add(new StyleInfo
                    {
                        Name = style.Name,
                        Scale = Round(style.Dimscale),
                        TextHeight = Round(style.Dimtxt),
                        ArrowSize = Round(style.Dimasz),
                        TextStyle = NameOf(tr, style.Dimtxsty),
                    });
                }
            }

            try
            {
                info.MLeaderStyleDetails = new List<StyleInfo>();
                foreach (KeyValuePair<string, ObjectId> entry in StyleDictionary(tr, MLeaderStyleDictionary))
                {
                    info.MLeaderStyles.Add(entry.Key);
                    if (tr.GetObject(entry.Value, OpenMode.ForRead) is MLeaderStyle style)
                    {
                        info.MLeaderStyleDetails.Add(new StyleInfo
                        {
                            Name = entry.Key,
                            Scale = Round(style.Scale),
                            TextHeight = Round(style.TextHeight),
                            ArrowSize = Round(style.ArrowSize),
                            TextStyle = NameOf(tr, style.TextStyleId),
                        });
                    }
                }

                info.TableStyles = StyleDictionary(tr, TableStyleDictionary).Keys.ToList();
                info.CurrentMLeaderStyle = CurrentStyleName("CMLEADERSTYLE");
                info.CurrentTableStyle = CurrentStyleName("CTABLESTYLE");
            }
            catch (System.Exception)
            {
                // The drawing setup is still useful without multileader and table styles.
                info.MLeaderStyleDetails = null;
            }
        }

        // ================================================================ create

        private Entity BuildAnnotation(EntitySpec spec, Styles styles)
        {
            string text = (spec as DimensionSpec)?.TextOverride ?? "";
            switch (spec)
            {
                case RadialDimensionSpec r:
                {
                    double leader = r.LeaderLength ?? 2 * (styles.DimTextSize > 0 ? styles.DimTextSize : Db.Textsize);
                    if (!r.Diameter)
                    {
                        return new RadialDimension(P3(r.Center), P3(r.ChordPoint), leader, text, styles.DimStyle);
                    }

                    var far = new Point3d(2 * r.Center.X - r.ChordPoint.X, 2 * r.Center.Y - r.ChordPoint.Y, 0);
                    return new DiametricDimension(P3(r.ChordPoint), far, leader, text, styles.DimStyle);
                }

                case AngularDimensionSpec a when a.ArcLength:
                    return new ArcDimension(P3(a.Center), P3(a.P1), P3(a.P2), P3(a.ArcPoint), text, styles.DimStyle);

                case AngularDimensionSpec a:
                    return new Point3AngularDimension(P3(a.Center), P3(a.P1), P3(a.P2), P3(a.ArcPoint), text, styles.DimStyle);

                case OrdinateDimensionSpec o:
                {
                    var dimension = new OrdinateDimension(o.UseXAxis, P3(o.FeaturePoint), P3(o.LeaderEnd), text, styles.DimStyle);
                    if (o.Origin is Point2 origin)
                    {
                        dimension.Origin = P3(origin);
                    }

                    return dimension;
                }

                case MLeaderSpec l:
                    return BuildLeader(l, styles);

                case TableSpec t:
                    return BuildTable(t, styles);

                default:
                    throw new NotSupportedException(spec.Kind.ToString());
            }
        }

        private MLeader BuildLeader(MLeaderSpec spec, Styles styles)
        {
            var leader = new MLeader();
            leader.SetDatabaseDefaults(Db);
            if (!styles.MLeaderStyle.IsNull)
            {
                leader.MLeaderStyle = styles.MLeaderStyle;
            }

            // Content type, then the leader lines, then the text: the order that works in BricsCAD.
            leader.ContentType = ContentType.MTextContent;
            Point3d landing = P3(spec.TextPosition);
            int cluster = leader.AddLeader();
            foreach (Point2 arrow in spec.ArrowPoints)
            {
                AddLeaderLine(leader, cluster, P3(arrow), landing);
            }

            double height = spec.Height ?? styles.TextHeight ?? (leader.TextHeight > 0 ? leader.TextHeight : Db.Textsize);
            using (var content = new MText())
            {
                content.SetDatabaseDefaults(Db);
                content.TextStyleId = styles.TextStyle.IsNull ? leader.TextStyleId : styles.TextStyle;
                content.TextHeight = height;
                content.Width = spec.Width ?? 0;
                content.Contents = ToMTextContents(spec.Text);
                content.Location = landing;
                leader.MText = content;
            }

            leader.TextHeight = height;
            if (spec.ArrowSize is double arrowSize)
            {
                leader.ArrowSize = arrowSize;
            }

            return leader;
        }

        /// <summary>
        /// Add a leader line from an arrow point to the landing. BricsCAD gives a new line its
        /// last vertex already, so that one is moved rather than a third one added; where a
        /// line starts with a single vertex, the landing is added instead.
        /// </summary>
        private static void AddLeaderLine(MLeader leader, int cluster, Point3d arrow, Point3d landing)
        {
            int line = leader.AddLeaderLine(cluster);
            leader.AddFirstVertex(line, arrow);
            leader.SetLastVertex(line, landing);
            if (leader.GetFirstVertex(line).DistanceTo(arrow) > 1e-9)
            {
                leader.SetFirstVertex(line, arrow);
                leader.AddLastVertex(line, landing);
            }
        }

        private Table BuildTable(TableSpec spec, Styles styles)
        {
            (List<double> widths, double rowHeight, double textHeight) = AnnotationGeometry.TableLayout(spec, styles.TextHeight ?? Db.Textsize);
            List<List<string>> cells = AnnotationGeometry.TableCells(spec);
            int rows = cells.Count, columns = spec.ColumnCount;

            var table = new Table();
            table.SetDatabaseDefaults(Db);
            if (!styles.TableStyle.IsNull)
            {
                table.TableStyle = styles.TableStyle;
            }

            table.SetSize(rows, columns);
            table.Position = P3(spec.Position);
            for (int c = 0; c < columns; c++)
            {
                table.SetColumnWidth(c, widths[c]);
            }

            // Most table styles start with a merged title row: make the top row what was asked for.
            if (columns > 1)
            {
                try
                {
                    CellRange top = CellRange.Create(table, 0, 0, 0, columns - 1);
                    if (spec.Title != null)
                    {
                        table.MergeCells(top);
                    }
                    else
                    {
                        table.UnmergeCells(top);
                    }
                }
                catch (System.Exception)
                {
                    // Already merged (or not), which is what we wanted.
                }
            }

            for (int r = 0; r < rows; r++)
            {
                bool title = spec.Title != null && r == 0;
                for (int c = 0; c < (title ? 1 : columns); c++)
                {
                    table.SetTextHeight(r, c, textHeight);
                    table.SetAlignment(r, c, CellAlignment.MiddleCenter);
                    if (!styles.TextStyle.IsNull)
                    {
                        table.SetTextStyle(r, c, styles.TextStyle);
                    }

                    table.SetTextString(r, c, cells[r][c]);
                }
            }

            for (int r = 0; r < rows; r++)
            {
                table.SetRowHeight(r, rowHeight);
            }

            table.GenerateLayout();
            return table;
        }

        // ================================================================ edit

        public IReadOnlyList<ItemResult> EditAnnotations(IReadOnlyList<AnnotationEdit> edits)
        {
            var results = new List<ItemResult>();
            using (_doc.LockDocument())
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    try
                    {
                        results.Add(EditOne(i, edits[i], tr));
                    }
                    catch (System.Exception ex)
                    {
                        string message = ex.Message.IndexOf("Locked", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "the object is on a locked layer (" + ex.Message + ")"
                            : ex.Message;
                        results.Add(ItemResult.Failure(i, message));
                    }
                }

                tr.Commit();
            }

            Flush();
            return results;
        }

        private ItemResult EditOne(int index, AnnotationEdit edit, Transaction tr)
        {
            ObjectId id = ResolveHandle(edit.Handle);
            if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Entity ent))
            {
                return ItemResult.Failure(index, $"Unknown or erased handle: {edit.Handle}");
            }

            // Check everything before changing anything, so a refused edit leaves the object as it was.
            string kind = AnnotationKind(ent);
            string? problem = AnnotationEdits.Problem(edit, kind);
            if (problem != null)
            {
                return ItemResult.Failure(index, problem);
            }

            ObjectId style = ObjectId.Null;
            if (edit.Style != null)
            {
                style = FindStyleFor(ent, edit.Style, tr, out problem);
                if (problem != null)
                {
                    return ItemResult.Failure(index, problem);
                }
            }

            if (ent is Table sized && edit.Cells != null)
            {
                int rows = sized.NumRows, columns = sized.NumColumns;
                TableCellEdit? outside = edit.Cells.FirstOrDefault(c => c.Row >= rows || c.Column >= columns);
                if (outside != null)
                {
                    return ItemResult.Failure(index,
                        $"cell row {outside.Row}, column {outside.Column} is outside the table ({rows} rows x {columns} columns, counted from 0)");
                }
            }

            ent.UpgradeOpen();
            string? note = null;
            switch (ent)
            {
                case DBText text:
                    EditText(text, edit, style);
                    break;

                case MText mtext:
                    if (edit.Text != null)
                    {
                        mtext.Contents = ToMTextContents(edit.Text);
                    }

                    mtext.TextHeight = edit.Height ?? mtext.TextHeight;
                    mtext.Width = edit.Width ?? mtext.Width;
                    mtext.Rotation = edit.RotationDeg is double degrees ? Rad(degrees) : mtext.Rotation;
                    mtext.Location = edit.Position is Point2 at ? P3(at) : mtext.Location;
                    if (!style.IsNull)
                    {
                        mtext.TextStyleId = style;
                    }

                    break;

                case Dimension dimension:
                    if (edit.TextOverride != null)
                    {
                        dimension.DimensionText = edit.TextOverride == "<>" ? "" : edit.TextOverride;
                    }

                    if (!style.IsNull)
                    {
                        dimension.DimensionStyle = style;
                    }

                    break;

                case MLeader leader:
                    EditLeader(leader, edit, style);
                    break;

                case Table table:
                    foreach (TableCellEdit cell in edit.Cells ?? new List<TableCellEdit>())
                    {
                        table.SetTextString(cell.Row, cell.Column, cell.Text);
                    }

                    if (!style.IsNull)
                    {
                        table.TableStyle = style;
                    }

                    table.GenerateLayout();
                    break;

                case BlockReference insert:
                    note = EditAttributes(insert, edit.Attributes!, tr, out problem);
                    if (problem != null)
                    {
                        return ItemResult.Failure(index, problem);
                    }

                    break;
            }

            return ItemResult.Success(index, ent.Handle.ToString(), note);
        }

        private void EditText(DBText text, AnnotationEdit edit, ObjectId style)
        {
            text.TextString = edit.Text ?? text.TextString;
            text.Height = edit.Height ?? text.Height;
            text.Rotation = edit.RotationDeg is double degrees ? Rad(degrees) : text.Rotation;
            if (!style.IsNull)
            {
                text.TextStyleId = style;
            }

            bool aligned = text.HorizontalMode != TextHorizontalMode.TextLeft || text.VerticalMode != TextVerticalMode.TextBase;
            if (edit.Position is Point2 at)
            {
                if (aligned)
                {
                    text.AlignmentPoint = P3(at);
                }
                else
                {
                    text.Position = P3(at);
                }
            }

            if (aligned)
            {
                // Centred and right-aligned text is placed from its alignment point: work its position out again.
                text.AdjustAlignment(Db);
            }
        }

        private void EditLeader(MLeader leader, AnnotationEdit edit, ObjectId style)
        {
            if (!style.IsNull)
            {
                leader.MLeaderStyle = style;
            }

            List<int> lines = LeaderLines(leader, out int cluster);
            if (edit.ArrowPoints != null)
            {
                Point3d landing = lines.Count > 0 ? leader.GetLastVertex(lines[0]) : leader.TextLocation;
                for (int i = 0; i < edit.ArrowPoints.Count; i++)
                {
                    if (i < lines.Count)
                    {
                        leader.SetFirstVertex(lines[i], P3(edit.ArrowPoints[i]));
                    }
                    else
                    {
                        AddLeaderLine(leader, cluster >= 0 ? cluster : cluster = leader.AddLeader(), P3(edit.ArrowPoints[i]), landing);
                    }
                }

                for (int i = lines.Count - 1; i >= edit.ArrowPoints.Count; i--)
                {
                    leader.RemoveLeaderLine(lines[i]);
                }

                lines = LeaderLines(leader, out cluster);
            }

            if (edit.Position is Point2 position)
            {
                // Move the landing and the text together, whichever of the two BricsCAD treats as leading.
                Point3d from = lines.Count > 0 ? leader.GetLastVertex(lines[0]) : leader.TextLocation;
                Vector3d shift = P3(position) - from;
                Point3d textAt = leader.TextLocation + shift;
                foreach (int line in lines)
                {
                    leader.SetLastVertex(line, leader.GetLastVertex(line) + shift);
                }

                leader.TextLocation = textAt;
            }

            if (edit.Text != null || edit.Width != null)
            {
                using (MText content = leader.MText)
                {
                    if (content == null)
                    {
                        throw new InvalidOperationException("this multileader has block content, not text");
                    }

                    if (edit.Text != null)
                    {
                        content.Contents = ToMTextContents(edit.Text);
                    }

                    content.Width = edit.Width ?? content.Width;
                    leader.MText = content;
                }
            }

            if (edit.Height is double height)
            {
                leader.TextHeight = height;
            }
        }

        /// <summary>Every leader line of a multileader, and the first leader cluster (-1 if it has none).</summary>
        private static List<int> LeaderLines(MLeader leader, out int firstCluster)
        {
            var lines = new List<int>();
            firstCluster = -1;
            foreach (object cluster in leader.GetLeaderIndexes())
            {
                int index = Convert.ToInt32(cluster, CultureInfo.InvariantCulture);
                if (firstCluster < 0)
                {
                    firstCluster = index;
                }

                foreach (object line in leader.GetLeaderLineIndexes(index))
                {
                    lines.Add(Convert.ToInt32(line, CultureInfo.InvariantCulture));
                }
            }

            return lines;
        }

        private static string? EditAttributes(BlockReference insert, Dictionary<string, string> values, Transaction tr, out string? problem)
        {
            problem = null;
            var byTag = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in insert.AttributeCollection)
            {
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is AttributeReference attribute)
                {
                    byTag[attribute.Tag] = attribute;
                }
            }

            List<string> unknown = values.Keys.Where(k => !byTag.ContainsKey(k)).ToList();
            if (unknown.Count == values.Count)
            {
                problem = byTag.Count == 0
                    ? "this block insert has no attributes"
                    : $"no attribute tagged {string.Join(", ", unknown)}; this block has: {string.Join(", ", byTag.Keys)}";
                return null;
            }

            foreach (KeyValuePair<string, string> value in values)
            {
                if (byTag.TryGetValue(value.Key, out AttributeReference? attribute))
                {
                    attribute.UpgradeOpen();
                    attribute.TextString = value.Value;
                }
            }

            return unknown.Count > 0 ? $"unknown attribute tag(s): {string.Join(", ", unknown)}; this block has: {string.Join(", ", byTag.Keys)}" : null;
        }

        private ObjectId FindStyleFor(Entity ent, string name, Transaction tr, out string? problem)
        {
            problem = null;
            ObjectId id;
            switch (ent)
            {
                case DBText _:
                case MText _:
                    id = FindSymbol(tr, Db.TextStyleTableId, name);
                    problem = id.IsNull ? UnknownStyle("Text style", name, Names<TextStyleTableRecord>(tr, Db.TextStyleTableId)) : null;
                    return id;

                case Dimension _:
                    id = FindSymbol(tr, Db.DimStyleTableId, name);
                    problem = id.IsNull ? UnknownStyle("Dimension style", name, Names<DimStyleTableRecord>(tr, Db.DimStyleTableId)) : null;
                    return id;

                case MLeader _:
                case Table _:
                {
                    bool leader = ent is MLeader;
                    Dictionary<string, ObjectId> known = StyleDictionary(tr, leader ? MLeaderStyleDictionary : TableStyleDictionary);
                    if (!known.TryGetValue(name, out id))
                    {
                        problem = UnknownStyle(leader ? "Multileader style" : "Table style", name, known.Keys);
                    }

                    return id;
                }

                default:
                    return ObjectId.Null;
            }
        }

        /// <summary>The kind names the shared edit rules use.</summary>
        private static string AnnotationKind(Entity ent)
        {
            switch (ent)
            {
                case DBText _: return "text";
                case MText _: return "mtext";
                case Dimension _: return "dimension";
                case MLeader _: return "mleader";
                case Table _: return "table";
                case BlockReference _: return "block_insert";
                default: return ent.GetType().Name.ToLowerInvariant();
            }
        }

        // ================================================================ read

        /// <summary>What a dimension shows: a length, or degrees for an angular dimension.</summary>
        private static double DimensionValue(Dimension d) =>
            d is Point3AngularDimension || d is LineAngularDimension2 ? Deg(d.Measurement) : d.Measurement;

        private static void SummarizeDimension(EntitySummary s, Dimension d, Transaction tr)
        {
            s.Kind = "dimension";
            s.Measurement = Round(DimensionValue(d));
            s.Text = string.IsNullOrEmpty(d.DimensionText) ? null : d.DimensionText;
            s.Style = NameOf(tr, d.DimensionStyle);
            switch (d)
            {
                case RotatedDimension linear:
                    s.DimType = "linear";
                    s.Start = Arr(linear.XLine1Point);
                    s.End = Arr(linear.XLine2Point);
                    s.DimLinePoint = Arr(linear.DimLinePoint);
                    s.Rotation = Round(Deg(linear.Rotation));
                    break;

                case AlignedDimension aligned:
                    s.DimType = "aligned";
                    s.Start = Arr(aligned.XLine1Point);
                    s.End = Arr(aligned.XLine2Point);
                    s.DimLinePoint = Arr(aligned.DimLinePoint);
                    break;

                case RadialDimension radius:
                    s.DimType = "radius";
                    s.Center = Arr(radius.Center);
                    s.Start = Arr(radius.ChordPoint);
                    break;

                case DiametricDimension diameter:
                    s.DimType = "diameter";
                    s.Start = Arr(diameter.ChordPoint);
                    s.Center = Arr(new Point3d(
                        (diameter.ChordPoint.X + diameter.FarChordPoint.X) / 2, (diameter.ChordPoint.Y + diameter.FarChordPoint.Y) / 2, 0));
                    break;

                case Point3AngularDimension angle:
                    s.DimType = "angular";
                    s.Center = Arr(angle.CenterPoint);
                    s.Start = Arr(angle.XLine1Point);
                    s.End = Arr(angle.XLine2Point);
                    s.DimLinePoint = Arr(angle.ArcPoint);
                    break;

                case LineAngularDimension2 angle:
                    s.DimType = "angular";
                    s.DimLinePoint = Arr(angle.ArcPoint);
                    break;

                case ArcDimension arc:
                    s.DimType = "arc_length";
                    s.Center = Arr(arc.CenterPoint);
                    s.Start = Arr(arc.XLine1Point);
                    s.End = Arr(arc.XLine2Point);
                    s.DimLinePoint = Arr(arc.ArcPoint);
                    break;

                case OrdinateDimension ordinate:
                    s.DimType = ordinate.UsingXAxis ? "ordinate_x" : "ordinate_y";
                    s.Start = Arr(ordinate.DefiningPoint);
                    s.End = Arr(ordinate.LeaderEndPoint);
                    break;
            }
        }

        private static void SummarizeLeader(EntitySummary s, MLeader leader, Transaction tr, bool forView)
        {
            s.Kind = "mleader";
            s.Style = NameOf(tr, leader.MLeaderStyle);
            List<int> lines = LeaderLines(leader, out _);
            s.ArrowPoints = lines.Select(line => Arr(leader.GetFirstVertex(line))).ToArray();
            s.Position = lines.Count > 0 ? Arr(leader.GetLastVertex(lines[0])) : null;
            if (leader.ContentType != ContentType.MTextContent)
            {
                return;
            }

            s.Height = Round(leader.TextHeight);
            using (MText content = leader.MText)
            {
                if (content == null)
                {
                    return;
                }

                s.Text = content.Text;
                s.Position ??= Arr(content.Location);
                if (forView)
                {
                    s.TextBox = MTextBox(content);
                }
            }
        }

        private static void SummarizeTable(EntitySummary s, Table table, Transaction tr)
        {
            s.Kind = "table";
            s.Position = Arr(table.Position);
            s.Style = NameOf(tr, table.TableStyle);
            int rows = table.NumRows, columns = table.NumColumns;
            s.RowCount = rows;
            s.ColumnCount = columns;
            int listedColumns = Math.Min(columns, MaxTableColumnsListed);
            s.ColumnWidths = Enumerable.Range(0, listedColumns).Select(c => Round(table.ColumnWidth(c))).ToArray();
            s.Rows = Enumerable.Range(0, Math.Min(rows, MaxTableRowsListed))
                .Select(r => Enumerable.Range(0, listedColumns).Select(c => table.TextString(r, c) ?? "").ToArray())
                .ToArray();
        }

        private static Dictionary<string, string>? ReadAttributes(BlockReference insert, Transaction tr)
        {
            Dictionary<string, string>? values = null;
            foreach (ObjectId id in insert.AttributeCollection)
            {
                if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is AttributeReference attribute))
                {
                    continue;
                }

                values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (values.Count < MaxAttributesListed)
                {
                    values[attribute.Tag] = attribute.TextString;
                }
            }

            return values;
        }

        // ================================================================ text boxes

        /// <summary>
        /// The box around an entity's text, turned with the text (bottom-left, bottom-right,
        /// top-right, top-left), from the sizes BricsCAD reports. Null for anything else.
        /// </summary>
        private static double[][]? TextBoxOf(Entity ent)
        {
            try
            {
                switch (ent)
                {
                    case DBText text: return DBTextBox(text);
                    case MText mtext: return MTextBox(mtext);
                    case Dimension dimension: return DimensionTextBox(dimension);
                    default: return null;
                }
            }
            catch (System.Exception)
            {
                return null; // the picture falls back to an estimate
            }
        }

        private static double[][] DBTextBox(DBText text)
        {
            // Measure the text lying flat, then turn its box back to the text's angle.
            double rotation = text.Rotation;
            Point3d pivot = text.Position;
            Extents3d flat;
            if (Math.Abs(rotation) < 1e-9)
            {
                flat = text.GeometricExtents;
            }
            else
            {
                using (Entity copy = text.GetTransformedCopy(Matrix3d.Rotation(-rotation, Vector3d.ZAxis, pivot)))
                {
                    flat = copy.GeometricExtents;
                }
            }

            double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
            double[] Turn(double x, double y)
            {
                double dx = x - pivot.X, dy = y - pivot.Y;
                return new[] { Math.Round(pivot.X + dx * cos - dy * sin, 4), Math.Round(pivot.Y + dx * sin + dy * cos, 4) };
            }

            return new[]
            {
                Turn(flat.MinPoint.X, flat.MinPoint.Y), Turn(flat.MaxPoint.X, flat.MinPoint.Y),
                Turn(flat.MaxPoint.X, flat.MaxPoint.Y), Turn(flat.MinPoint.X, flat.MaxPoint.Y),
            };
        }

        private static double[][] MTextBox(MText mtext)
        {
            double width = 0, height = 0;
            try
            {
                width = mtext.ActualWidth;
                height = mtext.ActualHeight;
            }
            catch (System.Exception)
            {
                // Not every MText can be measured (e.g. a multileader's copy of its text).
            }

            if (width <= 0 || height <= 0)
            {
                (width, height) = AnnotationGeometry.Size(AnnotationGeometry.Lines(mtext.Text ?? "", mtext.TextHeight, mtext.Width), mtext.TextHeight);
            }

            double ax, ay;
            switch (mtext.Attachment)
            {
                case AttachmentPoint.TopCenter: (ax, ay) = (0.5, 1); break;
                case AttachmentPoint.TopRight: (ax, ay) = (1, 1); break;
                case AttachmentPoint.MiddleLeft: (ax, ay) = (0, 0.5); break;
                case AttachmentPoint.MiddleCenter: (ax, ay) = (0.5, 0.5); break;
                case AttachmentPoint.MiddleRight: (ax, ay) = (1, 0.5); break;
                case AttachmentPoint.BottomLeft: (ax, ay) = (0, 0); break;
                case AttachmentPoint.BottomCenter: (ax, ay) = (0.5, 0); break;
                case AttachmentPoint.BottomRight: (ax, ay) = (1, 0); break;
                default: (ax, ay) = (0, 1); break;
            }

            return AnnotationGeometry.ToArrays(AnnotationGeometry.Box(P2(mtext.Location), width, height, Deg(mtext.Rotation), ax, ay));
        }

        /// <summary>A dimension's text isn't an object of its own: its box is worked out from where the text sits and its size.</summary>
        private static double[][] DimensionTextBox(Dimension d)
        {
            double height = d.Dimtxt * (d.Dimscale > 0 ? d.Dimscale : 1);
            string text = string.IsNullOrEmpty(d.DimensionText) || d.DimensionText == "<>"
                ? DimensionValue(d).ToString("F" + Math.Max(0, Math.Min(8, d.Dimdec)).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                : d.DimensionText;
            (double width, double boxHeight) = AnnotationGeometry.Size(new[] { text }, height);

            double along = d is RotatedDimension linear ? Deg(linear.Rotation)
                : d is AlignedDimension aligned ? Deg(Math.Atan2(aligned.XLine2Point.Y - aligned.XLine1Point.Y, aligned.XLine2Point.X - aligned.XLine1Point.X))
                : 0;
            double rotation = AnnotationGeometry.Readable(along + Deg(d.TextRotation));
            return AnnotationGeometry.ToArrays(AnnotationGeometry.Box(P2(d.TextPosition), width, boxHeight, rotation, 0.5, 0.5));
        }
    }
}
