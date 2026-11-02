using System;
using System.IO;
using System.Reflection;

namespace Dpt.AdHoc.Core
{
    /// <summary>Composition root shared by the protocol server, web server and supervisor.</summary>
    public sealed class ServerApp
    {
        public readonly string Name = "DPT.PSP AdHoc Server";
        public readonly string Version;
        public readonly string BuildStamp;

        public Config Cfg;
        public GameDatabase Db;
        public ServerState State;
        public Dpt.AdHoc.Net.AdhocServer Adhoc;
        public Dpt.AdHoc.Web.WebServer WebPublic;
        public Dpt.AdHoc.Web.WebServer WebAdmin;

        public DateTime StartedUtc = DateTime.UtcNow;
        public volatile bool ShuttingDown;

        public ServerApp()
        {
            Version = ResolveVersion();
            BuildStamp = ResolveBuildStamp();
        }

        public static string ResolveVersion()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                object[] attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs != null && attrs.Length > 0)
                {
                    string v = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                Version v2 = asm.GetName().Version;
                if (v2 != null) return v2.ToString(3);
            }
            catch { }
            return "1.0.0";
        }

        public static string ResolveBuildStamp()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            return "";
        }

        public TimeSpan Uptime
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - StartedUtc;
                return span.TotalSeconds < 0 ? TimeSpan.Zero : span;
            }
        }

        public long WorkingSetBytes
        {
            get
            {
                try { return System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
                catch { return 0; }
            }
        }
    }
}