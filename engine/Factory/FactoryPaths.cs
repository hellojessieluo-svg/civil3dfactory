using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DFactory
{
    /// <summary>
    /// 工厂根目录解析。
    /// civil3dfactory.ps1 跑 accoreconsole 时会设置 C3DF_ROOT；但用户在 Civil 3D 里点功能区时
    /// 没有这个环境变量，所以还要能从 bundle 的 current-version.json（deploy.ps1 写入
    /// source_path）或 DLL 所在位置往上回推。
    /// </summary>
    public static class FactoryPaths
    {
        static string _root;
        static bool _resolved;

        /// <summary>解析不到时返回 null，调用方自己决定是报错还是降级。</summary>
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
                    "定位不到工厂根目录：请设置环境变量 C3DF_ROOT，或用 deploy.ps1 重新部署插件。");
            return root;
        }

        public static string NodeJsonPath()
        {
            return Path.Combine(RequireRoot(), "nodes", "node.json");
        }

        /// <summary>交互式运行的用户级数据目录（上次参数、结果 JSON）。</summary>
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

        /// <summary>deploy.ps1 把源码目录写进 bundle 的 current-version.json。</summary>
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

        /// <summary>直接从 bin 目录 netload 调试时，沿 DLL 位置往上找 nodes\node.json。</summary>
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
