using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dpt.AdHoc.Core;
using Dpt.AdHoc.Net;

namespace Dpt.AdHoc.Web
{
    /// <summary>
    /// HTTP API. Two faces:
    ///   Public - read only, redacted (no MAC/IP/session ids/logs/moderation), safe to expose.
    ///   Admin  - everything, protected by signed + sealed requests (see ApiCrypto).
    /// </summary>
    public static class WebApi
    {
        private static readonly string[] PublicEventTypes =
            new string[] { "join", "leave", "game.new", "group.join", "group.leave" };

        public static bool Handle(WebServer web, HttpRequest req, HttpResponse resp, string clientIp)
        {
            if (web.Role == WebRole.Public) return HandlePublic(web, req, resp);
            return HandleAdmin(web, req, resp);
        }

        // ------------------------------------------------------------- public

        private static bool HandlePublic(WebServer web, HttpRequest req, HttpResponse resp)
        {
            string p = (req.Path ?? "").ToLowerInvariant();
            string method = req.Method ?? "GET";
            bool isGet = method == "GET" || method == "HEAD";

            switch (p)
            {
                case "/status.xml":
                    if (!isGet) return false;
                    web.SendXml(req, resp, 200, BuildStatusXml(web, true));
                    return true;

                case "/api/health":
                    web.SendJson(req, resp, 200, BuildHealth(web));
                    return true;

                case "/api/status":
                    if (!isGet) return false;
                    web.SendJson(req, resp, 200, BuildStatus(web, true));
                    return true;

                case "/api/games":
                    if (!isGet) return false;
                    web.SendJson(req, resp, 200, BuildGames(web, true));
                    return true;

                case "/api/history":
                    if (!isGet) return false;
                    web.SendJson(req, resp, 200, BuildHistory(web, req, true));
                    return true;

                default:
                    return false;
            }
        }

        // -------------------------------------------------------------- admin

        private static bool HandleAdmin(WebServer web, HttpRequest req, HttpResponse resp)
        {
            string p = (req.Path ?? "").ToLowerInvariant();
            string method = req.Method ?? "GET";
            bool isGet = method == "GET" || method == "HEAD";

            if (p == "/api/health")
            {
                web.SendJson(req, resp, 200, BuildHealth(web));
                return true;
            }

            if (p == "/api/auth")
            {
                if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                web.SendJson(req, resp, 200, BuildAuthInfo(web));
                return true;
            }

            AuthContext auth = web.Authenticate(req);
            if (!auth.Ok)
            {
                string body = "{\"ok\":false,\"error\":" + Json.Quote(auth.Error) +
                              ",\"auth\":true,\"code\":\"" + (auth.Blocked ? "blocked" : "signature") + "\"}";
                web.SendJson(req, resp, auth.Blocked ? 429 : 401, body);
                return true;
            }

            bool handled = DispatchAdmin(web, req, resp, p, method, isGet);
            if (handled) web.SealResponse(auth, resp);
            return handled;
        }

        private static bool DispatchAdmin(WebServer web, HttpRequest req, HttpResponse resp, string p,
                                          string method, bool isGet)
        {
            switch (p)
            {
                case "/status.xml":
                    if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    web.SendXml(req, resp, 200, BuildStatusXml(web, false));
                    return true;

                case "/api/status":
                    if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    web.SendJson(req, resp, 200, BuildStatus(web, false));
                    return true;

                case "/api/games":
                    if (isGet) { web.SendJson(req, resp, 200, BuildGames(web, false)); return true; }
                    if (method == "POST") { HandleGameSave(web, req, resp); return true; }
                    web.SendJson(req, resp, 405, Err("method not allowed"));
                    return true;

                case "/api/history":
                    if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    web.SendJson(req, resp, 200, BuildHistory(web, req, false));
                    return true;

                case "/api/logs":
                    if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    if (!web.Config.ExposeLogs) { web.SendJson(req, resp, 403, Err("logs are disabled by config")); return true; }
                    web.SendJson(req, resp, 200, BuildLogs(web, req));
                    return true;

                case "/api/bans":
                    if (!isGet) { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    web.SendJson(req, resp, 200, BuildBans(web));
                    return true;

                case "/api/bans/clear":
                    if (method != "POST") { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    {
                        int n = web.App.Adhoc == null ? 0 : web.App.Adhoc.ClearBans();
                        web.SendJson(req, resp, 200, "{\"ok\":true,\"cleared\":" + Json.Number(n) + "}");
                    }
                    return true;

                case "/api/kick":
                    if (method != "POST") { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    HandleKick(web, req, resp);
                    return true;

                case "/api/chat":
                    if (method != "POST") { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    HandleChat(web, req, resp);
                    return true;

                case "/api/reload":
                    if (method != "POST") { web.SendJson(req, resp, 405, Err("method not allowed")); return true; }
                    web.App.Db.Reload();
                    RefreshLiveGameNames(web);
                    web.SendJson(req, resp, 200, "{\"ok\":true,\"games\":" + Json.Number(web.App.Db.Count) + "}");
                    return true;

                default:
                    return false;
            }
        }

        private static string Err(string message)
        {
            return "{\"ok\":false,\"error\":" + Json.Quote(message) + "}";
        }

        // --------------------------------------------------------------- info

        private static string BuildHealth(WebServer web)
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("{\"ok\":true,\"name\":").Append(Json.Quote(web.App.Name))
              .Append(",\"version\":").Append(Json.Quote(web.App.Version))
              .Append(",\"role\":").Append(Json.Quote(web.RoleName))
              .Append(",\"uptimeSec\":").Append(Json.Number((long)web.App.Uptime.TotalSeconds))
              .Append('}');
            return sb.ToString();
        }

        private static string BuildAuthInfo(WebServer web)
        {
            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("scheme", ApiCrypto.Scheme);
            jb.Prop("salt", web.Config.AdminSalt);
            jb.Prop("iterations", web.Config.AdminKdfIterations);
            jb.Prop("encrypt", web.Config.AdminEncrypt);
            jb.Prop("role", "admin");
            jb.Prop("skewSec", ApiCrypto.MaxClockSkewSeconds);
            jb.Prop("ts", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            jb.EndObject();
            return jb.ToString();
        }

        // ------------------------------------------------------------- status

        private static string BuildStatus(WebServer web, bool redact)
        {
            ServerApp app = web.App;
            Config cfg = web.Config;
            ServerState st = app.State;
            GameDatabase db = app.Db;

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("nowUtc", Util.Iso(DateTime.UtcNow));
            jb.Prop("role", redact ? "public" : "admin");

            jb.Name("server").BeginObject();
            jb.Prop("name", app.Name);
            jb.Prop("version", app.Version);
            jb.Prop("build", app.BuildStamp);
            jb.Prop("startedUtc", Util.Iso(app.StartedUtc));
            jb.Prop("uptimeSec", (long)app.Uptime.TotalSeconds);
            jb.Prop("adhocPort", app.Adhoc != null ? app.Adhoc.Port : 0);
            jb.Prop("webPort", web.Port);
            jb.Prop("role", web.RoleName);
            jb.Prop("protocol", "PRO Online / PPSSPP adhocctl");
            if (!redact)
            {
                jb.Prop("webUrl", web.BaseUrl);
                jb.Prop("host", Environment.MachineName);
                jb.Prop("os", Environment.OSVersion.VersionString);
                jb.Prop("clr", Environment.Version.ToString());
                jb.Prop("cpus", Environment.ProcessorCount);
                jb.Prop("memoryMB", Math.Round(app.WorkingSetBytes / 1048576.0, 1));
                jb.Prop("logLevel", Log.MinLevel.ToString());
            }
            jb.EndObject();

            long seq;
            int online, peak, totalGames, totalGroups, historyCount, eventsCount;
            long totalConnections, totalLogins, chats, bytesIn, bytesOut, timeouts, protoErrors, kicks, bans, dups;

            jb.Name("games").BeginArray();
            lock (st.Sync)
            {
                List<GameEntry> games = new List<GameEntry>(st.Games.Values);
                games.Sort(delegate (GameEntry a, GameEntry b)
                {
                    int c = b.Sessions.Count.CompareTo(a.Sessions.Count);
                    if (c != 0) return c;
                    return string.Compare(db.DisplayName(a.Id), db.DisplayName(b.Id), StringComparison.OrdinalIgnoreCase);
                });

                for (int gi = 0; gi < games.Count; gi++)
                {
                    GameEntry g = games[gi];
                    jb.BeginObject();
                    jb.Prop("id", g.Id);
                    if (!redact) jb.Prop("rawId", g.RawId);
                    jb.Prop("name", db.DisplayName(g.Id));
                    jb.Prop("known", db.HasTitle(g.Id));
                    jb.Prop("players", g.Sessions.Count);
                    jb.Prop("peak", g.PeakPlayers);
                    jb.Prop("totalPlayers", g.TotalPlayers);
                    if (!redact) jb.Prop("createdUtc", Util.Iso(g.CreatedUtc));
                    jb.Prop("lastActivityUtc", Util.Iso(g.LastActivityUtc));

                    jb.Name("groups").BeginArray();
                    for (int gidx = 0; gidx < g.Groups.Count; gidx++)
                    {
                        GroupEntry group = g.Groups[gidx];
                        jb.BeginObject();
                        jb.Prop("name", group.Name);
                        jb.Prop("players", group.Sessions.Count);
                        if (!redact) jb.Prop("host", group.Founder != null ? group.Founder.MacText : "");
                        if (!redact) jb.Prop("createdUtc", Util.Iso(group.CreatedUtc));
                        jb.Name("members").BeginArray();
                        for (int si = 0; si < group.Sessions.Count; si++)
                            WritePlayer(jb, group.Sessions[si], group.Founder == group.Sessions[si], redact);
                        jb.EndArray();
                        jb.EndObject();
                    }
                    jb.EndArray();

                    jb.Name("idle").BeginArray();
                    for (int si = 0; si < g.Sessions.Count; si++)
                    {
                        ClientSession s = g.Sessions[si];
                        if (s.Group != null) continue;
                        WritePlayer(jb, s, false, redact);
                    }
                    jb.EndArray();
                    jb.EndObject();
                }

                online = st.Sessions.Count;
                peak = st.PeakOnline;
                totalGames = st.Games.Count;
                totalGroups = 0;
                foreach (KeyValuePair<string, GameEntry> kv in st.Games) totalGroups += kv.Value.Groups.Count;
                historyCount = st.History.Count;
                eventsCount = st.Events.Count;
                totalConnections = st.TotalConnections;
                totalLogins = st.TotalLogins;
                chats = st.TotalChats;
                bytesIn = 0;
                bytesOut = 0;
                foreach (KeyValuePair<string, ClientSession> skv in st.Sessions)
                {
                    bytesIn += skv.Value.BytesIn;
                    bytesOut += skv.Value.BytesOut;
                }
                timeouts = st.TimedOutSessions;
                protoErrors = st.ProtocolErrors;
                kicks = st.Kicks;
                bans = st.Bans;
                dups = st.DuplicateMacs;
                seq = st.EventSeq;
            }
            jb.EndArray();

            jb.Name("stats").BeginObject();
            jb.Prop("online", online);
            jb.Prop("peak", peak);
            jb.Prop("games", totalGames);
            jb.Prop("groups", totalGroups);
            jb.Prop("connections", totalConnections);
            jb.Prop("chats", chats);
            jb.Prop("timeouts", timeouts);
            jb.Prop("events", eventsCount);
            if (!redact)
            {
                jb.Prop("logins", totalLogins);
                jb.Prop("protocolErrors", protoErrors);
                jb.Prop("kicks", kicks);
                jb.Prop("bans", bans);
                jb.Prop("duplicateMacs", dups);
                jb.Prop("history", historyCount);
                jb.Prop("bytesIn", bytesIn);
                jb.Prop("bytesOut", bytesOut);
                jb.Prop("webRequests", web.Requests);
            }
            jb.Prop("eventSeq", seq);
            jb.EndObject();

            jb.Name("recent").BeginArray();
            lock (st.Sync)
            {
                int from = Math.Max(0, st.History.Count - 30);
                for (int i = st.History.Count - 1; i >= from; i--)
                {
                    HistoryEntry h = st.History[i];
                    jb.BeginObject();
                    jb.Prop("name", h.Name);
                    jb.Prop("gameId", h.GameId);
                    jb.Prop("gameName", h.GameName);
                    if (!redact)
                    {
                        jb.Prop("mac", h.Mac);
                        jb.Prop("ip", h.Ip);
                    }
                    jb.Prop("group", h.Group);
                    jb.Prop("connectUtc", Util.Iso(h.ConnectUtc));
                    jb.Prop("disconnectUtc", Util.Iso(h.DisconnectUtc));
                    jb.Prop("durationSec", h.DurationSec);
                    jb.Prop("reason", h.Reason);
                    jb.EndObject();
                }
            }
            jb.EndArray();

            jb.Name("events").BeginArray();
            lock (st.Sync)
            {
                int from = Math.Max(0, st.Events.Count - 60);
                for (int i = from; i < st.Events.Count; i++)
                {
                    ServerEvent e = st.Events[i];
                    if (redact && Array.IndexOf(PublicEventTypes, e.Type) < 0) continue;
                    jb.BeginObject();
                    jb.Prop("seq", e.Seq);
                    jb.Prop("timeUtc", Util.Iso(e.Utc));
                    jb.Prop("type", e.Type);
                    if (!redact) jb.Prop("gameId", e.GameId);
                    jb.Prop("gameName", e.GameName);
                    jb.Prop("player", e.Player);
                    jb.Prop("text", e.Text);
                    jb.EndObject();
                }
            }
            jb.EndArray();

            jb.Name("unknownIds").BeginArray();
            lock (st.Sync)
            {
                foreach (KeyValuePair<string, GameEntry> kv in st.Games)
                    if (!db.HasTitle(kv.Key)) jb.Str(kv.Key);
            }
            jb.EndArray();

            jb.Name("config").BeginObject();
            jb.Prop("theme", cfg.DefaultTheme);
            jb.Prop("sound", cfg.DefaultSound);
            jb.Prop("refreshMs", cfg.RefreshMs);
            jb.Prop("timeoutSec", cfg.UserTimeoutSec);
            jb.Prop("maxUsers", cfg.MaxUsers);
            jb.Prop("dbGames", db.Count);
            jb.Prop("dbCrosslinks", db.CrosslinkCount);
            jb.Prop("role", web.RoleName);
            jb.Prop("auth", web.IsAdmin);
            if (!redact)
            {
                jb.Prop("exposeLogs", cfg.ExposeLogs);
                jb.Prop("historySize", cfg.HistorySize);
                jb.Prop("eventSize", cfg.EventSize);
            }
            jb.EndObject();

            jb.EndObject();
            return jb.ToString();
        }

        private static void WritePlayer(JsonBuilder jb, ClientSession s, bool isHost, bool redact)
        {
            jb.BeginObject();
            if (!redact) jb.Prop("id", s.Id);
            jb.Prop("name", s.Nickname);
            if (!redact)
            {
                jb.Prop("mac", s.MacText);
                jb.Prop("ip", s.RemoteIp);
            }
            jb.Prop("connectUtc", Util.Iso(s.ConnectUtc));
            jb.Prop("connectedSec", (long)s.ConnectedFor.TotalSeconds);
            if (!redact)
            {
                jb.Prop("idleSec", (long)s.IdleFor.TotalSeconds);
                jb.Prop("bytesIn", s.BytesIn);
                jb.Prop("bytesOut", s.BytesOut);
                jb.Prop("pings", s.Pings);
            }
            jb.Prop("isHost", isHost);
            jb.EndObject();
        }

        private static string BuildStatusXml(WebServer web, bool redact)
        {
            ServerState st = web.App.State;
            GameDatabase db = web.App.Db;
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");

            lock (st.Sync)
            {
                sb.Append("<prometheus usercount=\"").Append(st.Sessions.Count.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
                foreach (KeyValuePair<string, GameEntry> kv in st.Games)
                {
                    GameEntry g = kv.Value;
                    sb.Append("\t<game name=\"").Append(Xml(db.DisplayName(g.Id))).Append("\" usercount=\"")
                      .Append(g.Sessions.Count.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
                    int active = 0;
                    for (int i = 0; i < g.Groups.Count; i++)
                    {
                        GroupEntry group = g.Groups[i];
                        sb.Append("\t\t<group name=\"").Append(Xml(group.Name)).Append("\" usercount=\"")
                          .Append(group.Sessions.Count.ToString(CultureInfo.InvariantCulture)).Append("\">\n");
                        for (int s = 0; s < group.Sessions.Count; s++)
                            sb.Append("\t\t\t<user>").Append(Xml(group.Sessions[s].Nickname)).Append("</user>\n");
                        sb.Append("\t\t</group>\n");
                        active += group.Sessions.Count;
                    }
                    if (g.Sessions.Count > active)
                    {
                        sb.Append("\t\t<group name=\"Groupless\" usercount=\"")
                          .Append((g.Sessions.Count - active).ToString(CultureInfo.InvariantCulture)).Append("\" />\n");
                    }
                    sb.Append("\t</game>\n");
                }
                sb.Append("</prometheus>");
            }
            return sb.ToString();
        }

        private static string Xml(string text)
        {
            if (text == null) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // -------------------------------------------------------------- games

        private static string BuildGames(WebServer web, bool redact)
        {
            GameDatabase db = web.App.Db;
            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("count", db.Count);
            jb.Prop("crosslinks", db.CrosslinkCount);
            jb.Name("games").BeginArray();
            List<KeyValuePair<string, string>> list = db.Snapshot();
            Dictionary<string, string> links = db.SnapshotLinks();
            for (int i = 0; i < list.Count; i++)
            {
                jb.BeginObject();
                jb.Prop("id", list[i].Key);
                jb.Prop("name", list[i].Value);
                string link;
                if (links.TryGetValue(list[i].Key, out link)) jb.Prop("crosslink", link);
                jb.EndObject();
            }
            jb.EndArray();
            jb.Name("unknownIds").BeginArray();
            lock (web.App.State.Sync)
            {
                foreach (KeyValuePair<string, GameEntry> kv in web.App.State.Games)
                    if (!db.HasTitle(kv.Key)) jb.Str(kv.Key);
            }
            jb.EndArray();
            jb.Prop("role", web.RoleName);
            jb.EndObject();
            return jb.ToString();
        }

        private static void HandleGameSave(WebServer web, HttpRequest req, HttpResponse resp)
        {
            string body = ReadBody(web, req, resp, 64 * 1024);
            if (body == null) return;

            Dictionary<string, object> request;
            try { request = Json.ParseObject(body); }
            catch (Exception ex) { web.SendJson(req, resp, 400, Err("invalid json: " + ex.Message)); return; }

            string id = GameDatabase.NormalizeId(Json.GetString(request, "id", ""));
            string name = Util.Clean(Json.GetString(request, "name", ""), 160);
            string crosslink = GameDatabase.NormalizeId(Json.GetString(request, "crosslink", ""));

            if (id.Length != 9)
            {
                web.SendJson(req, resp, 400, Err("id must be a 9 character product code like ULUS10511"));
                return;
            }
            if (name.Length == 0)
            {
                web.SendJson(req, resp, 400, Err("name is required"));
                return;
            }

            web.App.Db.SetName(id, name);
            if (crosslink.Length == 9) web.App.Db.SetCrosslink(id, crosslink);
            RefreshLiveGameNames(web);

            Log.Info("WEB", "game title saved: " + id + " = " + name +
                            (crosslink.Length == 9 ? " (crosslink -> " + crosslink + ")" : ""));

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("id", id);
            jb.Prop("name", name);
            jb.Prop("crosslink", crosslink);
            jb.Prop("count", web.App.Db.Count);
            jb.EndObject();
            web.SendJson(req, resp, 200, jb.ToString());
        }

        private static void RefreshLiveGameNames(WebServer web)
        {
            lock (web.App.State.Sync)
            {
                foreach (KeyValuePair<string, GameEntry> kv in web.App.State.Games)
                    kv.Value.Name = web.App.Db.DisplayName(kv.Key);
            }
        }

        // ------------------------------------------------------------ history

        private static string BuildHistory(WebServer web, HttpRequest req, bool redact)
        {
            int limit = 100;
            string l = req.QueryString("limit");
            if (!string.IsNullOrEmpty(l))
            {
                int parsed;
                if (int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    limit = Config.Clamp(parsed, 1, 2000);
            }

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Name("history").BeginArray();
            lock (web.App.State.Sync)
            {
                List<HistoryEntry> h = web.App.State.History;
                int from = Math.Max(0, h.Count - limit);
                for (int i = h.Count - 1; i >= from; i--)
                {
                    HistoryEntry e = h[i];
                    jb.BeginObject();
                    jb.Prop("name", e.Name);
                    jb.Prop("gameId", e.GameId);
                    jb.Prop("gameName", e.GameName);
                    jb.Prop("group", e.Group);
                    jb.Prop("connectUtc", Util.Iso(e.ConnectUtc));
                    jb.Prop("disconnectUtc", Util.Iso(e.DisconnectUtc));
                    jb.Prop("durationSec", e.DurationSec);
                    jb.Prop("reason", e.Reason);
                    if (!redact)
                    {
                        jb.Prop("mac", e.Mac);
                        jb.Prop("ip", e.Ip);
                        jb.Prop("bytesIn", e.BytesIn);
                        jb.Prop("bytesOut", e.BytesOut);
                    }
                    jb.EndObject();
                }
            }
            jb.EndArray();
            jb.EndObject();
            return jb.ToString();
        }

        // --------------------------------------------------------------- logs

        private static string BuildLogs(WebServer web, HttpRequest req)
        {
            long since = 0;
            string s = req.QueryString("since");
            if (!string.IsNullOrEmpty(s)) long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out since);

            int limit = 200;
            string l = req.QueryString("limit");
            if (!string.IsNullOrEmpty(l))
            {
                int parsed;
                if (int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    limit = Config.Clamp(parsed, 1, 1000);
            }

            LogLevel? minLevel = null;
            string lvl = req.QueryString("level");
            if (!string.IsNullOrEmpty(lvl) && !string.Equals(lvl, "all", StringComparison.OrdinalIgnoreCase))
                minLevel = Config.ParseLevel(lvl);

            string cat = req.QueryString("cat");

            List<LogEntry> entries = Log.Tail(since, limit, minLevel, cat);

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("seq", Log.LastSeq);
            jb.Name("lines").BeginArray();
            for (int i = 0; i < entries.Count; i++)
            {
                LogEntry e = entries[i];
                jb.BeginObject();
                jb.Prop("seq", e.Seq);
                jb.Prop("t", e.TimeText());
                jb.Prop("utc", Util.Iso(e.Utc));
                jb.Prop("lvl", Log.LevelLetter(e.Level));
                jb.Prop("cat", e.Category);
                jb.Prop("msg", e.Message);
                jb.EndObject();
            }
            jb.EndArray();
            jb.EndObject();
            return jb.ToString();
        }

        // --------------------------------------------------------------- bans

        private static string BuildBans(WebServer web)
        {
            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("count", web.App.Adhoc == null ? 0 : web.App.Adhoc.BanCount);
            jb.Name("bans").BeginArray();
            if (web.App.Adhoc != null)
            {
                List<string> list = web.App.Adhoc.BanSnapshot();
                for (int i = 0; i < list.Count; i++)
                {
                    string[] parts = list[i].Split('|');
                    jb.BeginObject();
                    jb.Prop("ip", parts[0]);
                    jb.Prop("secondsLeft", parts.Length > 1 ? parts[1] : "0");
                    jb.EndObject();
                }
            }
            jb.EndArray();
            jb.EndObject();
            return jb.ToString();
        }

        // -------------------------------------------------------------- admin

        private static void HandleKick(WebServer web, HttpRequest req, HttpResponse resp)
        {
            string body = ReadBody(web, req, resp, 16 * 1024);
            if (body == null) return;

            Dictionary<string, object> request = null;
            if (!string.IsNullOrEmpty(body))
            {
                try { request = Json.ParseObject(body); }
                catch (Exception ex) { web.SendJson(req, resp, 400, Err("invalid json: " + ex.Message)); return; }
            }

            string selector = "";
            if (request != null)
            {
                selector = Json.GetString(request, "id", "");
                if (selector.Length == 0) selector = Json.GetString(request, "mac", "");
                if (selector.Length == 0) selector = Json.GetString(request, "name", "");
            }
            if (selector.Length == 0) selector = req.QueryString("q") ?? "";
            if (selector.Length == 0) { web.SendJson(req, resp, 400, Err("id/mac/name selector required")); return; }

            string detail = "";
            int kicked = web.App.Adhoc == null ? 0 : web.App.Adhoc.Kick(selector, out detail);

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", kicked > 0);
            jb.Prop("kicked", kicked);
            jb.Prop("detail", detail);
            jb.EndObject();
            web.SendJson(req, resp, kicked > 0 ? 200 : 404, jb.ToString());
        }

        private static void HandleChat(WebServer web, HttpRequest req, HttpResponse resp)
        {
            string body = ReadBody(web, req, resp, 16 * 1024);
            if (body == null) return;

            string text = "";
            string sender = "SERVER";
            if (!string.IsNullOrEmpty(body))
            {
                Dictionary<string, object> request;
                try { request = Json.ParseObject(body); }
                catch (Exception ex) { web.SendJson(req, resp, 400, Err("invalid json: " + ex.Message)); return; }
                text = Json.GetString(request, "text", "");
                string s = Json.GetString(request, "sender", "");
                if (!string.IsNullOrEmpty(s)) sender = Util.Clean(s, 31);
            }
            if (text.Length == 0) text = req.QueryString("text") ?? "";
            text = Util.Clean(text, Packets.MessageSize - 1);
            if (text.Length == 0) { web.SendJson(req, resp, 400, Err("text is required")); return; }

            int sent = web.App.Adhoc == null ? 0 : web.App.Adhoc.BroadcastChat(text, sender);

            JsonBuilder jb = new JsonBuilder();
            jb.BeginObject();
            jb.Prop("ok", true);
            jb.Prop("sent", sent);
            jb.Prop("text", text);
            jb.EndObject();
            web.SendJson(req, resp, 200, jb.ToString());
        }

        private static string ReadBody(WebServer web, HttpRequest req, HttpResponse resp, int max)
        {
            if (req.Body != null && req.Body.Length > max)
            {
                web.SendJson(req, resp, 413, Err("body too large"));
                return null;
            }
            return req.BodyText();
        }
    }
}