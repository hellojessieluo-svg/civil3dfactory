#nullable disable   // This file is compiled by both Civil3DFactory (nullable off) and WaterBox (nullable on); treat as off

using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivAlign = Autodesk.Civil.DatabaseServices.Alignment;
using CivAlignEnts = Autodesk.Civil.DatabaseServices.AlignmentEntityCollection;

namespace Civil3DFactory.Geometry
{
    /// <summary>
    /// Core for rewriting alignment geometry in place: clear the entity collection and rebuild segment by segment from a polyline.
    /// The alignment's ObjectId / name / style / station reference are untouched, so corridors, offset alignments, profiles,
    /// sample line groups and other dependents attached by ObjectId all survive (dynamic ones rebuild themselves on the new geometry).
    /// Cost: the original PI/constraint layout is replaced by all-Fixed entities (fixed lines / fixed arcs).
    ///
    /// Single source of truth: compiled by both Civil3DFactory (node replace_alignment_geometry) and products\waterbox
    /// (command C3DF-ReplaceCenterline/HZX); change the algorithm only here.
    ///
    /// Geometry mapping is the inverse of alignment_to_polyline:
    ///   bulge=0 -> AddFixedLine; bulge!=0 -> AddFixedCurve(both endpoints + radius + direction),
    ///   radius = chord/(2*sin(theta/2)), theta = 4*atan|bulge|, bulge&lt;0 is clockwise.
    ///   Two points + radius can only express the minor arc; segments with theta>=180deg are split at the arc midpoint first.
    /// </summary>
    public static class AlignmentRebuildCore
    {
        /// <summary>Rebuild result statistics.</summary>
        public sealed class RebuildResult
        {
            public double OldLength;
            public double NewLength;
            public double PolylineLength;
            public int OldEntities;
            public int NewEntities;
            public int Lines;
            public int Arcs;
            public int ArcSplits;
            /// <summary>The polyline was drawn opposite to the original station direction and was rebuilt reversed automatically.</summary>
            public bool Reversed;
        }

        /// <summary>
        /// Rewrite the alignment in place from polyline geometry. al must already be open ForWrite; pl is read-only.
        /// Station direction follows the original alignment, not the drawing gesture: whichever end of the new line is nearer the original start becomes the start
        /// (decided by the sum of endpoint distances for both orientations, which also works when the alignment moved as a whole); if reversal is needed the vertices are reversed,
        /// bulges negated, and Reversed set to true.
        /// Throws InvalidOperationException when the polyline is closed / has too few vertices / all vertices coincide.
        /// </summary>
        public static RebuildResult RebuildFromPolyline(CivAlign al, Polyline pl)
        {
            if (pl.Closed)
                throw new InvalidOperationException("A closed polyline cannot be used as alignment geometry.");
            int nv = pl.NumberOfVertices;
            if (nv < 2)
                throw new InvalidOperationException("Polyline has fewer than 2 vertices.");

            // Read the vertex table first; it may be reversed as a whole after the direction check
            var pts = new Point2d[nv];
            var bulges = new double[nv];
            for (int i = 0; i < nv; i++)
            {
                pts[i] = pl.GetPoint2dAt(i);
                bulges[i] = pl.GetBulgeAt(i);
            }

            var r = new RebuildResult
            {
                OldLength = al.Length,
                PolylineLength = pl.Length,
                Reversed = ShouldReverse(al, pts)
            };

            if (r.Reversed)
            {
                var rp = new Point2d[nv];
                var rb = new double[nv];
                for (int i = 0; i < nv; i++) rp[i] = pts[nv - 1 - i];
                for (int j = 0; j < nv - 1; j++) rb[j] = -bulges[nv - 2 - j];   // segments walked backwards, arc direction negated
                rb[nv - 1] = 0;
                pts = rp;
                bulges = rb;
            }

            CivAlignEnts ents = al.Entities;
            r.OldEntities = ents.Count;
            ents.Clear();

            for (int i = 0; i < nv - 1; i++)
            {
                if (pts[i].GetDistanceTo(pts[i + 1]) < 1e-6) continue;   // coincident points, skip
                AddSegment(ents, pts[i], pts[i + 1], bulges[i], r, 0);
            }
            if (ents.Count == 0)
                throw new InvalidOperationException("Polyline has no valid segment (all vertices coincident?).");

            r.NewEntities = ents.Count;
            r.NewLength = al.Length;
            return r;
        }

        /// <summary>Orientation check: keep or flip, whichever gives the smaller (start-start + end-end) distance sum.
        /// When the original geometry is unavailable (zero length etc.) no reversal is applied.</summary>
        static bool ShouldReverse(CivAlign al, Point2d[] pts)
        {
            try
            {
                double e0 = 0, n0 = 0, e1 = 0, n1 = 0;
                al.PointLocation(al.StartingStation, 0.0, ref e0, ref n0);
                al.PointLocation(al.EndingStation, 0.0, ref e1, ref n1);
                var os = new Point2d(e0, n0);
                var oe = new Point2d(e1, n1);
                Point2d ps = pts[0], pe = pts[pts.Length - 1];
                double keep = ps.GetDistanceTo(os) + pe.GetDistanceTo(oe);
                double flip = pe.GetDistanceTo(os) + ps.GetDistanceTo(oe);
                return flip < keep;
            }
            catch
            {
                return false;
            }
        }

        static void AddSegment(CivAlignEnts ents, Point2d s, Point2d e, double bulge,
            RebuildResult r, int depth)
        {
            if (Math.Abs(bulge) < 1e-9)
            {
                ents.AddFixedLine(new Point3d(s.X, s.Y, 0.0), new Point3d(e.X, e.Y, 0.0));
                r.Lines++;
                return;
            }
            double theta = 4.0 * Math.Atan(Math.Abs(bulge));
            if (theta > Math.PI * 0.999 && depth < 8)
            {
                // AddFixedCurve with two points + radius only takes the minor arc; split segments >=180deg at the arc midpoint
                Point2d mid = BulgeMidPoint(s, e, bulge);
                double half = Math.Tan(theta / 8.0) * Math.Sign(bulge);
                r.ArcSplits++;
                AddSegment(ents, s, mid, half, r, depth + 1);
                AddSegment(ents, mid, e, half, r, depth + 1);
                return;
            }
            double chord = s.GetDistanceTo(e);
            double radius = chord / (2.0 * Math.Sin(theta / 2.0));
            ents.AddFixedCurve(new Point3d(s.X, s.Y, 0.0), new Point3d(e.X, e.Y, 0.0),
                radius, bulge < 0.0);
            r.Arcs++;
        }

        /// <summary>Arc midpoint of a bulge segment: chord midpoint - left normal * (bulge*chord/2).
        /// Check: (0,0)->(1,0) bulge=1 (half circle, counter-clockwise) midpoint should be (0.5,-0.5).</summary>
        static Point2d BulgeMidPoint(Point2d s, Point2d e, double b)
        {
            double dx = e.X - s.X, dy = e.Y - s.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / chord, uy = dy / chord;
            double mx = (s.X + e.X) / 2.0, my = (s.Y + e.Y) / 2.0;
            double sag = b * chord / 2.0;
            return new Point2d(mx + uy * sag, my - ux * sag);
        }
    }
}
