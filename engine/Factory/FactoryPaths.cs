using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DFactory
{
    /// <summary>
    /// Factory root directory resolution.
    /// civil3dfactory.ps1 sets C3DF_ROOT when it runs accoreconsole; but when the user clicks the ribbon inside Civil 3D
    /// that variable is absent, so we must also derive it from the bundle's current-version.json (deploy.ps1 writes
    /// source_path) or by walking up from the DLL location.
    /// </summary>
    public static class FactoryPaths
    {
        static string _root;
        static bool _resolved;

        /// <summary>Returns null when it cannot be resolved; the caller decides whether to fail or degrade.</summary>
        public static string Root
        {
            get
            {
                if (!_resolved)
                {
                    _root = Resolve();
                    _resolved = true;
                }
                return _root;
            }
        }

        public static void Invalidate()
        {
            _resolved = false;
            _root = null;
        }

        public static string RequireRoot()
        {
            string root = Root;
            if (string.IsNullOrEmpty(root))
                throw new InvalidOperationException(
                    "Cannot locate the factory root: set the C3DF_ROOT environment variable or redeploy the plugin with deploy.ps1.");
            return root;
        }

        public static string NodeJsonPath()
        {
            return Path.Combine(RequireRoot(), "nodes", "node.json");
        }

        /// <summary>Per-user data directory for interactive runs (last parameters, result JSON).</summary>
        public static string UserDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Civil3DFactory");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string Resolve()
        {
            string env = Environment.GetEnvironmentVariable("C3DF_ROOT");
            if (IsFactoryRoot(env)) return Path.GetFullPath(env);

            string fromBundle = FromBundleManifest();
            if (IsFactoryRoot(fromBundle)) return Path.GetFullPath(fromBundle);

            string fromDll = FromAssemblyLocation();
            if (IsFactoryRoot(fromDll)) return Path.GetFullPath(fromDll);

            return null;
        }

        static bool IsFactoryRoot(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try { return File.Exists(Path.Combine(dir, "nodes", "node.json")); }
            catch { return false; }
        }

        /// <summary>deploy.ps1 writes the source directory into the bundle's current-version.json.</summary>
        static string FromBundleManifest()
        {
            try
            {
                string manifest = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "ApplicationPlugins", "Civil3DFactory.bundle", "current-version.json");
                if (!File.Exists(manifest)) return null;
                JsonNode node = JsonNode.Parse(File.ReadAllText(manifest, Encoding.UTF8));
                JsonObject obj = node as JsonObject;
                if (obj == null || obj["source_path"] == null) return null;
                return obj["source_path"].GetValue<string>();
            }
            catch { return null; }
        }

        /// <summary>When debugging via netload straight from the bin directory, walk up from the DLL location to find nodes\node.json.</summary>
        static string FromAssemblyLocation()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path)) return null;
                DirectoryInfo dir = new FileInfo(path).Directory;
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (IsFactoryRoot(dir.FullName)) return dir.FullName;
                    dir = dir.Parent;
                }
                return null;
            }
            catch { return null; }
        }
    }
}
