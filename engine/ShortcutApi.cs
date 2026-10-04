using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DFactory
{
    // The data-shortcut assembly is supplied by Civil 3D, not redistributed or
    // included in the NuGet core reference package. Resolve it only when used.
    internal static class ShortcutApi
    {
        internal static Type HostType
        {
            get
            {
                const string name = "Autodesk.Civil.DataShortcuts.DataShortcuts";
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                string folder = Path.GetDirectoryName(typeof(Autodesk.Civil.ApplicationServices.CivilDocument).Assembly.Location);
                string path = Path.Combine(folder, "AeccDataShortcutMgd.dll");
                if (!File.Exists(path)) throw new NotSupportedException("Civil 3D data shortcuts assembly is unavailable: " + path);
                return Assembly.LoadFrom(path).GetType(name, true);
            }
        }

        static object Call(Type type, object instance, string name, object[] args)
        {
            MethodInfo selected = null;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | (instance == null ? BindingFlags.Static : BindingFlags.Instance)))
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) continue;
                bool match = true;
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();
                    if (args[i] != null && !pt.IsInstanceOfType(args[i]) && !(pt.IsEnum && args[i] is string)) { match = false; break; }
                }
                if (!match) continue;
                if (selected != null) throw new AmbiguousMatchException("Ambiguous data shortcuts method: " + name);
                selected = m;
            }
            if (selected == null) throw new NotSupportedException("This Civil 3D version has no matching data shortcuts method: " + name);
            ParameterInfo[] parameters = selected.GetParameters();
            for (int i = 0; i < args.Length; i++)
                if (parameters[i].ParameterType.IsEnum && args[i] is string value)
                    args[i] = Enum.Parse(parameters[i].ParameterType, value);
            try { return selected.Invoke(instance, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        static object Static(string name, params object[] args) { return Call(HostType, null, name, args); }
        internal static void SetWorkingFolder(string path) { Static("SetWorkingFolder", path); }
        internal static string GetWorkingFolder() { return (string)Static("GetWorkingFolder"); }
        internal static string GetCurrentProjectFolder() { return (string)Static("GetCurrentProjectFolder"); }
        internal static void SetCurrentProjectFolder(string name) { Static("SetCurrentProjectFolder", name); }
        internal static void CreateProjectFolder(string name, string description, string template, bool current)
        { Static("CreateProjectFolder", name, description, template, current); }
        internal static bool RepairBrokenDRef(ObjectId id, string target, bool other)
        { return (bool)Static("RepairBrokenDRef", id, target, other); }
        internal static ObjectIdCollection CreateReference(Database db, string source, string name, string kind)
        { return (ObjectIdCollection)Static("CreateReference", db, source, name, kind); }
        internal static DataShortcutManager CreateDataShortcutManager(ref bool valid)
        {
            object[] args = { valid };
            object manager = Static("CreateDataShortcutManager", args);
            valid = (bool)args[0];
            return new DataShortcutManager(manager);
        }
        internal static bool SaveDataShortcutManager(ref DataShortcutManager manager)
        {
            object[] args = { manager.Value };
            bool saved = (bool)Static("SaveDataShortcutManager", args);
            manager.Value = args[0];
            return saved;
        }
        internal sealed class ShortcutItem
        {
            readonly object value;
            internal ShortcutItem(object item) { value = item; }
            object Get(string name) { return value.GetType().GetProperty(name).GetValue(value, null); }
            internal string Name { get { return (string)Get("Name"); } }
            internal string DSEntityType { get { return Get("DSEntityType").ToString(); } }
            internal bool IsBroken { get { return (bool)Get("IsBroken"); } }
            internal string SourceLocation { get { return (string)Get("SourceLocation"); } }
        }
        internal sealed class DataShortcutManager : IDisposable
        {
            internal object Value;
            internal DataShortcutManager(object value) { Value = value; }
            object Invoke(string name, params object[] args) { return Call(Value.GetType(), Value, name, args); }
            internal int GetExportableItemsCount() { return (int)Invoke("GetExportableItemsCount"); }
            internal int GetPublishedItemsCount() { return (int)Invoke("GetPublishedItemsCount"); }
            internal ShortcutItem GetExportableItemAt(int index) { return new ShortcutItem(Invoke("GetExportableItemAt", index)); }
            internal ShortcutItem GetPublishedItemAt(int index) { return new ShortcutItem(Invoke("GetPublishedItemAt", index)); }
            internal void SetSelectItemAtIndex(int index, bool select) { Invoke("SetSelectItemAtIndex", index, select); }
            internal ObjectIdCollection CreateReference(int index, Database db) { return (ObjectIdCollection)Invoke("CreateReference", index, db); }
            public void Dispose() { if (Value is IDisposable disposable) disposable.Dispose(); }
        }
    }
}
