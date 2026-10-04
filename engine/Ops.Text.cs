using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// </summary>
    public static partial class Ops
    {
        // ==================== textstyle_edit ====================

        static JsonNode TextStyleEdit(JsonObject a, Document doc)
        {
            var items = a["items"] as JsonArray;
            if (items == null || items.Count == 0)
                throw new InvalidOperationException(
                    "items is required: [{name,font?,bigfont?,width?,oblique?,height?,create?}].");
            bool dry = GetBool(a, "dry_run", true);
            string setCurrent = GetString(a, "set_current", null);

            Database db = doc.Database;
            var changed = new JsonArray();
            var failed = new JsonArray();
            string currentBefore = null, currentAfter = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId,
                    dry ? OpenMode.ForRead : OpenMode.ForWrite);

                foreach (JsonNode n in items)
                {
                    var o = n as JsonObject; if (o == null) continue;
                    string name = GetString(o, "name", null);
                    if (string.IsNullOrEmpty(name))
                    { failed.Add(new JsonObject { ["name"] = "(empty)", ["why"] = "Missing name" }); continue; }

                    bool exists = tst.Has(name);
                    bool create = GetBool(o, "create", false);
                    if (!exists && !create)
                    {
                        failed.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["why"] = "Text style does not exist; specify create:true to create it"
                        });
                        continue;
                    }

                    try
                    {
                        TextStyleTableRecord rec;
                        var before = new JsonObject();
                        if (exists)
                        {
                            rec = (TextStyleTableRecord)tr.GetObject(tst[name],
                                dry ? OpenMode.ForRead : OpenMode.ForWrite);
                            before["font"] = rec.FileName;
                            before["bigfont"] = rec.BigFontFileName;
                            before["width"] = Math.Round(rec.XScale, 4);
                            before["oblique_deg"] = Math.Round(rec.ObliquingAngle * 180.0 / Math.PI, 4);
                            before["height"] = Math.Round(rec.TextSize, 4);
                        }
                        else
                        {
                            before["font"] = "(new)";
                            rec = new TextStyleTableRecord { Name = name };
                        }

                        var after = new JsonObject();
                        string font = GetString(o, "font", null);
                        string bigfont = GetString(o, "bigfont", null);
                        bool hasWidth = o["width"] != null;
                        bool hasOblique = o["oblique"] != null;
                        bool hasHeight = o["height"] != null;
                        bool hasAnno = o["annotative"] != null;
                        bool wantAnno = GetBool(o, "annotative", false);
                        bool hasAnnoPO = o["paper_orientation"] != null;
                        bool wantAnnoPO = GetBool(o, "paper_orientation", false);
                        if (exists)
                        {
                            before["annotative"] = rec.Annotative == AnnotativeStates.True;
                            before["paper_orientation"] = ReadAnnoPaperOrientation(rec);
                        }
                        else
                        {
                            before["annotative"] = false;
                            before["paper_orientation"] = false;
                        }

                        if (!dry)
                        {
                            if (!exists)
                            {
                                tst.UpgradeOpen();
                                tst.Add(rec);
                                tr.AddNewlyCreatedDBObject(rec, true);
                            }
                            if (font != null) rec.FileName = font;
                            if (bigfont != null) rec.BigFontFileName = bigfont;
                            if (hasWidth) rec.XScale = GetDouble(o, "width", rec.XScale);
                            if (hasOblique) rec.ObliquingAngle =
                                GetDouble(o, "oblique", 0.0) * Math.PI / 180.0;
                            if (hasHeight) rec.TextSize = GetDouble(o, "height", rec.TextSize);
                            if (hasAnno)
                            {
                                try { rec.Annotative = wantAnno ? AnnotativeStates.True : AnnotativeStates.False; }
                                catch { }
                                if ((rec.Annotative == AnnotativeStates.True) != wantAnno)
                                    WriteFlagXData(db, tr, rec, "AcadAnnotative", wantAnno);
                            }
                            if (hasAnnoPO) WriteFlagXData(db, tr, rec, "AcadAnnoPO", wantAnnoPO);

                            after["font"] = rec.FileName;
                            after["bigfont"] = rec.BigFontFileName;
                            after["width"] = Math.Round(rec.XScale, 4);
                            after["oblique_deg"] = Math.Round(rec.ObliquingAngle * 180.0 / Math.PI, 4);
                            after["height"] = Math.Round(rec.TextSize, 4);
                            after["annotative"] = rec.Annotative == AnnotativeStates.True ||
                                                  ReadFlagXData(rec, "AcadAnnotative");
                            after["paper_orientation"] = ReadAnnoPaperOrientation(rec);
                        }
                        else
                        {
                            after["font"] = font ?? (exists ? (string)before["font"] : "(not specified)");
                            after["bigfont"] = bigfont ??
                                (exists ? (string)before["bigfont"] : "(not specified)");
                            after["width"] = hasWidth ? GetDouble(o, "width", 1.0)
                                : (exists ? before["width"].GetValue<double>() : 1.0);
                            after["oblique_deg"] = hasOblique ? GetDouble(o, "oblique", 0.0)
                                : (exists ? before["oblique_deg"].GetValue<double>() : 0.0);
                            after["height"] = hasHeight ? GetDouble(o, "height", 0.0)
                                : (exists ? before["height"].GetValue<double>() : 0.0);
                            after["annotative"] = hasAnno ? wantAnno
                                : before["annotative"].GetValue<bool>();
                            after["paper_orientation"] = hasAnnoPO ? wantAnnoPO
                                : before["paper_orientation"].GetValue<bool>();
                        }

                        changed.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["action"] = exists ? "edit" : "create",
                            ["before"] = before,
                            ["after"] = after,
                            ["applied"] = !dry
                        });
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 120)
                        });
                    }
                }

                try
                {
                    var cur = (TextStyleTableRecord)tr.GetObject(db.Textstyle, OpenMode.ForRead);
                    currentBefore = cur.Name;
                }
                catch { }
                if (!string.IsNullOrEmpty(setCurrent))
                {
                    if (!tst.Has(setCurrent))
                        failed.Add(new JsonObject
                        {
                            ["name"] = setCurrent,
                            ["why"] = "set_current refers to a missing text style"
                        });
                    else
                    {
                        if (!dry) db.Textstyle = tst[setCurrent];
                        currentAfter = setCurrent;
                    }
                }

                tr.Commit();
            }

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["changed"] = changed,
                ["failed"] = failed,
                ["current_textstyle_before"] = currentBefore,
                ["current_textstyle_after"] = currentAfter ?? currentBefore,
                ["note"] = "Memory only; save_dwg to persist. TTF typeface assignment still requires font normalization."
            };
        }


        static bool ReadFlagXData(DBObject rec, string app)
        {
            try
            {
                using (ResultBuffer rb = rec.GetXDataForApplication(app))
                {
                    if (rb == null) return false;
                    foreach (TypedValue tv in rb)
                        if (tv.TypeCode == (short)DxfCode.ExtendedDataInteger16)
                            return Convert.ToInt32(tv.Value) != 0;
                }
            }
            catch { }
            return false;
        }

        static bool ReadAnnoPaperOrientation(DBObject rec)
        {
            return ReadFlagXData(rec, "AcadAnnoPO");
        }

        static void WriteFlagXData(Database db, Transaction tr, DBObject rec, string app, bool on)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(app))
            {
                rat.UpgradeOpen();
                var ratr = new RegAppTableRecord { Name = app };
                rat.Add(ratr);
                tr.AddNewlyCreatedDBObject(ratr, true);
            }
            using (var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, app),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(on ? 1 : 0))))
            {
                rec.XData = rb;
            }
        }

        // ==================== texts_edit ====================

        static JsonNode TextsEdit(JsonObject a, Document doc)
        {
            var set = a["set"] as JsonObject;
            if (set == null || set.Count == 0)
                throw new InvalidOperationException(
                    "set is required: {style?,height?,width_factor?,layer?}, at least one property.");
            bool dry = GetBool(a, "dry_run", true);
            string where = (GetString(a, "where", "model") ?? "model").ToLowerInvariant();
            int max = (int)GetDouble(a, "max", 100000);
            int sampleMax = (int)GetDouble(a, "samples", 20);

            var match = a["match"] as JsonObject ?? new JsonObject();
            var wantStyles = StringSet(match, "styles");
            var wantLayers = StringSet(match, "layers");
            string textContains = GetString(match, "text_contains", null);
            bool hasMatchHeight = match["height"] != null;
            double matchHeight = GetDouble(match, "height", 0.0);
            double matchTol = GetDouble(match, "height_tol", 0.001);
            var wantTypes = StringSet(match, "types");   // DBText / MText / AttributeDefinition / AttributeReference

            string newStyle = GetString(set, "style", null);
            string newLayer = GetString(set, "layer", null);
            bool hasNewHeight = set["height"] != null;
            double newHeight = GetDouble(set, "height", 0.0);
            bool hasNewWf = set["width_factor"] != null;
            double newWf = GetDouble(set, "width_factor", 0.0);
            bool hasNewAnno = set["annotative"] != null;
            bool newAnno = GetBool(set, "annotative", false);

            Database db = doc.Database;
            int scanned = 0, matched = 0, applied = 0, ctxAdded = 0;
            var byBucket = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new JsonArray();
            var failed = new JsonArray();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                ObjectId styleId = ObjectId.Null;
                if (!string.IsNullOrEmpty(newStyle))
                {
                    if (!tst.Has(newStyle))
                        throw new InvalidOperationException("Text style not found in drawing: '" + newStyle +
                            "'; create it with textstyle_edit or import it from your template.");
                    styleId = tst[newStyle];
                }
                ObjectId layerId = ObjectId.Null;
                if (!string.IsNullOrEmpty(newLayer))
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(newLayer))
                        throw new InvalidOperationException("Layer not found: '" + newLayer +
                            "'; create it with layers_edit first.");
                    layerId = lt[newLayer];
                }

                foreach (var pair in TextHosts(db, tr, where))
                {
                    string hostName = pair.Key;
                    BlockTableRecord btr = pair.Value;
                    foreach (ObjectId id in btr)
                    {
                        if (matched >= max) break;
                        DBObject o;
                        try { o = tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                        string tn = o.GetType().Name;
                        bool isText = tn == "DBText" || tn == "MText" ||
                                      tn == "AttributeDefinition" || tn == "AttributeReference";
                        if (!isText) continue;
                        scanned++;
                        if (wantTypes != null && !wantTypes.Contains(tn)) continue;

                        var ent = (Entity)o;
                        string curStyle = TextStyleNameOf(tr, o);
                        double curHeight = TextHeightOf(o);
                        string content = TextContentOf(o);

                        if (wantStyles != null && !wantStyles.Contains(curStyle ?? "")) continue;
                        if (wantLayers != null && !wantLayers.Contains(ent.Layer ?? "")) continue;
                        if (hasMatchHeight && Math.Abs(curHeight - matchHeight) > matchTol) continue;
                        if (!string.IsNullOrEmpty(textContains) &&
                            (content == null ||
                             content.IndexOf(textContains, StringComparison.Ordinal) < 0)) continue;

                        matched++;
                        string bucket = hostName + " | " + tn + " | " + (curStyle ?? "?") +
                                        " | h=" + Math.Round(curHeight, 3).ToString();
                        int c; byBucket.TryGetValue(bucket, out c); byBucket[bucket] = c + 1;
                        if (samples.Count < sampleMax)
                            samples.Add(new JsonObject
                            {
                                ["host"] = hostName,
                                ["type"] = tn,
                                ["handle"] = o.Handle.ToString(),
                                ["layer"] = ent.Layer,
                                ["style"] = curStyle,
                                ["height"] = Math.Round(curHeight, 4),
                                ["text"] = Truncate(content ?? "", 40)
                            });

                        if (dry) continue;
                        try
                        {
                            o.UpgradeOpen();
                            if (!styleId.IsNull) SetTextStyle(o, styleId);
                            if (hasNewHeight) SetTextHeight(o, newHeight);
                            if (hasNewWf)
                            {
                                var dbt = o as DBText;
                                if (dbt != null) dbt.WidthFactor = newWf;
                            }
                            if (!layerId.IsNull) ent.LayerId = layerId;
                            if (hasNewAnno)
                            {
                                o.Annotative = newAnno
                                    ? AnnotativeStates.True : AnnotativeStates.False;
                                if (newAnno && o.Annotative == AnnotativeStates.True) ctxAdded++;
                            }
                            applied++;
                        }
                        catch (System.Exception ex)
                        {
                            failed.Add(new JsonObject
                            {
                                ["handle"] = o.Handle.ToString(),
                                ["why"] = ex.GetType().Name + ": " + Truncate(ex.Message, 100)
                            });
                        }
                    }
                }
                tr.Commit();
            }

            var buckets = new JsonArray();
            foreach (var kv in byBucket)
                buckets.Add(new JsonObject { ["group"] = kv.Key, ["count"] = kv.Value });

            return new JsonObject
            {
                ["dry_run"] = dry,
                ["where"] = where,
                ["scanned_texts"] = scanned,
                ["matched"] = matched,
                ["applied"] = applied,
                ["groups"] = buckets,
                ["samples"] = samples,
                ["failed"] = failed,
                ["marked_annotative"] = ctxAdded,
                ["current_annotation_scale"] = CurrentScaleName(db),
                ["note"] = "Memory only; save_dwg to persist. Set CANNOSCALE using set_scale before set.annotative."
            };
        }



        static string CurrentScaleName(Database db)
        {
            try { var c = db.Cannoscale; return c == null ? null : c.Name; }
            catch { return null; }
        }

        static IEnumerable<KeyValuePair<string, BlockTableRecord>> TextHosts(
            Database db, Transaction tr, string where)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool wantModel = where == "model" || where == "all";
            bool wantLayouts = where == "layouts" || where == "all";
            bool wantBlocks = where == "blocks" || where == "all";
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                bool isModel = string.Equals(btr.Name, BlockTableRecord.ModelSpace,
                    StringComparison.OrdinalIgnoreCase);
                bool take = btr.IsLayout ? (isModel ? wantModel : wantLayouts) : wantBlocks;
                if (!take) continue;
                yield return new KeyValuePair<string, BlockTableRecord>(
                    isModel ? "Model" : btr.Name, btr);
            }
        }

        static HashSet<string> StringSet(JsonObject a, string key)
        {
            var arr = a[key] as JsonArray;
            if (arr == null || arr.Count == 0) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode n in arr) if (n != null) set.Add(n.ToString());
            return set;
        }

        static string TextStyleNameOf(Transaction tr, DBObject o)
        {
            ObjectId sid = ObjectId.Null;
            var dbt = o as DBText;
            if (dbt != null) sid = dbt.TextStyleId;
            var mt = o as MText;
            if (mt != null) sid = mt.TextStyleId;
            if (sid.IsNull) return null;
            try
            {
                var rec = (TextStyleTableRecord)tr.GetObject(sid, OpenMode.ForRead);
                return rec.Name;
            }
            catch { return null; }
        }

        static double TextHeightOf(DBObject o)
        {
            var dbt = o as DBText;
            if (dbt != null) return dbt.Height;
            var mt = o as MText;
            if (mt != null) return mt.TextHeight;
            return 0.0;
        }

        static string TextContentOf(DBObject o)
        {
            var dbt = o as DBText;
            if (dbt != null) return dbt.TextString;
            var mt = o as MText;
            if (mt != null) return mt.Contents;
            return null;
        }

        static void SetTextStyle(DBObject o, ObjectId styleId)
        {
            var dbt = o as DBText;
            if (dbt != null) { dbt.TextStyleId = styleId; return; }
            var mt = o as MText;
            if (mt != null) mt.TextStyleId = styleId;
        }

        static void SetTextHeight(DBObject o, double h)
        {
            if (h <= 0) throw new InvalidOperationException("Text height must be positive.");
            var dbt = o as DBText;
            if (dbt != null) { dbt.Height = h; return; }
            var mt = o as MText;
            if (mt != null) mt.TextHeight = h;
        }
    }
}
