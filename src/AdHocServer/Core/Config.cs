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
        // Public board: this is the port you expose to the internet.
        public int PublicPort = 44501;
        public int[] PublicPortFallbacks = new int[] { 44503, 44505 };
        public string PublicBind = "0.0.0.0";

        // Admin board: keep it on the LAN, it exposes moderation + protocol logs.
        public int AdminPort = 44502;
        public int[] AdminPortFallbacks = new int[] { 44504, 44506 };
        public string AdminBind = "0.0.0.0";
        public bool AdminPrivateOnly = true;

        // Reverse proxy (Nginx Proxy Manager, nginx, Cloudflare tunnel, NAT hairpin).
        public string PublicBasePath = "";
        public string AdminBasePath = "";
        public bool TrustProxyHeaders = true;
        public string TrustedProxies =
            "127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16";

        // Admin API protection: token -> PBKDF2(salt) -> HMAC signatures + sealed bodies.
        public string AdminToken = "";
        public string AdminSalt = "";
        public int AdminKdfIterations = 12000;
        public bool AdminEncrypt = true;
        public bool AdminAllowQueryToken = false;

        public bool WebCompress = true;
        public int WebRequestsPerMinute = 600;
        public int WebMaxConcurrent = 32;
        public bool ExposeLogs = true;
        public bool StatusEndpoints = true;
        public bool StatusPorts = true;
        public string ServerName = "DPT.PSP AdHoc Server";
        public string ServerLocation = "";
        public string ServerDescription = "";

        // ---- board visits + public chat ----
        public bool VisitTracking = true;
        public bool ChatEnabled = true;
        public bool ChatToGame = true;
        public int ChatCooldownSec = 20;
        public int ChatMaxPer10Min = 8;
        public int ChatMaxLength = 200;
        public string DefaultTheme = "light";
        public bool DefaultSound = true;
        public int RefreshMs = 2000;

        // ---- relay (PPSSPP aemu_postoffice) ----
        public bool RelayEnabled = true;
        public int RelayPort = 27313;
        public string RelayBind = "0.0.0.0";
        public int RelayMaxSessions = 512;
        public int RelayMaxPerIp = 128;
        public int RelayIdleSeconds = 600;
        public int RelayLifetimeSeconds = 43200;
        public int RelayMaxPdpBytes = 16384;
        public bool RelayUdpProbe = true;
        public bool RelayPortTranslation = true;

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
                            c.PublicPort = p;
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

            c.PublicPort = GetInt(kv, "web.publicPort", GetInt(kv, "web.port", c.PublicPort));
            c.PublicPortFallbacks = GetPortList(kv, "web.publicPortFallbacks",
                GetPortList(kv, "web.portFallbacks", c.PublicPortFallbacks));
            c.PublicBind = NormalizeBind(GetStr(kv, "web.publicBind", GetStr(kv, "web.bindAddress", c.PublicBind)));
            c.AdminPort = GetInt(kv, "web.adminPort", c.AdminPort);
            c.AdminPortFallbacks = GetPortList(kv, "web.adminPortFallbacks", c.AdminPortFallbacks);
            c.AdminBind = NormalizeBind(GetStr(kv, "web.adminBind", c.AdminBind));
            c.AdminPrivateOnly = GetBool(kv, "web.adminPrivateOnly", c.AdminPrivateOnly);
            c.PublicBasePath = NormalizeBasePath(GetStr(kv, "web.publicBasePath", c.PublicBasePath));
            c.AdminBasePath = NormalizeBasePath(GetStr(kv, "web.adminBasePath", c.AdminBasePath));
            c.TrustProxyHeaders = GetBool(kv, "web.trustProxyHeaders", c.TrustProxyHeaders);
            c.TrustedProxies = GetStr(kv, "web.trustedProxies", c.TrustedProxies);
            c.AdminKdfIterations = GetInt(kv, "web.adminKdfIterations", c.AdminKdfIterations);
            c.AdminEncrypt = GetBool(kv, "web.adminEncrypt", c.AdminEncrypt);
            c.AdminAllowQueryToken = GetBool(kv, "web.adminAllowQueryToken", c.AdminAllowQueryToken);
            c.WebCompress = GetBool(kv, "web.compress", c.WebCompress);
            c.WebRequestsPerMinute = GetInt(kv, "web.requestsPerMinute", c.WebRequestsPerMinute);
            c.WebMaxConcurrent = GetInt(kv, "web.maxConcurrentRequests", c.WebMaxConcurrent);
            c.ExposeLogs = GetBool(kv, "web.exposeLogs", c.ExposeLogs);
            c.StatusEndpoints = GetBool(kv, "web.statusEndpoints", c.StatusEndpoints);
            c.StatusPorts = GetBool(kv, "web.statusPorts", c.StatusPorts);
            c.ServerName = GetStr(kv, "web.serverName", c.ServerName);
            c.ServerLocation = GetStr(kv, "web.serverLocation", c.ServerLocation);
            c.ServerDescription = GetStr(kv, "web.serverDescription", c.ServerDescription);
            c.VisitTracking = GetBool(kv, "web.visits", c.VisitTracking);
            c.ChatEnabled = GetBool(kv, "web.chatEnabled", c.ChatEnabled);
            c.ChatToGame = GetBool(kv, "web.chatToGame", c.ChatToGame);
            c.ChatCooldownSec = GetInt(kv, "web.chatCooldownSec", c.ChatCooldownSec);
            c.ChatMaxPer10Min = GetInt(kv, "web.chatMaxPer10Min", c.ChatMaxPer10Min);
            c.ChatMaxLength = GetInt(kv, "web.chatMaxLength", c.ChatMaxLength);
            c.AdminToken = GetStr(kv, "web.adminToken", c.AdminToken);
            c.AdminSalt = GetStr(kv, "web.adminSalt", c.AdminSalt);
            c.DefaultTheme = GetStr(kv, "web.defaultTheme", c.DefaultTheme);
            c.DefaultSound = GetBool(kv, "web.defaultSound", c.DefaultSound);
            c.RefreshMs = GetInt(kv, "web.refreshMs", c.RefreshMs);
            c.WebRoot = GetStr(kv, "web.webRoot", c.WebRoot);

            c.RelayEnabled = GetBool(kv, "relay.enabled", c.RelayEnabled);
            c.RelayPort = GetInt(kv, "relay.port", c.RelayPort);
            c.RelayBind = NormalizeBind(GetStr(kv, "relay.bindAddress", c.RelayBind));
            c.RelayMaxSessions = GetInt(kv, "relay.maxSessions", c.RelayMaxSessions);
            c.RelayMaxPerIp = GetInt(kv, "relay.maxPerIp", c.RelayMaxPerIp);
            c.RelayIdleSeconds = GetInt(kv, "relay.idleSeconds", c.RelayIdleSeconds);
            c.RelayLifetimeSeconds = GetInt(kv, "relay.lifetimeSeconds", c.RelayLifetimeSeconds);
            c.RelayMaxPdpBytes = GetInt(kv, "relay.maxPdpBytes", c.RelayMaxPdpBytes);
            c.RelayUdpProbe = GetBool(kv, "relay.udpProbe", c.RelayUdpProbe);
            c.RelayPortTranslation = GetBool(kv, "relay.portTranslation", c.RelayPortTranslation);

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
            PublicPort = Clamp(PublicPort, 1, 65535);
            AdminPort = Clamp(AdminPort, 1, 65535);
            if (AdminPort == PublicPort) AdminPort = PublicPort == 65535 ? 65534 : PublicPort + 1;
            AdminKdfIterations = Clamp(AdminKdfIterations, 1000, 200000);
            if (string.IsNullOrEmpty(AdminSalt) || ApiCrypto.FromHex(AdminSalt) == null) AdminSalt = "";
            if (string.IsNullOrEmpty(PublicBind)) PublicBind = "0.0.0.0";
            if (string.IsNullOrEmpty(AdminBind)) AdminBind = "0.0.0.0";
            RelayPort = Clamp(RelayPort, 1, 65535);
            RelayMaxSessions = Clamp(RelayMaxSessions, 8, 8192);
            RelayMaxPerIp = Clamp(RelayMaxPerIp, 1, 4096);
            RelayIdleSeconds = Clamp(RelayIdleSeconds, 30, 86400);
            RelayLifetimeSeconds = Clamp(RelayLifetimeSeconds, 60, 172800);
            RelayMaxPdpBytes = Clamp(RelayMaxPdpBytes, 512, 65536);
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

        private static string NormalizeBind(string value)
        {
            if (string.IsNullOrEmpty(value)) return "0.0.0.0";
            string v = value.Trim();
            if (v == "*" || v == "+" || v == "any" || v == "all") return "0.0.0.0";
            if (v.IndexOf("://", StringComparison.Ordinal) >= 0 || v.IndexOf('/') >= 0) return "0.0.0.0";
            return v;
        }

        /// <summary>"/adhoc/" -> "/adhoc"; "" stays "". Used when the board lives behind a proxy sub-path.</summary>
        public static string NormalizeBasePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string v = value.Trim();
            if (v.Length == 0 || v == "/") return "";
            if (!v.StartsWith("/", StringComparison.Ordinal)) v = "/" + v;
            while (v.EndsWith("/", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 1);
            return v;
        }

        /// <summary>
        /// Makes sure the admin board has a token and a per-install salt.
        /// Generated values are written back into config.ini so they survive restarts.
        /// </summary>
        public bool EnsureSecrets()
        {
            bool changed = false;

            if (string.IsNullOrEmpty(AdminSalt) || ApiCrypto.FromHex(AdminSalt) == null)
            {
                AdminSalt = ApiCrypto.RandomHex(ApiCrypto.SaltBytes);
                changed = true;
            }

            if (string.IsNullOrEmpty(AdminToken))
            {
                AdminToken = ApiCrypto.RandomHex(24); // 48 hex chars, ~192 bit
                changed = true;
            }

            if (changed)
            {
                try
                {
                    UpdateIniKeys(new string[] { "adminToken", "adminSalt" });
                    Log.Warn("CONFIG", "admin credentials generated and saved to " + ConfigPath);
                }
                catch (Exception ex)
                {
                    Log.Error("CONFIG", "cannot persist admin credentials to config.ini: " + ex.Message);
                }
            }
            return changed;
        }

        /// <summary>Rewrites selected [web] keys in config.ini, keeping everything else intact.</summary>
        public void UpdateIniKeys(string[] keys)
        {
            bool hasBom;
            string text = ReadTextPreservingBom(ConfigPath, out hasBom);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            List<string> output = new List<string>();
            bool[] seen = new bool[keys.Length];

            string section = "";
            bool inWeb = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    if (inWeb) AppendMissingKeys(output, keys, seen);
                    int end = trimmed.IndexOf(']');
                    section = end > 1 ? trimmed.Substring(1, end - 1).Trim() : "";
                    inWeb = section.Equals("web", StringComparison.OrdinalIgnoreCase);
                    output.Add(line);
                    continue;
                }

                if (inWeb)
                {
                    int eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        string name = trimmed.Substring(0, eq).Trim();
                        int idx = IndexOfKey(keys, name);
                        if (idx >= 0)
                        {
                            if (!seen[idx])
                            {
                                output.Add(KeyLine(keys[idx]));
                                seen[idx] = true;
                            }
                            continue; // drop duplicated/stale lines
                        }
                    }
                }
                output.Add(line);
            }

            if (inWeb) AppendMissingKeys(output, keys, seen);
            else if (!AllSeen(seen))
            {
                output.Add("");
                output.Add("[web]");
                for (int k = 0; k < keys.Length; k++)
                    if (!seen[k]) output.Add(KeyLine(keys[k]));
            }

            string result = string.Join(Environment.NewLine, output.ToArray());
            WriteTextPreservingBom(ConfigPath, result, hasBom);
        }

        private static int IndexOfKey(string[] keys, string name)
        {
            for (int i = 0; i < keys.Length; i++)
                if (string.Equals(keys[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static bool AllSeen(bool[] seen)
        {
            for (int i = 0; i < seen.Length; i++) if (!seen[i]) return false;
            return true;
        }

        private void AppendMissingKeys(List<string> output, string[] keys, bool[] seen)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (seen[i]) continue;
                output.Add(KeyLine(keys[i]));
                seen[i] = true;
            }
        }

        private string KeyLine(string key)
        {
            if (string.Equals(key, "adminToken", StringComparison.OrdinalIgnoreCase)) return "adminToken=" + AdminToken;
            if (string.Equals(key, "adminSalt", StringComparison.OrdinalIgnoreCase)) return "adminSalt=" + AdminSalt;
            return key + "=";
        }

        private static string ReadTextPreservingBom(string path, out bool hasBom)
        {
            hasBom = false;
            if (!File.Exists(path)) return "";
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) hasBom = true;
            return hasBom ? new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3) : new UTF8Encoding(false).GetString(bytes);
        }

        private static void WriteTextPreservingBom(string path, string text, bool bom)
        {
            File.WriteAllText(path, text, new UTF8Encoding(bom));
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
            sb.AppendLine("; ---------- public board (expose this one to the internet) ----------");
            sb.AppendLine("publicPort=" + PublicPort.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("publicPortFallbacks=" + JoinPorts(PublicPortFallbacks));
            sb.AppendLine("; 0.0.0.0 = all interfaces, 127.0.0.1 = local only");
            sb.AppendLine("publicBind=0.0.0.0");
            sb.AppendLine();
            sb.AppendLine("; ---------- admin board (keep it in the LAN) ----------");
            sb.AppendLine("adminPort=" + AdminPort.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("adminPortFallbacks=" + JoinPorts(AdminPortFallbacks));
            sb.AppendLine("adminBind=0.0.0.0");
            sb.AppendLine("; Refuse admin connections from non private addresses (extra net for port forwards)");
            sb.AppendLine("adminPrivateOnly=true");
            sb.AppendLine();
            sb.AppendLine("; ---------- reverse proxy (Nginx Proxy Manager, nginx, NAT hairpin) ----------");
            sb.AppendLine("; Optional sub-path, e.g. /adhoc when the board is published as example.com/adhoc/");
            sb.AppendLine("publicBasePath=");
            sb.AppendLine("adminBasePath=");
            sb.AppendLine("; Trust X-Forwarded-For / X-Real-IP, but only from the proxies listed below");
            sb.AppendLine("trustProxyHeaders=true");
            sb.AppendLine("trustedProxies=127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16");
            sb.AppendLine();
            sb.AppendLine("; ---------- admin API protection ----------");
            sb.AppendLine("; Leave empty once, the server generates a strong token and saves it here.");
            sb.AppendLine("; The browser never sends the token: it derives a key (PBKDF2) and signs requests.");
            sb.AppendLine("adminToken=");
            sb.AppendLine("; Random per-install salt, generated on first run.");
            sb.AppendLine("adminSalt=");
            sb.AppendLine("adminKdfIterations=12000");
            sb.AppendLine("; Encrypt request/response bodies of the admin API (encrypt-then-MAC).");
            sb.AppendLine("adminEncrypt=true");
            sb.AppendLine("; Allow ?token= in URLs (less safe: leaks into history/logs). Keep false.");
            sb.AppendLine("adminAllowQueryToken=false");
            sb.AppendLine();
            sb.AppendLine("compress=true");
            sb.AppendLine("requestsPerMinute=600");
            sb.AppendLine("maxConcurrentRequests=32");
            sb.AppendLine("exposeLogs=true");
            sb.AppendLine("; Public status feed for the PPSSPP server browser: /data.json and /status.xml.");
            sb.AppendLine("; Contains only what the public board shows (nicks, games, groups) - no MAC/IP/ids.");
            sb.AppendLine("statusEndpoints=true");
            sb.AppendLine("; Also publish client PDP ports (pdp_ports) in /data.json. Same as other servers do.");
            sb.AppendLine("statusPorts=true");
            sb.AppendLine("; Metadata used by the PPSSPP server list entry, shown in /data.json.");
            sb.AppendLine("serverName=DPT.PSP AdHoc Server");
            sb.AppendLine("serverLocation=");
            sb.AppendLine("serverDescription=");
            sb.AppendLine("; Board visits: today/all-time counters and how many people are reading now.");
            sb.AppendLine("; Visitors are counted by a salted hash of IP+User-Agent; nothing is stored.");
            sb.AppendLine("visits=true");
            sb.AppendLine("; Public chat on the board: captcha + per-visitor cooldown.");
            sb.AppendLine("chatEnabled=true");
            sb.AppendLine("; Mirror board messages into the in-game chat (players see them in PPSSPP).");
            sb.AppendLine("chatToGame=true");
            sb.AppendLine("chatCooldownSec=20");
            sb.AppendLine("chatMaxPer10Min=8");
            sb.AppendLine("chatMaxLength=200");
            sb.AppendLine("defaultTheme=light");
            sb.AppendLine("defaultSound=true");
            sb.AppendLine("refreshMs=2000");
            sb.AppendLine("webRoot=public");
            sb.AppendLine();
            sb.AppendLine("[relay]");
            sb.AppendLine("; PPSSPP packet relay (aemu_postoffice, TCP). Needed for NAT hairpin and strict NATs.");
            sb.AppendLine("; In PPSSPP set Network -> 'Try to use server-provided packet relay' = Yes.");
            sb.AppendLine("enabled=true");
            sb.AppendLine("; PPSSPP hardcodes this port (AEMU_POSTOFFICE_PORT), keep 27313");
            sb.AppendLine("port=27313");
            sb.AppendLine("bindAddress=0.0.0.0");
            sb.AppendLine("maxSessions=512");
            sb.AppendLine("maxPerIp=128");
            sb.AppendLine("; Idle without a linked peer, seconds");
            sb.AppendLine("idleSeconds=600");
            sb.AppendLine("lifetimeSeconds=43200");
            sb.AppendLine("maxPdpBytes=16384");
            sb.AppendLine("; PPSSPP clients may differ in Network -> Port offset (0 on desktop, 10000 on Android).");
            sb.AppendLine("; Translate ports by that delta so their sessions match; false = strict aemu behaviour.");
            sb.AppendLine("portTranslation=true");
            sb.AppendLine("; Log a hint if PPSSPP uses UDP/ENet relay mode (AdhocUseUdp=Yes) which we do not speak");
            sb.AppendLine("udpProbe=true");
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