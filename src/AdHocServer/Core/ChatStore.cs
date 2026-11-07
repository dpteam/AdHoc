using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// The board's own chat ("shoutbox"): a small ring of messages that visitors can read
    /// without an account and post to after solving a captcha, with a per-visitor cooldown.
    /// Optionally mirrored into the in-game chat so players in PPSSPP see it too.
    /// </summary>
    public sealed class ChatStore
    {
        public sealed class Message
        {
            public long Seq;
            public DateTime Utc;
            public string Nick = "";
            public string Text = "";
            public string Source = "web"; // web | game | server
        }

        public sealed class Challenge
        {
            public string Id = "";
            public string Code = "";
            public DateTime ExpiresUtc;
        }

        private sealed class RateState
        {
            public DateTime LastPostUtc;
            public readonly List<DateTime> Window = new List<DateTime>();
            public DateTime LastTextUtc;
            public string LastText = "";
        }

        private readonly object _sync = new object();
        private readonly List<Message> _messages = new List<Message>();
        private readonly Dictionary<string, Challenge> _challenges = new Dictionary<string, Challenge>(StringComparer.Ordinal);
        private readonly Dictionary<string, RateState> _rates = new Dictionary<string, RateState>(StringComparer.Ordinal);

        private long _seq;
        private string _path = "";
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        public int KeepMessages = 120;          // persisted and replayed on start
        public int MaxMessages = 250;           // in memory
        public int CaptchaTtlSec = 240;
        public int CooldownSec = 20;
        public int WindowMinutes = 10;
        public int MaxPerWindow = 8;
        public int MaxLength = 200;
        public int MaxNick = 16;

        // ------------------------------------------------------------- messages

        public List<Message> Since(long since, int max)
        {
            List<Message> outList = new List<Message>();
            lock (_sync)
            {
                int from = 0;
                if (since > 0)
                {
                    // messages are appended in order, so a linear scan from the end is fine
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        if (_messages[i].Seq <= since) { from = i + 1; break; }
                    }
                }
                int count = _messages.Count - from;
                if (max > 0 && count > max) from = _messages.Count - max;
                for (int i = from; i < _messages.Count; i++) outList.Add(Clone(_messages[i]));
            }
            return outList;
        }

        public long LastSeq { get { lock (_sync) { return _seq; } } }

        public Message Append(string nick, string text, string source)
        {
            Message m = new Message();
            m.Utc = DateTime.UtcNow;
            m.Nick = SanitizeNick(nick);
            m.Text = SanitizeText(text);
            m.Source = string.IsNullOrEmpty(source) ? "web" : source;
            if (m.Text.Length == 0) return null;

            lock (_sync)
            {
                m.Seq = ++_seq;
                _messages.Add(m);
                if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
                _dirty = true;
            }
            return m;
        }

        /// <summary>Messages from players in game, so board visitors can follow the lobby chatter.</summary>
        public void AppendFromGame(string nick, string text)
        {
            Append(nick, text, "game");
        }

        // -------------------------------------------------------------- captcha

        public Challenge NewChallenge()
        {
            Challenge c = new Challenge();
            c.Id = Captcha.RandomId();
            c.Code = Captcha.NewCode();
            c.ExpiresUtc = DateTime.UtcNow.AddSeconds(CaptchaTtlSec);

            lock (_sync)
            {
                PruneChallengesLocked();
                if (_challenges.Count > 500) _challenges.Clear();
                _challenges[c.Id] = c;
            }
            // Debug only: lets tools/test-web.ps1 solve the captcha; never leaves the local log.
            Log.Debug("CHAT", "captcha " + c.Id + " = " + c.Code);
            return c;
        }

        public string Svg(Challenge c) { return Captcha.Svg(c.Code); }

        private bool CheckCaptchaLocked(string id, string answer)
        {
            if (string.IsNullOrEmpty(id)) return false;
            Challenge c;
            if (!_challenges.TryGetValue(id, out c)) return false;
            if (c.ExpiresUtc < DateTime.UtcNow) { _challenges.Remove(id); return false; }

            // single use: whatever the answer, the challenge is gone after one try
            _challenges.Remove(id);
            return string.Equals(c.Code, Captcha.Normalize(answer), StringComparison.Ordinal);
        }

        private void PruneChallengesLocked()
        {
            if (_challenges.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            List<string> gone = null;
            foreach (KeyValuePair<string, Challenge> kv in _challenges)
            {
                if (kv.Value.ExpiresUtc >= now) continue;
                if (gone == null) gone = new List<string>();
                gone.Add(kv.Key);
            }
            if (gone != null) for (int i = 0; i < gone.Count; i++) _challenges.Remove(gone[i]);
        }

        // ----------------------------------------------------------------- post

        public sealed class PostResult
        {
            public bool Ok;
            public string Error = "";
            public int RetryAfterSec;
            public Message Message;
            public Challenge Next;
        }

        /// <summary>
        /// Validates captcha + cooldown and appends the message. <paramref name="visitorId"/>
        /// is the anonymous hash from <see cref="VisitStore.Identity"/>.
        /// </summary>
        public PostResult Post(string visitorId, string nick, string text, string captchaId, string answer, bool skipCaptcha)
        {
            PostResult r = new PostResult();

            string cleanText = SanitizeText(text);
            if (cleanText.Length == 0) { r.Error = "empty"; return r; }

            lock (_sync)
            {
                PruneChallengesLocked();
                RateState st = StateLocked(visitorId);
                DateTime now = DateTime.UtcNow;

                int wait = 0;
                if (st.LastPostUtc != default(DateTime))
                {
                    double elapsed = (now - st.LastPostUtc).TotalSeconds;
                    if (elapsed < CooldownSec) wait = (int)Math.Ceiling(CooldownSec - elapsed);
                }
                if (wait > 0) { r.Error = "cooldown"; r.RetryAfterSec = wait; return r; }

                st.Window.RemoveAll(delegate(DateTime t) { return (now - t).TotalMinutes >= WindowMinutes; });
                if (st.Window.Count >= MaxPerWindow) { r.Error = "flood"; r.RetryAfterSec = 60; return r; }

                if (!skipCaptcha && !CheckCaptchaLocked(captchaId, answer)) { r.Error = "captcha"; r.RetryAfterSec = 0; return r; }

                if (string.Equals(st.LastText, cleanText, StringComparison.OrdinalIgnoreCase) &&
                    (now - st.LastTextUtc).TotalMinutes < 5)
                {
                    r.Error = "duplicate";
                    r.RetryAfterSec = 15;
                    return r;
                }

                Message m = new Message();
                m.Seq = ++_seq;
                m.Utc = now;
                m.Nick = SanitizeNick(nick);
                m.Text = cleanText;
                m.Source = "web";
                _messages.Add(m);
                if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
                _dirty = true;

                st.LastPostUtc = now;
                st.Window.Add(now);
                st.LastText = cleanText;
                st.LastTextUtc = now;

                r.Ok = true;
                r.Message = Clone(m);
            }

            return r;
        }

        public int CooldownLeft(string visitorId)
        {
            lock (_sync)
            {
                RateState st;
                if (!_rates.TryGetValue(visitorId, out st) || st.LastPostUtc == default(DateTime)) return 0;
                double elapsed = (DateTime.UtcNow - st.LastPostUtc).TotalSeconds;
                return elapsed >= CooldownSec ? 0 : (int)Math.Ceiling(CooldownSec - elapsed);
            }
        }

        private RateState StateLocked(string id)
        {
            RateState st;
            if (!_rates.TryGetValue(id, out st))
            {
                st = new RateState();
                _rates[id] = st;
            }
            return st;
        }

        // ------------------------------------------------------------- sanitize

        public string SanitizeNick(string nick)
        {
            string s = Util.Clean(nick, MaxNick * 2);
            StringBuilder sb = new StringBuilder(MaxNick);
            for (int i = 0; i < s.Length && sb.Length < MaxNick; i++)
            {
                char c = s[i];
                bool ok = char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.';
                if (ok) sb.Append(c);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "guest" : result;
        }

        public string SanitizeText(string text)
        {
            string s = Util.Clean(text, MaxLength * 2);
            if (s.Length == 0) return "";

            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            for (int i = 0; i < s.Length && sb.Length < MaxLength; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
                {
                    space = true;
                    continue;
                }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static Message Clone(Message m)
        {
            Message c = new Message();
            c.Seq = m.Seq;
            c.Utc = m.Utc;
            c.Nick = m.Nick;
            c.Text = m.Text;
            c.Source = m.Source;
            return c;
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
                Log.Warn("CHAT", "cannot read " + Path.GetFileName(path) + ": " + ex.Message + " (starting empty)");
                return;
            }
            if (root == null) return;

            lock (_sync)
            {
                List<object> list = Json.Get(root, "messages") as List<object>;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    Message m = new Message();
                    m.Nick = Json.GetString(d, "nick", "guest");
                    m.Text = Json.GetString(d, "text", "");
                    m.Source = Json.GetString(d, "src", "web");
                    m.Utc = Json.GetDate(d, "ts", DateTime.UtcNow);
                    m.Seq = (long)Json.GetInt(d, "seq", 0);
                    if (m.Text.Length == 0) continue;
                    if (m.Seq > _seq) _seq = m.Seq;
                    _messages.Add(m);
                }
                if (_messages.Count > KeepMessages) _messages.RemoveRange(0, _messages.Count - KeepMessages);
            }
        }

        public bool Save()
        {
            if (string.IsNullOrEmpty(_path)) return false;

            string json;
            lock (_sync)
            {
                if (!_dirty) return false;
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;

                JsonBuilder jb = new JsonBuilder(4096);
                jb.BeginObject();
                jb.Prop("version", 1);
                jb.Prop("updatedUtc", Util.Iso(DateTime.UtcNow));
                jb.Name("messages").BeginArray();
                int from = _messages.Count > KeepMessages ? _messages.Count - KeepMessages : 0;
                for (int i = from; i < _messages.Count; i++)
                {
                    Message m = _messages[i];
                    jb.BeginObject();
                    jb.Prop("seq", m.Seq);
                    jb.Prop("ts", Util.Iso(m.Utc));
                    jb.Prop("nick", m.Nick);
                    jb.Prop("text", m.Text);
                    jb.Prop("src", m.Source);
                    jb.EndObject();
                }
                jb.EndArray();
                jb.EndObject();
                json = jb.ToString();
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
                Log.Warn("CHAT", "cannot save " + Path.GetFileName(_path) + ": " + ex.Message);
                lock (_sync) { _dirty = true; }
                return false;
            }
        }

        public int Count { get { lock (_sync) { return _messages.Count; } } }

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
    }
}
