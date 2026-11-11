using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DFactory
{
    /// <summary>
    /// Node set_station_equations: put station equations on an alignment so its stationing
    /// follows a given chainage (e.g. the stations printed on an old construction drawing whose
    /// alignment is not the centreline polyline we have). Each equation is located either by a
    /// raw station or by a point (projected onto the alignment); the station ahead is the
    /// station wanted there. Sample lines and section view titles then show those stations.
    /// Memory only; save_dwg to persist.
    /// </summary>
    public static partial class Ops
    {
        static JsonNode RunNodeSetStationEquations(JsonObject a, Document doc)
        {
            string alName = Need(a, "alignment");
            var eqArr = a["equations"] as JsonArray;
            if (eqArr == null || eqArr.Count == 0)
                throw new InvalidOperationException("set_station_equations needs equations:[{ahead, raw_back? | x?,y?}].");
            bool clear = GetBool(a, "clear", true);
            bool dry = GetBool(a, "dry_run", false);
            double skipTol = GetDouble(a, "skip_tolerance", 0.001);
            // An object exactly at an equation (a sample line through the point) reports the
            // station BACK. Each equation is therefore put `lead` metres before its point with
            // ahead reduced by the same amount, so the point itself carries station = ahead.
            double lead = GetDouble(a, "lead", 0.01);
            if (lead < 0) throw new InvalidOperationException("lead must be >= 0.");

            Database db = doc.Database;
            var civ = Civ(db);
            int removed = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var al = FindAlignment(tr, civ, alName);
                if (al == null) throw new InvalidOperationException("Alignment '" + alName + "' not found.");
                if (clear && !dry)
                {
                    al.UpgradeOpen();
                    removed = al.StationEquations.Count;
                    for (int guard = removed; al.StationEquations.Count > 0; guard--)
                    {
                        if (guard <= 0) throw new InvalidOperationException("Could not remove existing station equations.");
                        al.StationEquations.Remove(al.StationEquations[al.StationEquations.Count - 1].RawStationBack);
                    }
                }
                tr.Commit();
            }

            var plan = new List<(double raw, double ahead, double offset)>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var al = FindAlignment(tr, civ, alName);
                if (!clear && al.StationEquations.Count > 0)
                    throw new InvalidOperationException("Alignment already has station equations; points cannot be resolved to raw stations. Use clear:true.");
                foreach (JsonNode n in eqArr)
                {
                    var o = n as JsonObject;
                    if (o == null || o["ahead"] == null) throw new InvalidOperationException("Each equation needs 'ahead'.");
                    double ahead = o["ahead"].GetValue<double>();
                    double raw, off = 0;
                    if (o["raw_back"] != null) raw = o["raw_back"].GetValue<double>();
                    else if (o["x"] != null && o["y"] != null)
                    {
                        raw = 0;
                        al.StationOffset(o["x"].GetValue<double>(), o["y"].GetValue<double>(), ref raw, ref off);
                    }
                    else throw new InvalidOperationException("Each equation needs raw_back or x,y.");
                    if (raw < al.StartingStation - 1e-6 || raw > al.EndingStation + 1e-6)
                        throw new InvalidOperationException("Equation at raw " + raw + " is outside the alignment.");
                    double l = Math.Min(lead, Math.Max(0, raw - al.StartingStation));
                    plan.Add((raw - l, ahead - l, off));
                }
                tr.Commit();
            }
            plan.Sort((p, q) => p.raw.CompareTo(q.raw));

            var outArr = new JsonArray();
            int added = 0, skipped = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var al = FindAlignment(tr, civ, alName);
                if (!dry) al.UpgradeOpen();
                double prevRaw = double.NegativeInfinity;
                foreach (var p in plan)
                {
                    var row = new JsonObject
                    {
                        ["raw_back"] = Math.Round(p.raw, 4),
                        ["ahead"] = p.ahead,
                        ["offset"] = Math.Round(p.offset, 3)
                    };
                    if (p.raw - prevRaw < 1e-3)
                        throw new InvalidOperationException("Two equations at the same raw station " + p.raw + ".");
                    prevRaw = p.raw;
                    if (added == 0 && Math.Abs(p.raw - p.ahead) < skipTol && Math.Abs(p.raw - al.StartingStation) < skipTol)
                    {
                        row["skipped"] = "start station already equals ahead";
                        skipped++;
                    }
                    else if (!dry)
                    {
                        al.StationEquations.Add(p.raw, p.ahead, StationEquationType.Increasing);
                        added++;
                    }
                    outArr.Add(row);
                }
                if (!dry) tr.Commit(); else tr.Abort();
            }

            var check = new JsonArray();
            if (!dry)
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var al = FindAlignment(tr, civ, alName);
                    foreach (StationEquation eq in al.StationEquations)
                        check.Add(new JsonObject
                        {
                            ["raw_back"] = Math.Round(eq.RawStationBack, 4),
                            ["back"] = Math.Round(eq.StationBack, 4),
                            ["ahead"] = Math.Round(eq.StationAhead, 4)
                        });
                    tr.Commit();
                }

            return new JsonObject
            {
                ["alignment"] = alName,
                ["dry_run"] = dry,
                ["removed"] = removed,
                ["added"] = added,
                ["skipped"] = skipped,
                ["equations"] = outArr,
                ["readback"] = check
            };
        }
    }
}
