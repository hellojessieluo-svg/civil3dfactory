using System;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivSectionSource = Autodesk.Civil.DatabaseServices.SectionSource;
using CivSubassembly = Autodesk.Civil.DatabaseServices.Subassembly;
using CivSubassemblyCollection = Autodesk.Civil.DatabaseServices.SubassemblyCollection;

namespace Civil3DFactory
{
    /// <summary>
    /// API members that do not exist in the Civil 3D 2022 reference assemblies (the net472 build targets
    /// 2022-2024 with the 2022 references). On net472 they are resolved by reflection at run time, so a
    /// 2023/2024 host that has the member still works and a 2022 host gets a clear NotSupportedException.
    /// On net8 / net10 the calls compile straight through.
    /// </summary>
    internal static class CivilCompat
    {
        /// <summary>SectionSource.SourceName (2023+); on 2022 resolved from SourceId.</summary>
        public static string SourceNameOf(this CivSectionSource src)
        {
#if NET472
            try
            {
                PropertyInfo p = src.GetType().GetProperty("SourceName");
                if (p != null) return p.GetValue(src) as string ?? "";
            }
            catch { }
            try
            {
                DBObject obj = src.SourceId.GetObject(OpenMode.ForRead);
                PropertyInfo np = obj.GetType().GetProperty("Name");
                return np != null ? (np.GetValue(obj) as string ?? "") : "";
            }
            catch { return ""; }
#else
            return src.SourceName;
#endif
        }

        /// <summary>Subassembly.Status as text (2023+); "Unknown" where the host has no such property.</summary>
        public static string StatusOf(this CivSubassembly sa)
        {
#if NET472
            try
            {
                PropertyInfo p = sa.GetType().GetProperty("Status");
                if (p != null) { object v = p.GetValue(sa); return v == null ? "Unknown" : v.ToString(); }
            }
            catch { }
            return "Unknown";
#else
            return sa.Status.ToString();
#endif
        }

        /// <summary>Subassembly.IsFromSubassemblyComposer (2023+); false where unavailable.</summary>
        public static bool IsComposer(this CivSubassembly sa)
        {
#if NET472
            try
            {
                PropertyInfo p = sa.GetType().GetProperty("IsFromSubassemblyComposer");
                if (p != null) return (bool)p.GetValue(sa);
            }
            catch { }
            return false;
#else
            return sa.IsFromSubassemblyComposer;
#endif
        }

        /// <summary>SubassemblyCollection.ImportSACSubassembly (2023+).</summary>
        public static ObjectId ImportSac(this CivSubassemblyCollection col, string name, string pktPath, Point3d origin)
        {
#if NET472
            MethodInfo m = col.GetType().GetMethod("ImportSACSubassembly", new[] { typeof(string), typeof(string), typeof(Point3d) });
            if (m == null)
                throw new NotSupportedException("ImportSACSubassembly is not available in this Civil 3D version (needs 2023 or newer). Import the PKT in the Civil 3D UI instead.");
            try { return (ObjectId)m.Invoke(col, new object[] { name, pktPath, origin }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
#else
            return col.ImportSACSubassembly(name, pktPath, origin);
#endif
        }
    }
}
