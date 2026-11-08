using System;
using System.Collections.Generic;
using Dpt.AdHoc.Net;

namespace Dpt.AdHoc.Core
{
    public sealed class GameEntry
    {
        public string Id = "";
        public string RawId = "";
        /// <summary>Every raw product code seen for this game (e.g. EU and US copies).</summary>
        public readonly List<string> RawIds = new List<string>();
        public string Name = "";
        public readonly List<GroupEntry> Groups = new List<GroupEntry>();
        public readonly List<ClientSession> Sessions = new List<ClientSession>();
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime LastActivityUtc = DateTime.UtcNow;
        public int PeakPlayers;
        public int TotalPlayers;

        public GroupEntry FindGroup(string name)
        {
            for (int i = 0; i < Groups.Count; i++)
                if (string.Equals(Groups[i].Name, name, StringComparison.OrdinalIgnoreCase)) return Groups[i];
            return null;
        }
    }

    public sealed class GroupEntry
    {
        public string Name = "";
        public GameEntry Game;
        public readonly List<ClientSession> Sessions = new List<ClientSession>();
        public ClientSession Founder;
        public DateTime CreatedUtc = DateTime.UtcNow;
        /// <summary>How many different players ever joined, so we can spot "joined alone".</summary>
        public int EverJoined;
    }

    public sealed class HistoryEntry
    {
        public string Name = "";
        public string GameId = "";
        public string GameName = "";
        public string Mac = "";
        public string Ip = "";
        public string Group = "";
        public DateTime ConnectUtc;
        public DateTime DisconnectUtc;
        public double DurationSec;
        public string Reason = "";
        public long BytesIn;
        public long BytesOut;
    }

    public sealed class ServerEvent
    {
        public long Seq;
        public DateTime Utc;
        public string Type = "";
        public string GameId = "";
        public string GameName = "";
        public string Player = "";
        public string Text = "";
    }

    /// <summary>
    /// Live board: games -> groups -> sessions, plus history and an event ring for the UI.
    /// Everything here is guarded by <see cref="Sync"/>. Keep critical sections short:
    /// never perform socket IO while holding it.
    /// </summary>
    public sealed class ServerState
    {
        public readonly object Sync = new object();
        public readonly Dictionary<string, GameEntry> Games = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        public readonly List<HistoryEntry> History = new List<HistoryEntry>();
        public readonly List<ServerEvent> Events = new List<ServerEvent>();
        public readonly Dictionary<string, ClientSession> Sessions = new Dictionary<string, ClientSession>(StringComparer.OrdinalIgnoreCase);

        public int OnlineSessions;
        public int PeakOnline;
        public long TotalConnections;
        public long TotalLogins;
        public long TotalChats;
        public long TotalSendBytes;
        public long TotalRecvBytes;
        public long TimedOutSessions;
        public long ProtocolErrors;
        public long Kicks;
        public long Bans;
        public long DuplicateMacs;
        public DateTime StartedUtc = DateTime.UtcNow;

        private readonly Config _cfg;
        private long _eventSeq;

        public ServerState(Config cfg)
        {
            _cfg = cfg;
        }

        public long EventSeq { get { lock (Sync) { return _eventSeq; } } }

        /// <summary>Nickname of the player behind a MAC, or "" when nobody matches.</summary>
        public string NicknameForMac(byte[] mac)
        {
            if (mac == null) return "";
            string text = Util.Mac(mac);
            lock (Sync)
            {
                foreach (KeyValuePair<string, ClientSession> kv in Sessions)
                {
                    ClientSession s = kv.Value;
                    if (s.Mac == null) continue;
                    if (!string.Equals(s.MacText, text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (s.Nickname.Length > 0) return s.Nickname;
                }
            }
            return "";
        }

        public void SessionOpened(ClientSession s)
        {
            lock (Sync)
            {
                Sessions[s.Id] = s;
                OnlineSessions = Sessions.Count;
                TotalConnections++;
                if (OnlineSessions > PeakOnline) PeakOnline = OnlineSessions;
            }
        }

        public void SessionClosed(ClientSession s)
        {
            lock (Sync) { Sessions.Remove(s.Id); OnlineSessions = Sessions.Count; }
        }

        public void AddEvent(string type, string text, string gameId, string gameName, string player)
        {
            ServerEvent e = new ServerEvent
            {
                Utc = DateTime.UtcNow,
                Type = type ?? "",
                Text = text ?? "",
                GameId = gameId ?? "",
                GameName = gameName ?? "",
                Player = player ?? ""
            };
            lock (Sync)
            {
                e.Seq = ++_eventSeq;
                Events.Add(e);
                if (Events.Count > _cfg.EventSize) Events.RemoveRange(0, Events.Count - _cfg.EventSize);
            }
        }

        public void AddHistory(HistoryEntry h, int max)
        {
            if (h == null) return;
            lock (Sync)
            {
                History.Add(h);
                if (History.Count > max) History.RemoveRange(0, History.Count - max);
            }
        }

        /// <summary>Restores sessions from a previous run so the board is not empty after a restart.</summary>
        public void SeedHistory(List<HistoryEntry> previous)
        {
            if (previous == null || previous.Count == 0) return;
            lock (Sync)
            {
                for (int i = 0; i < previous.Count; i++)
                {
                    HistoryEntry h = previous[i];
                    if (h == null) continue;
                    History.Add(h);
                }
                if (History.Count > _cfg.HistorySize)
                    History.RemoveRange(0, History.Count - _cfg.HistorySize);
            }
        }

        /// <summary>Remove idle games so the board does not collect dust. Caller holds Sync.</summary>
        public int TrimEmptyGames(int ttlMinutes)
        {
            lock (Sync)
            {
                if (ttlMinutes <= 0) return 0;
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-ttlMinutes);
                List<string> drop = new List<string>();
                foreach (KeyValuePair<string, GameEntry> kv in Games)
                {
                    GameEntry g = kv.Value;
                    if (g.Sessions.Count == 0 && g.LastActivityUtc < cutoff) drop.Add(kv.Key);
                }
                for (int i = 0; i < drop.Count; i++)
                {
                    Games.Remove(drop[i]);
                    Log.Debug("STATE", "idle game removed: " + drop[i]);
                }
                return drop.Count;
            }
        }

        public GameEntry GetGame(string id)
        {
            GameEntry g;
            lock (Sync) { return Games.TryGetValue(id ?? "", out g) ? g : null; }
        }

        public List<ClientSession> AllSessionsSnapshot()
        {
            lock (Sync) { return new List<ClientSession>(Sessions.Values); }
        }
    }
}