using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Lifetime statistics that survive restarts: per-game peaks and play time, global counters
    /// and the tail of the session history. Kept in <c>data/stats.json</c>, written atomically
    /// (tmp + move) every minute when something changed and once more on shutdown.
    ///
    /// Live numbers stay in <see cref="ServerState"/>; this class only accumulates.
    /// </summary>
    public sealed class StatsStore
    {
        public sealed class GameTotals
        {
            public string Id = "";
            public string Name = "";
            public int Peak;
            public long Joins;
            public long Sessions;
            public double Seconds;
            public DateTime FirstSeenUtc = DateTime.UtcNow;
            public DateTime LastSeenUtc = DateTime.UtcNow;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, GameTotals> _games = new Dictionary<string, GameTotals>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();

        private string _path = "";
        private bool _dirty;
        private DateTime _loadedUtc = DateTime.UtcNow;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        public int PeakOnline;
        public long TotalConnections;
        public long TotalLogins;
        public long TotalChats;
        public long TotalSessions;
        public double TotalSeconds;
        public long BytesIn;
        public long BytesOut;
        public long Timeouts;
        public DateTime FirstStartUtc = DateTime.UtcNow;
        public int Starts;

        /// <summary>How many finished sessions to keep in the file (the board shows the newest first).</summary>
        public int HistoryKeep = 500;

        public string FilePath { get { return _path; } }
        public bool Dirty { get { lock (_sync) { return _dirty; } } }

        public int GameCount { get { lock (_sync) { return _games.Count; } } }

        // ------------------------------------------------------------- lifetime

        public GameTotals GameOf(string productId)
        {
            string id = GameDatabase.NormalizeId(productId);
            if (id.Length == 0) return null;
            lock (_sync)
            {
                GameTotals t;
                return _games.TryGetValue(id, out t) ? t : null;
            }
        }

        /// <summary>Snapshot for the API (copy, safe to read outside the lock).</summary>
        public List<GameTotals> GameSnapshot(int limit)
        {
            List<GameTotals> list = new List<GameTotals>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, GameTotals> kv in _games)
                {
                    GameTotals t = kv.Value;
                    GameTotals c = new GameTotals();
                    c.Id = t.Id; c.Name = t.Name; c.Peak = t.Peak; c.Joins = t.Joins;
                    c.Sessions = t.Sessions; c.Seconds = t.Seconds;
                    c.FirstSeenUtc = t.FirstSeenUtc; c.LastSeenUtc = t.LastSeenUtc;
                    list.Add(c);
                }
            }
            list.Sort(delegate(GameTotals a, GameTotals b)
            {
                int byPeak = b.Peak.CompareTo(a.Peak);
                return byPeak != 0 ? byPeak : b.Seconds.CompareTo(a.Seconds);
            });
            if (limit > 0 && list.Count > limit) list.RemoveRange(limit, list.Count - limit);
            return list;
        }

        /// <summary>Peak of a game as known from previous runs (0 when never seen).</summary>
        public int PeakOf(string productId)
        {
            GameTotals t = GameOf(productId);
            return t == null ? 0 : t.Peak;
        }

        public long JoinsOf(string productId)
        {
            GameTotals t = GameOf(productId);
            return t == null ? 0 : t.Joins;
        }

        /// <summary>Snapshot of the stored history (newest last), for seeding the live board.</summary>
        public List<HistoryEntry> HistorySnapshot(int limit)
        {
            lock (_sync)
            {
                int from = limit > 0 && _history.Count > limit ? _history.Count - limit : 0;
                return new List<HistoryEntry>(_history.GetRange(from, _history.Count - from));
            }
        }

        // ---------------------------------------------------------------- notes

        public void NoteLogin(string gameId, string gameName)
        {
            string id = GameDatabase.NormalizeId(gameId);
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                TotalLogins++;
                _dirty = true;
                if (id.Length == 0) return;
                GameTotals t;
                if (!_games.TryGetValue(id, out t))
                {
                    t = new GameTotals();
                    t.Id = id;
                    t.FirstSeenUtc = now;
                    _games[id] = t;
                }
                t.Joins++;
                if (!string.IsNullOrEmpty(gameName)) t.Name = gameName;
                t.LastSeenUtc = now;
            }
        }

        public void NoteOnline(int online)
        {
            if (online <= 0) return;
            lock (_sync)
            {
                if (online <= PeakOnline) return;
                PeakOnline = online;
                _dirty = true;
            }
        }

        public void NoteGamePeak(string gameId, int players)
        {
            if (players <= 0) return;
            string id = GameDatabase.NormalizeId(gameId);
            if (id.Length == 0) return;
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                GameTotals t;
                if (!_games.TryGetValue(id, out t))
                {
                    t = new GameTotals();
                    t.Id = id;
                    t.FirstSeenUtc = now;
                    _games[id] = t;
                }
                t.LastSeenUtc = now;
                if (players <= t.Peak) return;
                t.Peak = players;
                _dirty = true;
            }
        }

        public void NoteChat()
        {
            lock (_sync) { TotalChats++; _dirty = true; }
        }

        public void NoteConnection()
        {
            lock (_sync) { TotalConnections++; _dirty = true; }
        }

        public void NoteTraffic(long bytesIn, long bytesOut)
        {
            if (bytesIn <= 0 && bytesOut <= 0) return;
            lock (_sync)
            {
                BytesIn += bytesIn;
                BytesOut += bytesOut;
                _dirty = true;
            }
        }

        /// <summary>Folds one finished session into the lifetime numbers and the stored history.</summary>
        public void NoteSessionEnd(HistoryEntry h)
        {
            if (h == null) return;
            string id = GameDatabase.NormalizeId(h.GameId);
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                TotalSessions++;
                TotalSeconds += h.DurationSec;
                if (string.Equals(h.Reason, "timeout", StringComparison.OrdinalIgnoreCase)) Timeouts++;
                _dirty = true;

                if (id.Length > 0)
                {
                    GameTotals t;
                    if (!_games.TryGetValue(id, out t))
                    {
                        t = new GameTotals();
                        t.Id = id;
                        t.FirstSeenUtc = h.ConnectUtc;
                        _games[id] = t;
                    }
                    t.Sessions++;
                    t.Seconds += h.DurationSec;
                    if (!string.IsNullOrEmpty(h.GameName)) t.Name = h.GameName;
                    t.LastSeenUtc = now;
                }

                HistoryEntry copy = CopyHistory(h);
                _history.Add(copy);
                if (_history.Count > HistoryKeep) _history.RemoveRange(0, _history.Count - HistoryKeep);
            }
        }

        // ------------------------------------------------------------- load/save

        public void Load(string path, string dataDir)
        {
            _path = path;
            _loadedUtc = DateTime.UtcNow;

            Dictionary<string, object> root = null;
            try
            {
                if (!File.Exists(path)) return;
                root = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Warn("STATS", "cannot read " + Path.GetFileName(path) + ": " + ex.Message + " (starting fresh)");
                return;
            }
            if (root == null) return;

            lock (_sync)
            {
                PeakOnline = Json.GetInt(root, "peakOnline", 0);
                TotalConnections = (long)Json.GetInt(root, "totalConnections", 0);
                TotalLogins = (long)Json.GetInt(root, "totalLogins", 0);
                TotalChats = (long)Json.GetInt(root, "totalChats", 0);
                TotalSessions = (long)Json.GetInt(root, "totalSessions", 0);
                TotalSeconds = Json.GetDouble(root, "totalSeconds", 0);
                BytesIn = (long)Json.GetDouble(root, "bytesIn", 0);
                BytesOut = (long)Json.GetDouble(root, "bytesOut", 0);
                Timeouts = (long)Json.GetInt(root, "timeouts", 0);
                Starts = Json.GetInt(root, "starts", 0);
                FirstStartUtc = Json.GetDate(root, "firstStartUtc", _loadedUtc);

                List<object> games = Json.Get(root, "games") as List<object>;
                if (games != null)
                {
                    for (int i = 0; i < games.Count; i++)
                    {
                        Dictionary<string, object> g = games[i] as Dictionary<string, object>;
                        if (g == null) continue;
                        string id = GameDatabase.NormalizeId(Json.GetString(g, "id", ""));
                        if (id.Length == 0) continue;
                        GameTotals t = new GameTotals();
                        t.Id = id;
                        t.Name = Json.GetString(g, "name", "");
                        t.Peak = Json.GetInt(g, "peak", 0);
                        t.Joins = (long)Json.GetInt(g, "joins", 0);
                        t.Sessions = (long)Json.GetInt(g, "sessions", 0);
                        t.Seconds = Json.GetDouble(g, "seconds", 0);
                        t.FirstSeenUtc = Json.GetDate(g, "firstSeenUtc", _loadedUtc);
                        t.LastSeenUtc = Json.GetDate(g, "lastSeenUtc", _loadedUtc);
                        _games[id] = t;
                    }
                }

                List<object> history = Json.Get(root, "history") as List<object>;
                if (history != null)
                {
                    for (int i = 0; i < history.Count; i++)
                    {
                        Dictionary<string, object> h = history[i] as Dictionary<string, object>;
                        if (h == null) continue;
                        HistoryEntry e = new HistoryEntry();
                        e.Name = Json.GetString(h, "name", "");
                        e.GameId = Json.GetString(h, "gameId", "");
                        e.GameName = Json.GetString(h, "gameName", "");
                        e.Group = Json.GetString(h, "group", "");
                        e.Reason = Json.GetString(h, "reason", "");
                        e.ConnectUtc = Json.GetDate(h, "connectUtc", _loadedUtc);
                        e.DisconnectUtc = Json.GetDate(h, "disconnectUtc", _loadedUtc);
                        e.DurationSec = Json.GetDouble(h, "seconds", 0);
                        _history.Add(e);
                    }
                    if (_history.Count > HistoryKeep) _history.RemoveRange(0, _history.Count - HistoryKeep);
                }
            }

            Log.Info("STATS", "lifetime stats loaded: " + _games.Count + " games, peak online " + PeakOnline +
                              ", " + TotalSessions + " sessions, history " + _history.Count);
        }

        /// <summary>Marks a new run; call once after Load.</summary>
        public void NoteStart()
        {
            lock (_sync)
            {
                if (Starts == 0) FirstStartUtc = DateTime.UtcNow;
                Starts++;
                _dirty = true;
            }
        }

        public bool Save()
        {
            if (string.IsNullOrEmpty(_path)) return false;

            string json;
            lock (_sync)
            {
                if (!_dirty) return false;
                json = BuildJson();
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
            }

            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    try { File.Replace(tmp, _path, null); }
                    catch
                    {
                        File.Delete(_path);
                        File.Move(tmp, _path);
                    }
                }
                else
                {
                    File.Move(tmp, _path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("STATS", "cannot save " + Path.GetFileName(_path) + ": " + ex.Message);
                lock (_sync) { _dirty = true; } // try again on the next tick
                return false;
            }
        }

        /// <summary>Writes at most once every <paramref name="minSeconds"/>, and only when dirty.</summary>
        public bool SaveIfDue(int minSeconds)
        {
            if (string.IsNullOrEmpty(_path)) return false;
            lock (_sync)
            {
                if (!_dirty) return false;
                if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < minSeconds) return false;
            }
            return Save();
        }

        private string BuildJson()
        {
            JsonBuilder jb = new JsonBuilder(8192);
            jb.BeginObject();
            jb.Prop("version", 1);
            jb.Prop("updatedUtc", Util.Iso(DateTime.UtcNow));
            jb.Prop("firstStartUtc", Util.Iso(FirstStartUtc));
            jb.Prop("starts", Starts);
            jb.Prop("peakOnline", PeakOnline);
            jb.Prop("totalConnections", TotalConnections);
            jb.Prop("totalLogins", TotalLogins);
            jb.Prop("totalChats", TotalChats);
            jb.Prop("totalSessions", TotalSessions);
            jb.Prop("totalSeconds", Math.Round(TotalSeconds, 1));
            jb.Prop("bytesIn", BytesIn);
            jb.Prop("bytesOut", BytesOut);
            jb.Prop("timeouts", Timeouts);

            jb.Name("games").BeginArray();
            List<GameTotals> games = new List<GameTotals>(_games.Values);
            games.Sort(delegate(GameTotals a, GameTotals b)
            {
                int byPeak = b.Peak.CompareTo(a.Peak);
                if (byPeak != 0) return byPeak;
                int bySeconds = b.Seconds.CompareTo(a.Seconds);
                return bySeconds != 0 ? bySeconds : string.CompareOrdinal(a.Id, b.Id);
            });
            for (int i = 0; i < games.Count; i++)
            {
                GameTotals t = games[i];
                jb.BeginObject();
                jb.Prop("id", t.Id);
                jb.Prop("name", t.Name);
                jb.Prop("peak", t.Peak);
                jb.Prop("joins", t.Joins);
                jb.Prop("sessions", t.Sessions);
                jb.Prop("seconds", Math.Round(t.Seconds, 1));
                jb.Prop("firstSeenUtc", Util.Iso(t.FirstSeenUtc));
                jb.Prop("lastSeenUtc", Util.Iso(t.LastSeenUtc));
                jb.EndObject();
            }
            jb.EndArray();

            jb.Name("history").BeginArray();
            for (int i = 0; i < _history.Count; i++)
            {
                HistoryEntry h = _history[i];
                jb.BeginObject();
                jb.Prop("name", h.Name);
                jb.Prop("gameId", h.GameId);
                jb.Prop("gameName", h.GameName);
                jb.Prop("group", h.Group);
                jb.Prop("connectUtc", Util.Iso(h.ConnectUtc));
                jb.Prop("disconnectUtc", Util.Iso(h.DisconnectUtc));
                jb.Prop("seconds", h.DurationSec);
                jb.Prop("reason", h.Reason);
                jb.EndObject();
            }
            jb.EndArray();

            jb.EndObject();
            return jb.ToString();
        }

        private static HistoryEntry CopyHistory(HistoryEntry h)
        {
            HistoryEntry c = new HistoryEntry();
            c.Name = h.Name;
            c.GameId = h.GameId;
            c.GameName = h.GameName;
            c.Group = h.Group;
            c.ConnectUtc = h.ConnectUtc;
            c.DisconnectUtc = h.DisconnectUtc;
            c.DurationSec = h.DurationSec;
            c.Reason = h.Reason;
            c.BytesIn = h.BytesIn;
            c.BytesOut = h.BytesOut;
            return c;
        }
    }
}