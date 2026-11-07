using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// "Who is looking at the board" statistics: today's visits, unique visitors,
    /// how many people are reading right now, and a small per-day history.
    ///
    /// Privacy: a visitor is identified by a truncated SHA-256 of IP + User-Agent + a
    /// server-side salt. The hash is never written to disk and never leaves the process -
    /// only counters are published. No IP, no UA, no cookie.
    /// </summary>
    public sealed class VisitStore
    {
        public sealed class Day
        {
            public string Date = "";
            public int Views;
            public int Visitors;
            public int Peak;
        }

        public sealed class Snapshot
        {
            public int Online;
            public int PeakOnline;
            public int TodayViews;
            public int TodayVisitors;
            public int TotalViews;
            public int TotalVisitors;
            public List<Day> Days = new List<Day>();
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Day> _days = new Dictionary<string, Day>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _live = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _lastVisit = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> _todayVisitors = new HashSet<string>(StringComparer.Ordinal);

        private string _path = "";
        private string _salt = "dpt-visits";
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;
        private string _today = "";
        private int _peakOnline;
        private int _totalViews;
        private int _totalVisitors;

        /// <summary>How long a board counts as "open" without a heartbeat.</summary>
        public int LiveWindowSec = 90;
        /// <summary>A returning reader is a new visit after this many minutes.</summary>
        public int VisitGapMin = 30;
        public int KeepDays = 60;

        public bool Enabled = true;

        // ------------------------------------------------------------- lifetime

        public void Configure(string path, string salt, bool enabled)
        {
            _path = path;
            _salt = string.IsNullOrEmpty(salt) ? "dpt-visits" : salt;
            Enabled = enabled;
        }

        /// <summary>One heartbeat from a browser. Returns the fresh counters.</summary>
        public Snapshot Touch(string ip, string userAgent)
        {
            DateTime now = DateTime.UtcNow;
            string id = Identity(ip, userAgent);

            lock (_sync)
            {
                if (!Enabled) return BuildLocked(now);

                RollDay(now);

                DateTime seen;
                bool wasLive = _live.TryGetValue(id, out seen) && (now - seen).TotalSeconds < LiveWindowSec;
                _live[id] = now;

                if (!wasLive)
                {
                    DateTime last;
                    bool seenBefore = _lastVisit.TryGetValue(id, out last);
                    bool countView = !seenBefore || (now - last).TotalMinutes >= VisitGapMin;

                    if (countView)
                    {
                        Day day = TodayLocked(now);
                        day.Views++;
                        _totalViews++;
                        _dirty = true;

                        if (_todayVisitors.Add(id))
                        {
                            day.Visitors++;
                            _totalVisitors++;
                        }
                    }
                    _lastVisit[id] = now;
                }

                int online = CountLiveLocked(now);
                Day today = TodayLocked(now);
                if (online > today.Peak) today.Peak = online;
                if (online > _peakOnline) _peakOnline = online;

                return BuildLocked(now);
            }
        }

        public Snapshot Current()
        {
            lock (_sync) { return BuildLocked(DateTime.UtcNow); }
        }

        private int CountLiveLocked(DateTime now)
        {
            int n = 0;
            foreach (KeyValuePair<string, DateTime> kv in _live)
                if ((now - kv.Value).TotalSeconds < LiveWindowSec) n++;
            return n;
        }

        private Day TodayLocked(DateTime now)
        {
            string key = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Day day;
            if (!_days.TryGetValue(key, out day))
            {
                day = new Day();
                day.Date = key;
                _days[key] = day;
                _dirty = true;
            }
            return day;
        }

        private void RollDay(DateTime now)
        {
            string key = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (key == _today) return;

            _today = key;
            _todayVisitors.Clear();
            TodayLocked(now);

            // drop very old days so the file stays small
            if (_days.Count > KeepDays + 10)
            {
                List<string> keys = new List<string>(_days.Keys);
                keys.Sort(StringComparer.Ordinal);
                int drop = keys.Count - KeepDays;
                for (int i = 0; i < drop; i++) _days.Remove(keys[i]);
                _dirty = true;
            }
        }

        private Snapshot BuildLocked(DateTime now)
        {
            RollDay(now);

            // prune stale live entries so the count does not drift
            if (_live.Count > 0)
            {
                List<string> gone = null;
                foreach (KeyValuePair<string, DateTime> kv in _live)
                {
                    if ((now - kv.Value).TotalSeconds < LiveWindowSec * 4) continue;
                    if (gone == null) gone = new List<string>();
                    gone.Add(kv.Key);
                }
                if (gone != null) for (int i = 0; i < gone.Count; i++) _live.Remove(gone[i]);
            }

            Snapshot s = new Snapshot();
            s.Online = CountLiveLocked(now);
            s.PeakOnline = _peakOnline;
            s.TotalViews = _totalViews;
            s.TotalVisitors = _totalVisitors;

            Day today = TodayLocked(now);
            s.TodayViews = today.Views;
            s.TodayVisitors = today.Visitors;

            List<string> keys = new List<string>(_days.Keys);
            keys.Sort(StringComparer.Ordinal);
            int from = keys.Count > 14 ? keys.Count - 14 : 0;
            for (int i = from; i < keys.Count; i++) s.Days.Add(Clone(_days[keys[i]]));

            return s;
        }

        private static Day Clone(Day d)
        {
            Day c = new Day();
            c.Date = d.Date;
            c.Views = d.Views;
            c.Visitors = d.Visitors;
            c.Peak = d.Peak;
            return c;
        }

        /// <summary>Stable, non-reversible visitor id (never stored, never exposed).</summary>
        public string Identity(string ip, string userAgent)
        {
            string raw = (ip ?? "") + "|" + (userAgent ?? "") + "|" + _salt;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ load/save

        public void Load(string path)
        {
            _path = path;
            if (!File.Exists(path)) return;

            Dictionary<string, object> root;
            try { root = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex)
            {
                Log.Warn("VISIT", "cannot read " + Path.GetFileName(path) + ": " + ex.Message + " (starting fresh)");
                return;
            }
            if (root == null) return;

            lock (_sync)
            {
                _totalViews = Json.GetInt(root, "totalViews", 0);
                _totalVisitors = Json.GetInt(root, "totalVisitors", 0);
                _peakOnline = Json.GetInt(root, "peakOnline", 0);

                List<object> days = Json.Get(root, "days") as List<object>;
                if (days != null)
                {
                    for (int i = 0; i < days.Count; i++)
                    {
                        Dictionary<string, object> d = days[i] as Dictionary<string, object>;
                        if (d == null) continue;
                        Day day = new Day();
                        day.Date = Json.GetString(d, "d", "");
                        day.Views = Json.GetInt(d, "v", 0);
                        day.Visitors = Json.GetInt(d, "u", 0);
                        day.Peak = Json.GetInt(d, "p", 0);
                        if (day.Date.Length == 10) _days[day.Date] = day;
                    }
                }
                _today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            Log.Info("VISIT", "board visits loaded: " + _totalViews + " visits, " + _totalVisitors +
                              " unique, peak online " + _peakOnline);
        }

        public bool Save()
        {
            if (string.IsNullOrEmpty(_path)) return false;

            string json;
            lock (_sync)
            {
                if (!_dirty) return false;
                json = BuildJsonLocked();
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
                    catch { File.Delete(_path); File.Move(tmp, _path); }
                }
                else File.Move(tmp, _path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("VISIT", "cannot save " + Path.GetFileName(_path) + ": " + ex.Message);
                lock (_sync) { _dirty = true; }
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

        private string BuildJsonLocked()
        {
            JsonBuilder jb = new JsonBuilder(2048);
            jb.BeginObject();
            jb.Prop("version", 1);
            jb.Prop("updatedUtc", Util.Iso(DateTime.UtcNow));
            jb.Prop("totalViews", _totalViews);
            jb.Prop("totalVisitors", _totalVisitors);
            jb.Prop("peakOnline", _peakOnline);

            jb.Name("days").BeginArray();
            List<string> keys = new List<string>(_days.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                Day d = _days[keys[i]];
                jb.BeginObject();
                jb.Prop("d", d.Date);
                jb.Prop("v", d.Views);
                jb.Prop("u", d.Visitors);
                jb.Prop("p", d.Peak);
                jb.EndObject();
            }
            jb.EndArray();

            jb.EndObject();
            return jb.ToString();
        }
    }
}
