using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Grid earthwork balance core (single source of truth): scatter a grid over the volume surface -> cell-to-cell transportation problem (exact minimum haul distance x volume)
    /// -> haul-distance histogram + aggregated haul arrows.
    ///
    /// Shared by two entry points: factory node grid_earthwork_balance (AI via civil3dfactory.ps1) and
    /// WaterBox command C3DF-GridBalance/PH (manual use by colleagues). Algorithm changes go here only.
    ///
    /// Conventions:
    ///  - sampled dz = comparison surface (design) - base surface (existing); dz&lt;0 cut, dz&gt;0 fill (same as bounded_volumes);
    ///  - statistics cells (fine) carry volumes and colour blocks, solver cells (coarse) carry the hauls -- the volume truth is balanced to the TIN, cell size only affects haul-distance resolution;
    ///  - the cut/fill imbalance goes to a virtual node (big-M distance) that only absorbs the remainder; it enters neither the histogram nor the arrows.
    /// </summary>
    public static class GridBalanceCore
    {
        public struct Cell
        {
            public double X, Y, Vol;   // Vol is always positive; cut and fill are kept in separate lists
            public Cell(double x, double y, double v) { X = x; Y = y; Vol = v; }
        }

        public sealed class Flow
        {
            public double SX, SY, TX, TY, Vol, Dist;
        }

        public sealed class Result
        {
            public List<Cell> CutCells = new List<Cell>();   // fine cells (after balancing)
            public List<Cell> FillCells = new List<Cell>();
            public int CellsInside, CellsOffSurface, CellsNearZero;
            public double GridCut, GridFill;                 // grid integral before balancing
            public double TinCut, TinFill;                   // GetBoundedVolumes truth (passed in by the caller)
            public double ClosureCutPct, ClosureFillPct;     // |grid-tin|/tin ×100
            public double SolverStep;                        // solver cell size (may be coarser than the statistics cell size)
            public List<Flow> Flows = new List<Flow>();      // real hauls (virtual excluded)
            public double InternalMoved, AvgDist, Shortfall, Surplus, ObjVolDist;
            public double[] BandEdges = new double[0]; public double[] BandVols = new double[0];
            public long SolveMs;
        }

        // ---------------- 1. Scatter the grid ----------------

        /// <summary>Fine cells whose centre falls inside the ring. sampler returns dz (NaN = off surface).</summary>
        public static Result BuildCells(IList<Point2d> ring, double step,
                                        Func<double, double, double> sampler,
                                        double minDepth = 0.01)
        {
            if (ring == null || ring.Count < 3) throw new InvalidOperationException("Boundary ring has fewer than 3 points.");
            if (step <= 0) throw new InvalidOperationException("Cell size must be greater than 0.");
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (Point2d p in ring)
            {
                if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y;
            }
            var r = new Result();
            double cellArea = step * step;
            int nx = (int)Math.Ceiling((x1 - x0) / step), ny = (int)Math.Ceiling((y1 - y0) / step);
            if ((long)nx * ny > 4_000_000)
                throw new InvalidOperationException("Bounding box " + nx + "x" + ny + " has too many cells; increase the cell size.");
            for (int i = 0; i < nx; i++)
            {
                double cx = x0 + (i + 0.5) * step;
                for (int j = 0; j < ny; j++)
                {
                    double cy = y0 + (j + 0.5) * step;
                    if (!PointInRing(ring, cx, cy)) continue;
                    r.CellsInside++;
                    double dz = sampler(cx, cy);
                    if (double.IsNaN(dz)) { r.CellsOffSurface++; continue; }
                    if (Math.Abs(dz) < minDepth) { r.CellsNearZero++; continue; }
                    double v = Math.Abs(dz) * cellArea;
                    if (dz < 0) { r.CutCells.Add(new Cell(cx, cy, v)); r.GridCut += v; }
                    else { r.FillCells.Add(new Cell(cx, cy, v)); r.GridFill += v; }
                }
            }
            return r;
        }

        public static bool PointInRing(IList<Point2d> ring, double px, double py)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
                if ((yi > py) != (yj > py) &&
                    px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Balance the grid integral to the TIN truth (proportional scaling, spatial distribution preserved).</summary>
        public static void Reconcile(Result r, double tinCut, double tinFill)
        {
            r.TinCut = tinCut; r.TinFill = tinFill;
            r.ClosureCutPct = tinCut > 1e-9 ? Math.Abs(r.GridCut - tinCut) / tinCut * 100 : 0;
            r.ClosureFillPct = tinFill > 1e-9 ? Math.Abs(r.GridFill - tinFill) / tinFill * 100 : 0;
            if (r.GridCut > 1e-9 && tinCut > 1e-9)
            {
                double k = tinCut / r.GridCut;
                for (int i = 0; i < r.CutCells.Count; i++)
                { var c = r.CutCells[i]; r.CutCells[i] = new Cell(c.X, c.Y, c.Vol * k); }
            }
            if (r.GridFill > 1e-9 && tinFill > 1e-9)
            {
                double k = tinFill / r.GridFill;
                for (int i = 0; i < r.FillCells.Count; i++)
                { var c = r.FillCells[i]; r.FillCells[i] = new Cell(c.X, c.Y, c.Vol * k); }
            }
        }

        // ---------------- 2. Coarsen the solver grid ----------------

        /// <summary>When cut+fill nodes exceed maxNodes, coarsen by an integer factor; returns the solver cell size.</summary>
        public static double Coarsen(Result r, double step, int maxNodes,
                                     out List<Cell> cut, out List<Cell> fill)
        {
            int total = r.CutCells.Count + r.FillCells.Count;
            int f = 1;
            while (total > (long)maxNodes * f * f) f++;
            if (f == 1) { cut = r.CutCells; fill = r.FillCells; return step; }
            double s2 = step * f;
            cut = Merge(r.CutCells, s2); fill = Merge(r.FillCells, s2);
            return s2;
        }

        static List<Cell> Merge(List<Cell> cells, double s)
        {
            var d = new Dictionary<long, double[]>();   // key → [Σv, Σvx, Σvy]
            foreach (var c in cells)
            {
                long kx = (long)Math.Floor(c.X / s), ky = (long)Math.Floor(c.Y / s);
                long key = kx * 4_000_003L + ky;
                if (!d.TryGetValue(key, out var a)) { a = new double[3]; d[key] = a; }
                a[0] += c.Vol; a[1] += c.Vol * c.X; a[2] += c.Vol * c.Y;
            }
            var outp = new List<Cell>(d.Count);
            foreach (var a in d.Values)
                if (a[0] > 1e-9) outp.Add(new Cell(a[1] / a[0], a[2] / a[0], a[0]));
            return outp;
        }

        // ---------------- 3. Transportation problem (SSP min-cost flow, exact) ----------------

        /// <summary>
        /// supply = cut cells, demand = fill cells; the deficit side gets a big-M virtual node that absorbs the remainder (not in the result Flows).
        /// Successive shortest paths + potentials, dense Dijkstra; size is controlled by Coarsen.
        /// </summary>
        public static void Solve(Result r, List<Cell> cut, List<Cell> fill)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int m = cut.Count, n = fill.Count;
            if (m == 0 || n == 0)
            {
                r.InternalMoved = 0; r.AvgDist = 0;
                r.Shortfall = m == 0 ? SumV(fill) : 0;
                r.Surplus = n == 0 ? SumV(cut) : 0;
                r.SolveMs = sw.ElapsedMilliseconds; return;
            }

            double totC = SumV(cut), totF = SumV(fill);
            // distance upper bound -> big M
            double maxD = 0;
            var sx = new double[m + 1]; var sy = new double[m + 1]; var sv = new double[m + 1];
            var tx = new double[n + 1]; var ty = new double[n + 1]; var tv = new double[n + 1];
            for (int i = 0; i < m; i++) { sx[i] = cut[i].X; sy[i] = cut[i].Y; sv[i] = cut[i].Vol; }
            for (int j = 0; j < n; j++) { tx[j] = fill[j].X; ty[j] = fill[j].Y; tv[j] = fill[j].Vol; }
            for (int i = 0; i < m; i++)
                for (int j = 0; j < n; j++)
                {
                    double dd = Dist(sx[i], sy[i], tx[j], ty[j]);
                    if (dd > maxD) maxD = dd;
                }
            double bigM = (maxD + 1) * 10;

            int M = m, N = n;   // sizes including the virtual node
            bool vSrc = false, vSnk = false;
            if (totF > totC + 1e-6) { vSrc = true; sv[m] = totF - totC; M = m + 1; r.Shortfall = totF - totC; }
            else if (totC > totF + 1e-6) { vSnk = true; tv[n] = totC - totF; N = n + 1; r.Surplus = totC - totF; }

            // distance function (virtual nodes always bigM)
            Func<int, int, double> cost = (i, j) =>
                (vSrc && i == m) || (vSnk && j == n) ? bigM : Dist(sx[i], sy[i], tx[j], ty[j]);

            int V = M + N;                       // nodes: 0..M-1 sources, M..M+N-1 sinks
            var pot = new double[V];
            var remS = new double[M]; Array.Copy(sv, remS, M);
            var remT = new double[N]; Array.Copy(tv, remT, N);
            // sparse flow storage: one j->flow dictionary per source
            var flow = new Dictionary<int, double>[M];
            for (int i = 0; i < M; i++) flow[i] = new Dictionary<int, double>();

            var dist = new double[V]; var prev = new int[V]; var done = new bool[V];
            double remainTotal = Math.Max(totC, totF);
            int guard = 0, guardMax = (M + N) * 40;

            while (remainTotal > 1e-6)
            {
                if (++guard > guardMax) throw new InvalidOperationException("Solver iteration limit exceeded (" + guardMax + "), aborted.");
                for (int k = 0; k < V; k++) { dist[k] = double.MaxValue; prev[k] = -1; done[k] = false; }
                for (int i = 0; i < M; i++) if (remS[i] > 1e-9) dist[i] = 0;

                // dense Dijkstra (reduced costs)
                for (int it = 0; it < V; it++)
                {
                    int u = -1; double best = double.MaxValue;
                    for (int k = 0; k < V; k++) if (!done[k] && dist[k] < best) { best = dist[k]; u = k; }
                    if (u < 0) break;
                    done[u] = true;
                    if (u < M)
                    {   // source u -> all sinks (forward arcs, infinite capacity)
                        for (int j = 0; j < N; j++)
                        {
                            double rc = cost(u, j) + pot[u] - pot[M + j];
                            if (rc < -1e-7) rc = 0;   // numerical noise
                            double nd = dist[u] + rc;
                            if (nd < dist[M + j] - 1e-12) { dist[M + j] = nd; prev[M + j] = u; }
                        }
                    }
                    else
                    {   // sink u-M -> sources with flow (reverse arcs)
                        int j = u - M;
                        for (int i = 0; i < M; i++)
                        {
                            double f0;
                            if (!flow[i].TryGetValue(j, out f0) || f0 <= 1e-9) continue;
                            double rc = -cost(i, j) + pot[u] - pot[i];
                            if (rc < -1e-7) rc = 0;
                            double nd = dist[u] + rc;
                            if (nd < dist[i] - 1e-12) { dist[i] = nd; prev[i] = M + j; }
                        }
                    }
                }

                // pick the nearest sink with a deficit
                int target = -1; double bestT = double.MaxValue;
                for (int j = 0; j < N; j++)
                    if (remT[j] > 1e-9 && dist[M + j] < bestT) { bestT = dist[M + j]; target = M + j; }
                if (target < 0) throw new InvalidOperationException("Deficit remains but no reachable path -- should not happen.");

                // trace back the path and find the bottleneck
                double push = remT[target - M];
                int node = target;
                while (true)
                {
                    int p = prev[node];
                    if (p < 0) { if (node < M) push = Math.Min(push, remS[node]); break; }
                    if (node >= M) { /* source->sink forward arc, unbounded */ }
                    else
                    {   // sink->source reverse arc, limited by existing flow
                        int j = p - M;
                        push = Math.Min(push, flow[node][j]);
                    }
                    node = p;
                }
                if (push <= 1e-9) throw new InvalidOperationException("Augmenting amount is 0 -- numerical anomaly.");

                // apply
                node = target;
                while (true)
                {
                    int p = prev[node];
                    if (p < 0) { remS[node] -= push; break; }
                    if (node >= M)
                    {
                        int j = node - M; double f0;
                        flow[p].TryGetValue(j, out f0); flow[p][j] = f0 + push;
                    }
                    else
                    {
                        int j = p - M;
                        flow[node][j] -= push;
                    }
                    node = p;
                }
                remT[target - M] -= push;
                remainTotal -= push;

                // update potentials
                for (int k = 0; k < V; k++)
                    if (dist[k] < double.MaxValue) pot[k] += Math.Min(dist[k], bestT);
                    else pot[k] += bestT;
            }

            // summarise (virtual excluded)
            double moved = 0, vd = 0;
            for (int i = 0; i < m; i++)
                foreach (var kv in flow[i])
                {
                    int j = kv.Key; double v = kv.Value;
                    if (v <= 1e-6 || (vSnk && j == n)) continue;
                    double dd = Dist(sx[i], sy[i], tx[j], ty[j]);
                    r.Flows.Add(new Flow { SX = sx[i], SY = sy[i], TX = tx[j], TY = ty[j], Vol = v, Dist = dd });
                    moved += v; vd += v * dd;
                }
            r.InternalMoved = moved;
            r.ObjVolDist = vd;
            r.AvgDist = moved > 1e-9 ? vd / moved : 0;
            r.SolveMs = sw.ElapsedMilliseconds;
        }

        static double SumV(List<Cell> c) { double s = 0; foreach (var x in c) s += x.Vol; return s; }
        static double Dist(double ax, double ay, double bx, double by)
        { double dx = ax - bx, dy = ay - by; return Math.Sqrt(dx * dx + dy * dy); }

        // ---------------- 4. Histogram and arrow aggregation ----------------

        public static void Histogram(Result r, double[] edges)
        {
            r.BandEdges = edges;
            r.BandVols = new double[edges.Length + 1];
            foreach (var f in r.Flows)
            {
                int b = 0;
                while (b < edges.Length && f.Dist > edges[b]) b++;
                r.BandVols[b] += f.Vol;
            }
        }

        /// <summary>Aggregate by the display cell of source / sink; largest volumes first, truncated to maxArrows.</summary>
        public static List<Flow> AggregateArrows(Result r, double displayStep, int maxArrows)
        {
            var d = new Dictionary<string, double[]>();  // [Σv, Σv·sx, Σv·sy, Σv·tx, Σv·ty, Σv·dist]
            foreach (var f in r.Flows)
            {
                long a1 = (long)Math.Floor(f.SX / displayStep), a2 = (long)Math.Floor(f.SY / displayStep);
                long b1 = (long)Math.Floor(f.TX / displayStep), b2 = (long)Math.Floor(f.TY / displayStep);
                string key = a1 + "_" + a2 + "|" + b1 + "_" + b2;
                if (!d.TryGetValue(key, out var a)) { a = new double[6]; d[key] = a; }
                a[0] += f.Vol; a[1] += f.Vol * f.SX; a[2] += f.Vol * f.SY;
                a[3] += f.Vol * f.TX; a[4] += f.Vol * f.TY; a[5] += f.Vol * f.Dist;
            }
            var list = new List<Flow>(d.Count);
            foreach (var a in d.Values)
                if (a[0] > 1e-9)
                    list.Add(new Flow { Vol = a[0], SX = a[1] / a[0], SY = a[2] / a[0], TX = a[3] / a[0], TY = a[4] / a[0], Dist = a[5] / a[0] });
            list.Sort((p, q) => q.Vol.CompareTo(p.Vol));
            if (list.Count > maxArrows) list.RemoveRange(maxArrows, list.Count - maxArrows);
            return list;
        }

        // ---------------- 5. Drawing (shared by both entry points) ----------------

        public const string LayerCut = "C3DF-BALANCE-CUT";
        public const string LayerFill = "C3DF-BALANCE-FILL";
        public const string LayerArrow = "C3DF-BALANCE-ARROW";
        public const string LayerText = "C3DF-BALANCE-NOTE";

        public static ObjectId EnsureLayer(Transaction tr, Database db, string name, short aci, byte transparencyPct)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var rec = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, aci),
            };
            ObjectId id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            // No Transparency: under accoreconsole set_Transparency throws eNoDatabase (even after appending);
            // pure decoration, not worth a workaround -- users can enable it in the layer manager. transparencyPct stays as a placeholder.
            return id;
        }

        /// <summary>Idempotent: wipe old entities on this tool's four layers (only the layers it draws on).</summary>
        public static int ClearOwnLayers(Transaction tr, Database db)
        {
            int erased = 0;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                if (e.Layer == LayerCut || e.Layer == LayerFill || e.Layer == LayerArrow || e.Layer == LayerText)
                { e.UpgradeOpen(); e.Erase(); erased++; }
            }
            return erased;
        }

        public static int DrawCells(Transaction tr, BlockTableRecord space, List<Cell> cells,
                                    double step, string layer)
        {
            double h = step / 2;
            int cnt = 0;
            foreach (var c in cells)
            {
                // Solid vertex order is Z-shaped: lower-left, lower-right, upper-left, upper-right
                var s = new Solid(
                    new Point3d(c.X - h, c.Y - h, 0), new Point3d(c.X + h, c.Y - h, 0),
                    new Point3d(c.X - h, c.Y + h, 0), new Point3d(c.X + h, c.Y + h, 0))
                { Layer = layer };
                space.AppendEntity(s);
                tr.AddNewlyCreatedDBObject(s, true);
                cnt++;
            }
            return cnt;
        }

        public static int DrawArrows(Transaction tr, BlockTableRecord space, List<Flow> arrows,
                                     double maxVolForWidth, double widthMin, double widthMax)
        {
            int cnt = 0;
            foreach (var a in arrows)
            {
                double L = Dist(a.SX, a.SY, a.TX, a.TY);
                if (L < 1e-6) continue;
                double w = widthMin + (widthMax - widthMin) * Math.Min(1.0, a.Vol / Math.Max(maxVolForWidth, 1e-9));
                double hl = Math.Min(0.35 * L, w * 3.5);
                double ux = (a.TX - a.SX) / L, uy = (a.TY - a.SY) / L;
                double bx = a.TX - ux * hl, by = a.TY - uy * hl;
                var pl = new Polyline(3) { Layer = LayerArrow };
                pl.AddVertexAt(0, new Point2d(a.SX, a.SY), 0, w, w);
                pl.AddVertexAt(1, new Point2d(bx, by), 0, w * 2.6, 0);
                pl.AddVertexAt(2, new Point2d(a.TX, a.TY), 0, 0, 0);
                space.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                cnt++;
            }
            return cnt;
        }

        public static void DrawText(Transaction tr, BlockTableRecord space, double x, double y,
                                    string s, double h)
        {
            var t = new DBText
            {
                Position = new Point3d(x, y, 0),
                TextString = s,
                Height = h,
                Layer = LayerText,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = new Point3d(x, y, 0),
            };
            space.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
        }

        /// <summary>Histogram text lines (same format for the command line / receipt of both entry points).</summary>
        public static List<string> HistogramLines(Result r)
        {
            var lines = new List<string>();
            if (r.BandEdges == null) return lines;
            for (int b = 0; b <= r.BandEdges.Length; b++)
            {
                string label = b == 0 ? "≤" + r.BandEdges[0] + "m"
                    : b == r.BandEdges.Length ? ">" + r.BandEdges[r.BandEdges.Length - 1] + "m"
                    : r.BandEdges[b - 1] + "~" + r.BandEdges[b] + "m";
                double v = r.BandVols[b];
                if (v < 0.005) continue;
                double pct = r.InternalMoved > 1e-9 ? v / r.InternalMoved * 100 : 0;
                lines.Add(label + "  " + v.ToString("0") + " m3  (" + pct.ToString("0.0") + "%)");
            }
            return lines;
        }
    }
}
