using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Dpt.AdHoc.Core;
using Dpt.AdHoc.Net;

namespace Dpt.AdHoc
{
    /// <summary>
    /// Small interactive console: live status line + a handful of operator commands.
    /// Everything interesting goes to the log file, this is just the cockpit.
    /// </summary>
    public sealed class ConsoleUi
    {
        private readonly ServerApp _app;
        private readonly Action _requestShutdown;
        private Thread _thread;
        private volatile bool _stop;
        private bool _noKeys;

        public ConsoleUi(ServerApp app, Action requestShutdown)
        {
            _app = app;
            _requestShutdown = requestShutdown;
        }

        public void Start()
        {
            try { Console.Title = "DPT.PSP AdHoc - adhoc " + _app.Adhoc.Port + " / public " + _app.WebPublic.Port + " / admin " + _app.WebAdmin.Port; } catch { }
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "console-ui";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(1000); } catch { }
        }

        private static int SafeWidth()
        {
            try
            {
                int w = Console.WindowWidth;
                return w < 40 ? 80 : w;
            }
            catch { return 100; }
        }

        private void Loop()
        {
            Console.WriteLine("Type 'help' for commands (the web UI is the fancy part).");
            int width = SafeWidth();
            bool statusLine = !_app.Cfg.NoConsoleStatusLine;

            while (!_stop)
            {
                try
                {
                    if (!_noKeys)
                    {
                        bool keyAvailable;
                        try { keyAvailable = Console.KeyAvailable; }
                        catch (InvalidOperationException) { _noKeys = true; keyAvailable = true; }

                        if (!keyAvailable)
                        {
                            if (statusLine) DrawStatus(width);
                            Thread.Sleep(700);
                            continue;
                        }
                    }

                    if (!_noKeys)
                    {
                        Console.Write("\r" + new string(' ', Math.Max(0, width - 1)) + "\r");
                    }
                    Console.Write("adhoc> ");
                    string line = Console.ReadLine();
                    if (line == null) break;
                    Execute(line.Trim());
                }
                catch (Exception ex)
                {
                    Log.Debug("UI", "console loop: " + ex.Message);
                    try { Thread.Sleep(500); } catch { }
                }
            }
        }

        private void DrawStatus(int width)
        {
            int online, games, groups;
            lock (_app.State.Sync)
            {
                online = _app.State.Sessions.Count;
                games = _app.State.Games.Count;
                groups = 0;
                foreach (KeyValuePair<string, GameEntry> kv in _app.State.Games) groups += kv.Value.Groups.Count;
            }

            string line = " online " + online + "/" + _app.Cfg.MaxUsers +
                          " | games " + games + " | groups " + groups +
                          " | web " + _app.WebPublic.Port + "/" + _app.WebAdmin.Port + " | relay " + (_app.Relay == null ? 0 : _app.Relay.SessionCount) + " | up " + Util.HumanDuration(_app.Uptime) +
                          " | mem " + (_app.WorkingSetBytes / 1048576).ToString(CultureInfo.InvariantCulture) + "MB ";
            if (line.Length > width - 1) line = line.Substring(0, width - 1);

            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("\r" + line.PadRight(Math.Max(0, width - 1)));
                Console.ResetColor();
            }
            catch { }
        }

        private void Execute(string line)
        {
            if (line.Length == 0) return;
            string[] parts = line.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help":
                case "?":
                    PrintHelp();
                    break;

                case "status":
                    PrintStatus();
                    break;

                case "games":
                    PrintGames();
                    break;

                case "sessions":
                case "users":
                    PrintSessions();
                    break;

                case "kick":
                    if (arg.Length == 0) { Console.WriteLine("usage: kick <session-id|mac|nickname>"); break; }
                    {
                        string detail;
                        int n = _app.Adhoc.Kick(arg, out detail);
                        Console.WriteLine(n > 0 ? "kicked " + detail : "no session matched '" + arg + "'");
                    }
                    break;

                case "say":
                    if (arg.Length == 0) { Console.WriteLine("usage: say <text>"); break; }
                    {
                        int sent = _app.Adhoc.BroadcastChat(arg, "SERVER");
                        Console.WriteLine("message delivered to " + sent + " player(s)");
                    }
                    break;

                case "bans":
                    {
                        List<string> bans = _app.Adhoc.BanSnapshot();
                        Console.WriteLine("banned ips: " + bans.Count);
                        for (int i = 0; i < bans.Count; i++)
                        {
                            string[] p = bans[i].Split('|');
                            Console.WriteLine("  " + p[0] + " (" + (p.Length > 1 ? p[1] : "?") + "s left)");
                        }
                    }
                    break;

                case "clearbans":
                    Console.WriteLine("cleared " + _app.Adhoc.ClearBans() + " ban entries");
                    break;

                case "logs":
                    {
                        int n = 20;
                        if (arg.Length > 0) int.TryParse(arg, out n);
                        List<LogEntry> entries = Log.Tail(0, n <= 0 ? 20 : n, null, null);
                        for (int i = 0; i < entries.Count; i++)
                        {
                            LogEntry e = entries[i];
                            try
                            {
                                Console.ForegroundColor = ColorFor(e.Level);
                                Console.WriteLine("  " + Log.Format(e));
                                Console.ResetColor();
                            }
                            catch { Console.WriteLine("  " + Log.Format(e)); }
                        }
                    }
                    break;

                case "reload":
                    _app.Db.Reload();
                    Console.WriteLine("game database reloaded: " + _app.Db.Count + " titles");
                    break;

                case "quit":
                case "exit":
                    Console.WriteLine("stopping...");
                    if (_requestShutdown != null) _requestShutdown();
                    break;

                default:
                    Console.WriteLine("unknown command '" + cmd + "', try 'help'");
                    break;
            }
        }

        private static ConsoleColor ColorFor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return ConsoleColor.Red;
                case LogLevel.Warn: return ConsoleColor.Yellow;
                case LogLevel.Info: return ConsoleColor.Gray;
                case LogLevel.Debug: return ConsoleColor.DarkGray;
                default: return ConsoleColor.DarkGray;
            }
        }

        private void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("  help                  this text");
            Console.WriteLine("  status                server + counters");
            Console.WriteLine("  games                 games on the board");
            Console.WriteLine("  sessions              every connected player");
            Console.WriteLine("  kick <id|mac|nick>    disconnect a player");
            Console.WriteLine("  say <text>            chat message to everybody");
            Console.WriteLine("  bans / clearbans      temporary ip bans");
            Console.WriteLine("  logs [n]              tail of the in-memory log");
            Console.WriteLine("  reload                re-read data/games.json");
            Console.WriteLine("  quit                  stop the server");
            Console.WriteLine();
        }

        private void PrintStatus()
        {
            Console.WriteLine();
            Console.WriteLine("  " + _app.Name + " v" + _app.Version + "  (build " + _app.BuildStamp + ")");
            Console.WriteLine("  uptime         : " + Util.HumanDuration(_app.Uptime));
            Console.WriteLine("  adhoc port     : " + _app.Adhoc.Port + (File.Exists(_app.Cfg.ConfigPath) ? "  (config: " + _app.Cfg.ConfigPath + ")" : ""));
            Console.WriteLine("  public board   : " + (_app.WebPublic.IsRunning ? _app.WebPublic.BaseUrl : "not running"));
            Console.WriteLine("  admin board    : " + (_app.WebAdmin.IsRunning ? _app.WebAdmin.BaseUrl : "not running"));
            Console.WriteLine("  web root       : " + _app.WebPublic.Root);
            Console.WriteLine("  protocol       : " + _app.Adhoc.StatusLine());
            Console.WriteLine("  public         : " + _app.WebPublic.StatusLine());
            Console.WriteLine("  admin          : " + _app.WebAdmin.StatusLine());
            Console.WriteLine("  relay          : " + (_app.Relay != null ? _app.Relay.StatusLine() : "off"));
            Console.WriteLine("  admin token    : " + (string.IsNullOrEmpty(_app.Cfg.AdminToken) ? "MISSING (see config.ini)" : "set (config.ini: web.adminToken)"));
            Console.WriteLine("  game db        : " + _app.Db.Count + " titles / " + _app.Db.CrosslinkCount + " crosslinks");
            Console.WriteLine("  memory         : " + (_app.WorkingSetBytes / 1048576).ToString(CultureInfo.InvariantCulture) + " MB");
            Console.WriteLine();
        }

        private void PrintGames()
        {
            Console.WriteLine();
            lock (_app.State.Sync)
            {
                if (_app.State.Games.Count == 0)
                {
                    Console.WriteLine("  (nothing yet)");
                }
                else
                {
                    foreach (KeyValuePair<string, GameEntry> kv in _app.State.Games)
                    {
                        GameEntry g = kv.Value;
                        Console.WriteLine("  " + g.Id + "  " + _app.Db.DisplayName(g.Id) + "  players=" + g.Sessions.Count +
                                          " groups=" + g.Groups.Count);
                        for (int i = 0; i < g.Groups.Count; i++)
                        {
                            GroupEntry grp = g.Groups[i];
                            Console.Write("      [" + grp.Name + "]");
                            for (int s = 0; s < grp.Sessions.Count; s++)
                                Console.Write(" " + grp.Sessions[s].Nickname + (grp.Founder == grp.Sessions[s] ? "*" : ""));
                            Console.WriteLine();
                        }
                    }
                }
            }
            Console.WriteLine();
        }

        private void PrintSessions()
        {
            Console.WriteLine();
            List<ClientSession> all = _app.State.AllSessionsSnapshot();
            if (all.Count == 0) Console.WriteLine("  (no sessions)");
            for (int i = 0; i < all.Count; i++)
            {
                ClientSession s = all[i];
                Console.WriteLine("  " + s.Id + "  " + s.RemoteIp + ":" + s.RemotePort +
                                  "  " + (s.Nickname.Length > 0 ? s.Nickname : "-") +
                                  "  mac=" + s.MacText +
                                  "  game=" + (s.GameId.Length > 0 ? s.GameId : "-") +
                                  "  group=" + (s.GroupName.Length > 0 ? s.GroupName : "-") +
                                  "  idle=" + (int)s.IdleFor.TotalSeconds + "s" +
                                  "  up=" + Util.HumanDuration(s.ConnectedFor));
            }
            Console.WriteLine();
        }
    }
}