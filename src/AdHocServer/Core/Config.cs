using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Configuration for the whole server. Deliberately hand-parsed (no XML/INI libs)
    /// so the binary stays 100% BCL-only and XP friendly.
    /// </summary>
    public sealed class Config
    {
        public string BaseDir = ".";
        public string ConfigPath = "config.ini";
        public string LogDir = "logs";
        public string DataDir = "data";
        public string WebRoot = "public";

        // ---- server (adhoc protocol) ----
        public int AdhocPort = 27312;
        public int[] AdhocPortFallbacks = new int[] { 27313, 27314 };
        public int ListenBacklog = 128;
        public int MaxUsers = 256;
        public int MaxUsersPerIp = 16;
        public int UserTimeoutSec = 25;
        public int SendTimeoutMs = 5000;
        public int ReadTimeoutMs = 1000;
        public int SessionMaxHours = 12;
        public int EmptyGameTtlMin = 15;
        public bool IgnoreUnknownOpcodes = true;
        public bool KickDuplicateMac = false;
        public bool RequireValidProductCode = true;
        public string ShutdownMessage = "ADHOC SERVER IS SHUTTING DOWN!";

        // ---- web ----
        public int WebPort = 8080;
        public int[] WebPortFallbacks = new int[] { 8081, 8082, 8083, 8084, 8085 };
        public string WebBindAddress = "0.0.0.0";
        public bool WebCompress = true;
        public int WebRequestsPerMinute = 600;
        public int WebMaxConcurrent = 32;
        public bool ExposeLogs = true;
        public string AdminToken = "";
        public string DefaultTheme = "light";
        public bool DefaultSound = true;
        public int RefreshMs = 2000;

        // ---- limits / abuse ----
        public int MaxFailedLoginsPerIp = 20;
        public int BanSeconds = 120;
        public int HistorySize = 300;
        public int EventSize = 400;
        public int LogRingSize = 1500;
        public int MaxPacketBuffer = 4096;

        // ---- logging ----
        public LogLevel LogLevel = LogLevel.Info;
        public bool ProtocolTrace = true;
        public int LogMaxFileMB = 8;
        public int LogRetentionDays = 14;

        // ---- diagnostics / flags ----
        public bool NoConsoleStatusLine = false;

        public string AdhocPortHint = "27312";

        public static Config Load(string baseDir)
        {
            Config c = new Config();
            c.BaseDir = Path.GetFullPath(baseDir);

            string iniPath = Path.Combine(c.BaseDir, "config.ini");
            if (!File.Exists(iniPath))
            {
                // Compatibility with the old server: port.txt used to hold the web port.
                int legacyWebPort = 0;
                string legacy = Path.Combine(c.BaseDir, "port.txt");
                if (File.Exists(legacy))
                {
                    try
                    {
                        int p;
                        if (int.TryParse(File.ReadAllText(legacy).Trim(), out p) && p > 0 && p <= 65535)
                        {
                            legacyWebPort = p;
                            c.WebPort = p;
                        }
                    }
                    catch { /* ignore broken legacy file */ }
                }
                try { c.WriteDefaultIni(iniPath); } catch { /* not fatal */ }
                if (legacyWebPort > 0)
                {
                    Log.Info("CONFIG", "Legacy port.txt detected, web port set to " + legacyWebPort.ToString(CultureInfo.InvariantCulture));
                }
            }

            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(iniPath)) ParseIni(File.ReadAllLines(iniPath), kv); }
            catch (Exception ex) { Log.Warn("CONFIG", "config.ini parse failed: " + ex.Message); }

            c.ConfigPath = iniPath;

            c.AdhocPort = GetInt(kv, "server.adhocPort", c.AdhocPort);
            c.AdhocPortFallbacks = GetPortList(kv, "server.adhocPortFallbacks", c.AdhocPortFallbacks);
            c.ListenBacklog = GetInt(kv, "server.listenBacklog", c.ListenBacklog);
            c.MaxUsers = GetInt(kv, "server.maxUsers", c.MaxUsers);
            c.MaxUsersPerIp = GetInt(kv, "server.maxUsersPerIp", c.MaxUsersPerIp);
            c.UserTimeoutSec = GetInt(kv, "server.userTimeoutSec", c.UserTimeoutSec);
            c.SendTimeoutMs = GetInt(kv, "server.sendTimeoutMs", c.SendTimeoutMs);
            c.ReadTimeoutMs = GetInt(kv, "server.readTimeoutMs", c.ReadTimeoutMs);
            c.SessionMaxHours = GetInt(kv, "server.sessionMaxHours", c.SessionMaxHours);
            c.EmptyGameTtlMin = GetInt(kv, "server.emptyGameTtlMin", c.EmptyGameTtlMin);
            c.IgnoreUnknownOpcodes = GetBool(kv, "server.ignoreUnknownOpcodes", c.IgnoreUnknownOpcodes);
            c.KickDuplicateMac = GetBool(kv, "server.kickDuplicateMac", c.KickDuplicateMac);
            c.RequireValidProductCode = GetBool(kv, "server.requireValidProductCode", c.RequireValidProductCode);
            c.ShutdownMessage = GetStr(kv, "server.shutdownMessage", c.ShutdownMessage);

            c.WebPort = GetInt(kv, "web.port", c.WebPort);
            c.WebPortFallbacks = GetPortList(kv, "web.portFallbacks", c.WebPortFallbacks);
            c.WebBindAddress = GetStr(kv, "web.bindAddress", GetStr(kv, "web.prefix", c.WebBindAddress));
            if (c.WebBindAddress.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                c.WebBindAddress.IndexOf('/') >= 0 || c.WebBindAddress == "+" || c.WebBindAddress == "*")
                c.WebBindAddress = "0.0.0.0";
            c.WebCompress = GetBool(kv, "web.compress", c.WebCompress);
            c.WebRequestsPerMinute = GetInt(kv, "web.requestsPerMinute", c.WebRequestsPerMinute);
            c.WebMaxConcurrent = GetInt(kv, "web.maxConcurrentRequests", c.WebMaxConcurrent);
            c.ExposeLogs = GetBool(kv, "web.exposeLogs", c.ExposeLogs);
            c.AdminToken = GetStr(kv, "web.adminToken", c.AdminToken);
            c.DefaultTheme = GetStr(kv, "web.defaultTheme", c.DefaultTheme);
            c.DefaultSound = GetBool(kv, "web.defaultSound", c.DefaultSound);
            c.RefreshMs = GetInt(kv, "web.refreshMs", c.RefreshMs);
            c.WebRoot = GetStr(kv, "web.webRoot", c.WebRoot);

            c.MaxFailedLoginsPerIp = GetInt(kv, "limits.maxFailedLoginsPerIp", c.MaxFailedLoginsPerIp);
            c.BanSeconds = GetInt(kv, "limits.banSeconds", c.BanSeconds);
            c.HistorySize = GetInt(kv, "limits.historySize", c.HistorySize);
            c.EventSize = GetInt(kv, "limits.eventSize", c.EventSize);
            c.LogRingSize = GetInt(kv, "limits.logRingSize", c.LogRingSize);
            c.MaxPacketBuffer = GetInt(kv, "limits.maxPacketBuffer", c.MaxPacketBuffer);

            c.LogDir = GetStr(kv, "logging.dir", c.LogDir);
            c.LogLevel = ParseLevel(GetStr(kv, "logging.level", "Info"));
            c.ProtocolTrace = GetBool(kv, "logging.protocolTrace", c.ProtocolTrace);
            c.LogMaxFileMB = GetInt(kv, "logging.maxFileMB", c.LogMaxFileMB);
            c.LogRetentionDays = GetInt(kv, "logging.retentionDays", c.LogRetentionDays);

            c.NoConsoleStatusLine = GetBool(kv, "ui.noConsoleStatusLine", c.NoConsoleStatusLine);

            c.Normalize();
            return c;
        }

        public void Normalize()
        {
            if (!Path.IsPathRooted(LogDir)) LogDir = Path.Combine(BaseDir, LogDir);
            if (!Path.IsPathRooted(DataDir)) DataDir = Path.Combine(BaseDir, DataDir);
            if (!Path.IsPathRooted(WebRoot)) WebRoot = Path.Combine(BaseDir, WebRoot);

            AdhocPort = Clamp(AdhocPort, 1, 65535);
            WebPort = Clamp(WebPort, 1, 65535);
            ListenBacklog = Clamp(ListenBacklog, 8, 4096);
            MaxUsers = Clamp(MaxUsers, 1, 4096);
            MaxUsersPerIp = Clamp(MaxUsersPerIp, 1, 4096);
            UserTimeoutSec = Clamp(UserTimeoutSec, 5, 3600);
            SendTimeoutMs = Clamp(SendTimeoutMs, 100, 120000);
            ReadTimeoutMs = Clamp(ReadTimeoutMs, 100, 60000);
            SessionMaxHours = Clamp(SessionMaxHours, 0, 720);
            EmptyGameTtlMin = Clamp(EmptyGameTtlMin, 1, 10080);
            WebRequestsPerMinute = Clamp(WebRequestsPerMinute, 10, 1000000);
            WebMaxConcurrent = Clamp(WebMaxConcurrent, 1, 512);
            RefreshMs = Clamp(RefreshMs, 500, 60000);
            HistorySize = Clamp(HistorySize, 10, 10000);
            EventSize = Clamp(EventSize, 10, 10000);
            LogRingSize = Clamp(LogRingSize, 50, 20000);
            MaxPacketBuffer = Clamp(MaxPacketBuffer, 256, 1048576);
            LogMaxFileMB = Clamp(LogMaxFileMB, 1, 1024);
            LogRetentionDays = Clamp(LogRetentionDays, 1, 3650);
            if (DefaultTheme != "light" && DefaultTheme != "dark" && DefaultTheme != "oled") DefaultTheme = "light";
            if (string.IsNullOrEmpty(ShutdownMessage)) ShutdownMessage = "ADHOC SERVER IS SHUTTING DOWN!";
        }

        public string AdhocPortList()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(AdhocPort.ToString(CultureInfo.InvariantCulture));
            if (AdhocPortFallbacks != null)
            {
                for (int i = 0; i < AdhocPortFallbacks.Length; i++)
                {
                    if (AdhocPortFallbacks[i] <= 0 || AdhocPortFallbacks[i] == AdhocPort) continue;
                    sb.Append(',').Append(AdhocPortFallbacks[i].ToString(CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        public static LogLevel ParseLevel(string s)
        {
            if (string.IsNullOrEmpty(s)) return LogLevel.Info;
            switch (s.Trim().ToLowerInvariant())
            {
                case "error": return LogLevel.Error;
                case "warn":
                case "warning": return LogLevel.Warn;
                case "info": return LogLevel.Info;
                case "debug": return LogLevel.Debug;
                case "trace":
                case "verbose": return LogLevel.Verbose;
                default:
                    int lvl;
                    if (int.TryParse(s.Trim(), out lvl)) return (LogLevel)Clamp(lvl, 0, 4);
                    return LogLevel.Info;
            }
        }

        private void WriteDefaultIni(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; ==========================================================");
            sb.AppendLine("; DPT.Agent AdHoc Server - configuration");
            sb.AppendLine("; Encoding: UTF-8. Restart the server after editing.");
            sb.AppendLine("; ==========================================================");
            sb.AppendLine();
            sb.AppendLine("[server]");
            sb.AppendLine("; Ad-hoc control protocol port (PPSSPP clients connect here)");
            sb.AppendLine("adhocPort=" + AdhocPort.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("; If the main port is busy the server tries these (comma separated)");
            sb.AppendLine("adhocPortFallbacks=" + JoinPorts(AdhocPortFallbacks));
            sb.AppendLine("listenBacklog=128");
            sb.AppendLine("maxUsers=256");
            sb.AppendLine("maxUsersPerIp=16");
            sb.AppendLine("; Idle session is dropped after this many seconds without traffic");
            sb.AppendLine("userTimeoutSec=25");
            sb.AppendLine("sendTimeoutMs=5000");
            sb.AppendLine("readTimeoutMs=1000");
            sb.AppendLine("; Hard session lifetime in hours (0 = unlimited)");
            sb.AppendLine("sessionMaxHours=12");
            sb.AppendLine("; How long an empty game stays on the board");
            sb.AppendLine("emptyGameTtlMin=15");
            sb.AppendLine("; Tolerant mode: unknown opcodes/odd states do not kill the session");
            sb.AppendLine("ignoreUnknownOpcodes=true");
            sb.AppendLine("kickDuplicateMac=false");
            sb.AppendLine("requireValidProductCode=true");
            sb.AppendLine("shutdownMessage=ADHOC SERVER IS SHUTTING DOWN!");
            sb.AppendLine();
            sb.AppendLine("[web]");
            sb.AppendLine("port=8080");
            sb.AppendLine("portFallbacks=" + JoinPorts(WebPortFallbacks));
            sb.AppendLine("; Bind address: 0.0.0.0 = all interfaces, 127.0.0.1 = local only");
            sb.AppendLine("bindAddress=0.0.0.0");
            sb.AppendLine("compress=true");
            sb.AppendLine("requestsPerMinute=600");
            sb.AppendLine("maxConcurrentRequests=32");
            sb.AppendLine("exposeLogs=true");
            sb.AppendLine("; If set, write endpoints require header X-Admin-Token");
            sb.AppendLine("adminToken=");
            sb.AppendLine("defaultTheme=light");
            sb.AppendLine("defaultSound=true");
            sb.AppendLine("refreshMs=2000");
            sb.AppendLine("webRoot=public");
            sb.AppendLine();
            sb.AppendLine("[limits]");
            sb.AppendLine("maxFailedLoginsPerIp=20");
            sb.AppendLine("banSeconds=120");
            sb.AppendLine("historySize=300");
            sb.AppendLine("eventSize=400");
            sb.AppendLine("logRingSize=1500");
            sb.AppendLine("maxPacketBuffer=4096");
            sb.AppendLine();
            sb.AppendLine("[logging]");
            sb.AppendLine("dir=logs");
            sb.AppendLine("; Error | Warn | Info | Debug | Verbose");
            sb.AppendLine("level=Info");
            sb.AppendLine("protocolTrace=true");
            sb.AppendLine("maxFileMB=8");
            sb.AppendLine("retentionDays=14");
            sb.AppendLine();
            sb.AppendLine("[ui]");
            sb.AppendLine("noConsoleStatusLine=false");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string JoinPorts(int[] list)
        {
            if (list == null || list.Length == 0) return "";
            string[] parts = new string[list.Length];
            for (int i = 0; i < list.Length; i++) parts[i] = list[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        private static void ParseIni(string[] lines, Dictionary<string, string> into)
        {
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null) continue;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    section = end > 1 ? line.Substring(1, end - 1).Trim() : "";
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                // strip inline comments only when clearly separated
                int cm = val.IndexOf(" ;");
                if (cm > 0) val = val.Substring(0, cm).Trim();
                string full = string.IsNullOrEmpty(section) ? key : section + "." + key;
                into[full] = val;
            }
        }

        private static string GetStr(Dictionary<string, string> kv, string key, string def)
        {
            string v;
            return kv.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        private static int GetInt(Dictionary<string, string> kv, string key, int def)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return def;
            int r;
            return int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static bool GetBool(Dictionary<string, string> kv, string key, bool def)
        {
            string v;
            if (!kv.TryGetValue(key, out v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }

        private static int[] GetPortList(Dictionary<string, string> kv, string key, int[] def)
        {
            string v;
            if (!kv.TryGetValue(key, out v) || v.Trim().Length == 0) return def;
            string[] parts = v.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<int> list = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                int p;
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p <= 65535)
                    list.Add(p);
            }
            return list.ToArray();
        }

        public static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}