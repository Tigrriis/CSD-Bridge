using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Bricscad.ApplicationServices;
using CsdBridge.Core.Vehicles;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace CsdBridge.BricsCAD
{
    /// <summary>
    /// Keeps each turning path's inputs in the drawing so AITURNEDIT can regenerate it. Every
    /// entity drawn for a manoeuvre carries its id as extended data, and the manoeuvre itself
    /// (a <see cref="ManoeuvreRecord"/> as JSON) is an Xrecord in a dictionary of the drawing.
    /// </summary>
    internal static class ManoeuvreStore
    {
        private const string App = "CSDBRIDGE_TURN";
        private const string DictionaryName = "CSDBRIDGE_TURNPATHS";
        private const int Chunk = 240;

        // DXF group codes: extended data application name and string; Xrecord text.
        private const int XDataApp = 1001;
        private const int XDataString = 1000;
        private const int RecordText = 1;

        /// <summary>The block a manoeuvre was picked by, when it was drawn as one.</summary>
        public sealed class PickedBlock
        {
            public ObjectId Id { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Rotation { get; set; }
        }

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 16);

        /// <summary>Tags the manoeuvre's entities and saves (or replaces) its record.</summary>
        public static void Save(Document doc, ManoeuvreRecord r)
        {
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureApp(tr, db);
                IEnumerable<string> handles = r.BlockHandle == null ? r.Handles : r.Handles.Concat(new[] { r.BlockHandle });
                foreach (string handle in handles)
                {
                    ObjectId id = Resolve(db, handle);
                    if (!id.IsNull && tr.GetObject(id, OpenMode.ForWrite) is DBObject obj)
                    {
                        obj.XData = new ResultBuffer(
                            new TypedValue(XDataApp, App),
                            new TypedValue(XDataString, r.Id));
                    }
                }

                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                DBDictionary dict;
                if (nod.Contains(DictionaryName))
                {
                    dict = (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), OpenMode.ForWrite);
                }
                else
                {
                    nod.UpgradeOpen();
                    dict = new DBDictionary();
                    nod.SetAt(DictionaryName, dict);
                    tr.AddNewlyCreatedDBObject(dict, true);
                }

                string json = r.ToJson();
                var values = new List<TypedValue>();
                for (int i = 0; i < json.Length; i += Chunk)
                {
                    values.Add(new TypedValue(RecordText, json.Substring(i, Math.Min(Chunk, json.Length - i))));
                }

                var data = new ResultBuffer(values.ToArray());
                if (dict.Contains(r.Id))
                {
                    var existing = (Xrecord)tr.GetObject(dict.GetAt(r.Id), OpenMode.ForWrite);
                    existing.Data = data;
                }
                else
                {
                    var record = new Xrecord { Data = data };
                    dict.SetAt(r.Id, record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }

                tr.Commit();
            }
        }

        /// <summary>The manoeuvre the picked entity belongs to, or null with the reason.</summary>
        public static ManoeuvreRecord? Find(Document doc, ObjectId picked, out PickedBlock? block, out string? problem)
        {
            block = null;
            problem = null;
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(picked, OpenMode.ForRead) is Entity ent))
                {
                    problem = "That isn't a drawing entity.";
                    return null;
                }

                string? id = IdOf(ent);
                if (id == null)
                {
                    problem = "That object wasn't drawn by AITURN, or was drawn before turning paths could be edited. Draw it again with AITURN.";
                    return null;
                }

                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(DictionaryName))
                {
                    problem = "This drawing has no saved turning paths.";
                    return null;
                }

                var dict = (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), OpenMode.ForRead);
                if (!dict.Contains(id))
                {
                    problem = "That turning path's settings are no longer in the drawing (it may have been copied from another drawing).";
                    return null;
                }

                var record = (Xrecord)tr.GetObject(dict.GetAt(id), OpenMode.ForRead);
                var json = new StringBuilder();
                using (ResultBuffer data = record.Data)
                {
                    if (data != null)
                    {
                        foreach (TypedValue v in data)
                        {
                            if (v.TypeCode == RecordText && v.Value is string text)
                            {
                                json.Append(text);
                            }
                        }
                    }
                }

                if (ent is BlockReference br)
                {
                    Point3d pos = br.Position;
                    block = new PickedBlock { Id = picked, X = pos.X, Y = pos.Y, Rotation = br.Rotation };
                }

                tr.Commit();
                try
                {
                    return ManoeuvreRecord.FromJson(json.ToString());
                }
                catch (System.Exception ex)
                {
                    problem = "That turning path's saved settings can't be read: " + ex.Message;
                    return null;
                }
            }
        }

        /// <summary>
        /// Erases what a manoeuvre drew. For a block: the picked reference (or the one saved), and the
        /// block's definition once nothing else uses it. Otherwise every entity still tagged with its id.
        /// </summary>
        public static void Erase(Document doc, ManoeuvreRecord r, PickedBlock? picked)
        {
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (r.BlockName != null)
                {
                    ObjectId reference = picked != null ? picked.Id : (r.BlockHandle == null ? ObjectId.Null : Resolve(db, r.BlockHandle));
                    EraseIfTagged(tr, reference, r.Id);
                    var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (blocks.Has(r.BlockName))
                    {
                        var definition = (BlockTableRecord)tr.GetObject(blocks[r.BlockName], OpenMode.ForRead);
                        bool stillUsed = false;
                        foreach (ObjectId id in definition.GetBlockReferenceIds(true, false))
                        {
                            if (!id.IsNull && !id.IsErased)
                            {
                                stillUsed = true;
                            }
                        }

                        if (!stillUsed)
                        {
                            definition.UpgradeOpen();
                            definition.Erase();
                        }
                    }
                }
                else
                {
                    foreach (string handle in r.Handles)
                    {
                        EraseIfTagged(tr, Resolve(db, handle), r.Id);
                    }
                }

                tr.Commit();
            }
        }

        private static void EraseIfTagged(Transaction tr, ObjectId id, string manoeuvreId)
        {
            if (id.IsNull || id.IsErased)
            {
                return;
            }

            if (tr.GetObject(id, OpenMode.ForWrite) is DBObject obj && IdOf(obj) == manoeuvreId)
            {
                obj.Erase();
            }
        }

        private static string? IdOf(DBObject obj)
        {
            try
            {
                using (ResultBuffer data = obj.GetXDataForApplication(App))
                {
                    if (data == null)
                    {
                        return null;
                    }

                    foreach (TypedValue v in data)
                    {
                        if (v.TypeCode == XDataString && v.Value is string id)
                        {
                            return id;
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                // No such application in this drawing: not one of ours.
            }

            return null;
        }

        private static void EnsureApp(Transaction tr, Database db)
        {
            var apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (apps.Has(App))
            {
                return;
            }

            apps.UpgradeOpen();
            var app = new RegAppTableRecord { Name = App };
            apps.Add(app);
            tr.AddNewlyCreatedDBObject(app, true);
        }

        private static ObjectId Resolve(Database db, string hex)
        {
            try
            {
                var handle = new Handle(Convert.ToInt64(hex, 16));
                return db.TryGetObjectId(handle, out ObjectId id) && !id.IsErased ? id : ObjectId.Null;
            }
            catch (FormatException)
            {
                return ObjectId.Null;
            }
        }
    }
}
