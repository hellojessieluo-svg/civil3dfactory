using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CivDoc = Autodesk.Civil.ApplicationServices.CivilDocument;
using CivAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

namespace Civil3DFactory
{
    public static partial class Ops
    {
        /// <summary>
        ///
        /// </summary>
        static JsonNode RunNodeSetProfileViewHatch(JsonObject a, Document doc)
        {
            bool dry = GetBool(a, "dry_run", true);
            string cutStyle = GetString(a, "cut_style", null);
            string fillStyle = GetString(a, "fill_style", null);
            bool clear = GetBool(a, "clear", true);
            var only = new HashSet<string>();
            if (a["views"] is JsonArray va) foreach (var v in va) only.Add(v.GetValue<string>());
            if (!dry && string.IsNullOrEmpty(cutStyle) && string.IsNullOrEmpty(fillStyle))
                throw new InvalidOperationException("Editing (dry_run:false) requires cut_style or fill_style.");

            Database db = doc.Database;
            CivDoc civ = Civ(db);
            var views = new JsonArray();
            JsonObject members = null;
            int added = 0, cleared = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId cutId = string.IsNullOrEmpty(cutStyle) ? ObjectId.Null : FindStyleIdStrict(tr, civ.Styles.ShapeStyles, cutStyle);
                ObjectId fillId = string.IsNullOrEmpty(fillStyle) ? ObjectId.Null : FindStyleIdStrict(tr, civ.Styles.ShapeStyles, fillStyle);
                foreach (ObjectId id in ModelSpace(db, tr))
                {
                    CivProfileView pv;
                    try { pv = tr.GetObject(id, OpenMode.ForRead) as CivProfileView; } catch { continue; }
                    if (pv == null) continue;
                    if (only.Count > 0 && !only.Contains(pv.Name)) continue;
                    var info = new JsonObject { ["view"] = pv.Name, ["handle"] = pv.Handle.ToString() };
                    CivAlignment al = null;
                    try { al = tr.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivAlignment; } catch { }
                    if (al == null) { info["error"] = "View has no alignment"; views.Add(info); continue; }
                    info["alignment"] = al.Name;
                    var egs = new List<CivProfile>(); var fgs = new List<CivProfile>();
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        var p = tr.GetObject(pid, OpenMode.ForRead) as CivProfile; if (p == null) continue;
                        string pt = ""; try { pt = p.ProfileType.ToString(); } catch { }
                        if (pt.Equals("EG", StringComparison.OrdinalIgnoreCase)) egs.Add(p); else if (pt.Equals("FG", StringComparison.OrdinalIgnoreCase)) fgs.Add(p);
                    }
                    CivProfile ground = egs.FirstOrDefault(p => p.Name.Contains("Ground")) ?? egs.FirstOrDefault();
                    CivProfile design = fgs.FirstOrDefault(p => p.Name.Contains("Design")) ?? fgs.FirstOrDefault();
                    info["ground"] = ground?.Name; info["design"] = design?.Name;
                    object coll = null;
                    try { coll = pv.HatchAreas; } catch (Exception ex) { info["hatch_areas_error"] = ex.Message; }
                    if (coll == null) { views.Add(info); continue; }
                    if (members == null) members = DescribeMembers(coll);
                    int before = CountOf(coll);
                    info["hatch_areas_before"] = before;
                    if (!dry && ground != null && design != null)
                    {
                        pv.UpgradeOpen();
                        if (clear && before > 0) { cleared += ClearAll(coll, before); }
                        var log = new JsonArray();
                        if (!cutId.IsNull) { log.Add(AddArea(coll, "C3DF-Cut", ground.ObjectId, design.ObjectId, cutId)); added++; }
                        if (!fillId.IsNull) { log.Add(AddArea(coll, "C3DF-Fill", design.ObjectId, ground.ObjectId, fillId)); added++; }
                        info["applied"] = log;
                        info["hatch_areas_after"] = CountOf(coll);
                    }
                    views.Add(info);
                }
                tr.Commit();
            }
            return new JsonObject { ["dry_run"] = dry, ["views"] = views, ["areas_added"] = added, ["areas_cleared"] = cleared, ["members"] = members };
        }

        static JsonObject DescribeMembers(object coll)
        {
            var o = new JsonObject { ["collection_type"] = coll.GetType().FullName };
            var ms = new JsonArray();
            foreach (var m in coll.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (!m.IsSpecialName && m.DeclaringType != typeof(object))
                    ms.Add(m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ") : " + m.ReturnType.Name);
            o["methods"] = ms;
            var ps = new JsonArray();
            foreach (var p in coll.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) ps.Add(p.Name + " : " + p.PropertyType.Name);
            o["properties"] = ps;
            return o;
        }

        static int CountOf(object coll)
        {
            var p = coll.GetType().GetProperty("Count");
            if (p != null) { try { return Convert.ToInt32(p.GetValue(coll)); } catch { } }
            int n = 0; if (coll is IEnumerable e) foreach (var _ in e) n++;
            return n;
        }

        static int ClearAll(object coll, int count)
        {
            var names = new List<string>();
            if (coll is IEnumerable e)
                foreach (var x in e)
                {
                    var np = x.GetType().GetProperty("Name") ?? x.GetType().GetProperty("HatchAreaName");
                    if (np != null) names.Add(Convert.ToString(np.GetValue(x)));
                }
            var rm = coll.GetType().GetMethod("Remove", new[] { typeof(string) });
            if (rm == null) throw new InvalidOperationException("Hatch area collection has no Remove(string); inspect members in result");
            foreach (var n in names) rm.Invoke(coll, new object[] { n });
            return names.Count;
        }

        static JsonObject AddArea(object coll, string name, ObjectId upper, ObjectId lower, ObjectId styleId)
        {
            var add = coll.GetType().GetMethod("Add", new[] { typeof(string), typeof(ObjectId), typeof(ObjectId), typeof(ObjectId) });
            if (add == null) throw new InvalidOperationException("Hatch area collection has no Add(string, ObjectId, ObjectId, ObjectId); inspect members");
            add.Invoke(coll, new object[] { name, upper, lower, styleId });
            return new JsonObject { ["name"] = name, ["upper"] = upper.Handle.ToString(), ["lower"] = lower.Handle.ToString(), ["style"] = styleId.Handle.ToString() };
        }
    }
}
