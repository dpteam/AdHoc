using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc.Net
{
    /// <summary>
    /// SceNetAdhocctl (PRO Online / PPSSPP) control server.
    ///
    /// Wire format (see docs/PROTOCOL.md):
    ///   C2S LOGIN      : 144 bytes  [op 0x01][mac 6][nickname 128][product code 9]
    ///   C2S CONNECT    : 9 bytes    [op 0x02][group 8]
    ///   C2S DISCONNECT : 1 byte     [op 0x03]
    ///   C2S SCAN       : 1 byte     [op 0x04]
    ///   C2S CHAT       : 65 bytes   [op 0x07][message 64]
    ///   C2S PING       : 1 byte     [op 0x00]
    /// S2C answers mirror the reference implementation (CONNECT_BSSID, CONNECT, DISCONNECT,
    /// SCAN, SCAN_COMPLETE, CHAT). There is intentionally no login ACK.
    /// </summary>
    public sealed class AdhocServer
    {
        private sealed class Outbound
        {
            public ClientSession Target;
            public byte[] Data;
            public Outbound(ClientSession target, byte[] data) { Target = target; Data = data; }
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly ServerState _state;
        private readonly GameDatabase _db;

        private readonly object _banLock = new object();
        private readonly Dictionary<string, DateTime> _bans = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _failCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hintLock = new object();
        private readonly Dictionary<string, DateTime> _mixedHints = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private Thread _acceptThread;
        private Thread _maintenanceThread;
        private volatile bool _running;
        private int _port;
        private long _acceptedTotal;

        /// <summary>Raised when the accept loop dies for an unexpected reason (supervisor hook).</summary>
        public event EventHandler Failed;

        public AdhocServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
            _state = app.State;
            _db = app.Db;
        }

        public int Port { get { return _port; } }
        public long AcceptedTotal { get { return Interlocked.Read(ref _acceptedTotal); } }

        public bool IsRunning
        {
            get { return _running && _acceptThread != null && _acceptThread.IsAlive; }
        }

        public int BanCount { get { lock (_banLock) { return _bans.Count; } } }

        // ------------------------------------------------------------ lifecycle

        public bool Start()
        {
            if (_running) return true;

            List<int> ports = new List<int>();
            ports.Add(_cfg.AdhocPort);
            if (_cfg.AdhocPortFallbacks != null)
            {
                for (int i = 0; i < _cfg.AdhocPortFallbacks.Length; i++)
                {
                    int p = _cfg.AdhocPortFallbacks[i];
                    if (p > 0 && p <= 65535 && !ports.Contains(p)) ports.Add(p);
                }
            }

            Exception last = null;
            for (int i = 0; i < ports.Count; i++)
            {
                int port = ports[i];
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    _listener = listener;
                    _port = port;
                    listener.Start(_cfg.ListenBacklog);

                    _running = true;

                    _acceptThread = new Thread(AcceptLoop);
                    _acceptThread.IsBackground = true;
                    _acceptThread.Name = "adhoc-accept";
                    _acceptThread.Start();

                    _maintenanceThread = new Thread(MaintenanceLoop);
                    _maintenanceThread.IsBackground = true;
                    _maintenanceThread.Name = "adhoc-maintenance";
                    _maintenanceThread.Start();

                    Log.Info("NET", "adhoc server listening on 0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture) +
                                    " (backlog " + _cfg.ListenBacklog.ToString(CultureInfo.InvariantCulture) + ")");
                    if (port != _cfg.AdhocPort)
                        Log.Warn("NET", "primary port " + _cfg.AdhocPort + " is busy, running on fallback " + port);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Warn("NET", "cannot bind adhoc port " + port.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                }
            }

            Log.Error("NET", "adhoc listener failed on every port. Last error: " + (last == null ? "unknown" : last.Message));
            return false;
        }

        public bool Restart()
        {
            Log.Warn("NET", "restarting adhoc listener");
            try { Stop(); } catch { }
            Thread.Sleep(300);
            return Start();
        }

        public void Stop()
        {
            bool wasRunning = _running;
            _running = false;

            if (wasRunning)
            {
                Log.Info("NET", "stopping adhoc server, notifying players");
                try { BroadcastChat(_cfg.ShutdownMessage, "SERVER"); } catch { }
                Thread.Sleep(60); // let the notice go out
            }

            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;

            List<ClientSession> sessions = _state.AllSessionsSnapshot();
            for (int i = 0; i < sessions.Count; i++)
            {
                try { sessions[i].RequestClose("shutdown"); } catch { }
            }

            JoinThread(_acceptThread, 3000);
            JoinThread(_maintenanceThread, 2000);
            _acceptThread = null;
            _maintenanceThread = null;

            if (wasRunning) Log.Info("NET", "adhoc server stopped");
        }

        private static void JoinThread(Thread t, int ms)
        {
            try { if (t != null && t.IsAlive) t.Join(ms); } catch { }
        }

        // -------------------------------------------------------------- accept

        private void AcceptLoop()
        {
            int consecutiveFailures = 0;
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    consecutiveFailures = 0;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (SocketException sex)
                {
                    if (!_running) break;
                    consecutiveFailures++;
                    Log.Warn("NET", "accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (consecutiveFailures >= 25)
                    {
                        Log.Error("NET", "accept loop looks dead, asking supervisor for a restart");
                        RaiseFailed();
                        break;
                    }
                    Thread.Sleep(Math.Min(2000, 50 * consecutiveFailures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    consecutiveFailures++;
                    Log.Error("NET", "unexpected accept error: " + ex.Message);
                    if (consecutiveFailures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }

                if (client == null) continue;

                try
                {
                    OnAccepted(client);
                }
                catch (Exception ex)
                {
                    Log.Error("NET", "cannot register client: " + ex.Message);
                    SafeClose(client);
                }
            }

            Log.Debug("NET", "accept loop finished");
        }

        private void RaiseFailed()
        {
            try
            {
                EventHandler h = Failed;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch { }
        }

        private void OnAccepted(TcpClient client)
        {
            IPEndPoint ep = null;
            try { ep = client.Client.RemoteEndPoint as IPEndPoint; } catch { }
            string ip = ep == null ? "0.0.0.0" : Util.Ip(ep.Address);

            if (IsBanned(ip))
            {
                Log.Warn("NET", "rejected banned ip " + ip);
                SafeClose(client);
                return;
            }

            int perIp;
            int online;
            lock (_state.Sync)
            {
                online = _state.Sessions.Count;
                perIp = CountIpLocked(ip);
            }

            if (online >= _cfg.MaxUsers)
            {
                Log.Warn("NET", "rejected " + ip + ": server full (" + online + "/" + _cfg.MaxUsers + ")");
                SafeClose(client);
                return;
            }

            if (perIp >= _cfg.MaxUsersPerIp)
            {
                Log.Warn("NET", "rejected " + ip + ": too many sessions from this ip (" + perIp + ")");
                SafeClose(client);
                return;
            }

            ClientSession session;
            try
            {
                session = new ClientSession(client, _cfg);
            }
            catch (Exception ex)
            {
                Log.Warn("NET", "cannot create session for " + ip + ": " + ex.Message);
                SafeClose(client);
                return;
            }

            _state.SessionOpened(session);
            _app.Stats.NoteConnection();
            Interlocked.Increment(ref _acceptedTotal);
            Log.Info("NET", "connection #" + Interlocked.Read(ref _acceptedTotal).ToString(CultureInfo.InvariantCulture) +
                            " from " + ip + ":" + session.RemotePort + " id=" + session.Id +
                            " (online " + online + "/" + _cfg.MaxUsers + ")");

            Thread t = new Thread(SessionLoop);
            t.IsBackground = true;
            t.Name = "adhoc-" + session.Id;
            try { t.Start(session); }
            catch (Exception ex)
            {
                Log.Error("NET", "cannot start session thread: " + ex.Message);
                CloseSession(session, "thread-start-failed", false);
            }
        }

        private int CountIpLocked(string ip)
        {
            int n = 0;
            foreach (KeyValuePair<string, ClientSession> kv in _state.Sessions)
            {
                if (string.Equals(kv.Value.RemoteIp, ip, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        private static void SafeClose(TcpClient client)
        {
            try { if (client != null) client.Close(); } catch { }
            try { if (client != null) client.Client.Close(); } catch { }
        }

        // ------------------------------------------------------------- session

        private void SessionLoop(object state)
        {
            ClientSession s = (ClientSession)state;
            string reason = "closed";
            try
            {
                while (_running && s.IsOpen)
                {
                    if (_cfg.SessionMaxHours > 0 && s.ConnectedFor.TotalHours >= _cfg.SessionMaxHours)
                    {
                        reason = "session-lifetime";
                        Log.Info("PROTO", s.RemoteIp + " session lifetime limit reached");
                        break;
                    }

                    if (s.IdleFor.TotalSeconds >= _cfg.UserTimeoutSec)
                    {
                        reason = "timeout";
                        Log.Info("PROTO", s.RemoteIp + " idle for " +
                                 ((int)s.IdleFor.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s, dropping");
                        break;
                    }

                    if (s.RxLen >= s.Rx.Length)
                    {
                        if (s.Rx.Length >= _cfg.MaxPacketBuffer)
                        {
                            reason = "rx-overflow";
                            Log.Warn("PROTO", s.RemoteIp + " rx buffer overflow (" + s.Rx.Length + "B)");
                            break;
                        }
                        s.EnsureRxCapacity(s.Rx.Length * 2);
                    }

                    int read;
                    try
                    {
                        read = s.Stream.Read(s.Rx, s.RxLen, s.Rx.Length - s.RxLen);
                    }
                    catch (IOException ioex)
                    {
                        if (IsTimeout(ioex)) continue;
                        reason = "io-error";
                        Log.Debug("PROTO", s.RemoteIp + " read failed: " + ioex.Message);
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        reason = "disposed";
                        break;
                    }
                    catch (SocketException sex)
                    {
                        if (sex.SocketErrorCode == SocketError.TimedOut || sex.SocketErrorCode == SocketError.WouldBlock) continue;
                        reason = "socket-" + sex.SocketErrorCode;
                        Log.Debug("PROTO", s.RemoteIp + " socket error: " + sex.SocketErrorCode);
                        break;
                    }

                    if (read <= 0)
                    {
                        reason = "peer-closed";
                        break;
                    }

                    s.RxLen += read;
                    s.AddReceived(read);
                    s.TouchRecv();

                    // keepalive pings are frequent (every 2s per client) - do not flood the log
                    bool isPing = read == 1 && s.Rx[s.RxLen - 1] == Op.Ping;
                    if (!isPing)
                        Log.Proto("PROTO", "<- {0} [{1}] {2}B {3}", s.RemoteIp, s.Id, read,
                            Util.HexPreview(s.Rx, Math.Max(0, s.RxLen - read), read, 48));

                    if (!ProcessBuffer(s))
                    {
                        reason = "protocol-error";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "exception";
                Log.Error("PROTO", "session " + s.Id + " crashed: " + ex);
            }
            finally
            {
                CloseSession(s, reason, true);
            }
        }

        private static bool IsTimeout(Exception ex)
        {
            SocketException se = ex as SocketException;
            if (se != null)
                return se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock;
            IOException io = ex as IOException;
            if (io != null)
            {
                se = io.InnerException as SocketException;
                if (se != null)
                    return se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.WouldBlock;
            }
            return false;
        }

        /// <summary>Consume as many complete packets as possible. Returns false to drop the session.</summary>
        private bool ProcessBuffer(ClientSession s)
        {
            int guard = 0;
            while (s.RxLen > 0)
            {
                if (++guard > 512)
                {
                    Log.Warn("PROTO", s.RemoteIp + " packet loop guard hit (buffer " + s.RxLen + "B)");
                    return false;
                }

                byte op = s.Rx[0];

                if (s.State == SessionState.Waiting)
                {
                    if (op != Op.Login)
                    {
                        Log.Warn("PROTO", "unexpected opcode 0x" + op.ToString("X2", CultureInfo.InvariantCulture) +
                                          " in WAITING state from " + s.RemoteIp + " buffer=" +
                                          Util.HexPreview(s.Rx, 0, s.RxLen, 32));
                        _state.ProtocolErrors++;
                        RegisterFailure(s.RemoteIp);
                        return false;
                    }

                    if (s.RxLen < Packets.LoginSize) return true;

                    byte[] loginPacket = new byte[Packets.LoginSize];
                    Buffer.BlockCopy(s.Rx, 0, loginPacket, 0, Packets.LoginSize);
                    s.Consume(Packets.LoginSize);
                    if (!HandleLogin(s, loginPacket)) return false;
                    continue;
                }

                switch (op)
                {
                    case Op.Ping:
                        s.Consume(1);
                        s.CountPing();
                        Log.Verbose("PROTO", "ping from " + s.ToString() + " (" + s.Pings + ")");
                        break;

                    case Op.Disconnect:
                        s.Consume(1);
                        HandleLeaveGroup(s, "client-request");
                        break;

                    case Op.Scan:
                        s.Consume(1);
                        HandleScan(s);
                        break;

                    case Op.Connect:
                        if (s.RxLen < Packets.ConnectRequestSize) return true;
                        {
                            byte[] group = new byte[Packets.GroupNameSize];
                            Buffer.BlockCopy(s.Rx, 1, group, 0, Packets.GroupNameSize);
                            s.Consume(Packets.ConnectRequestSize);
                            if (!HandleConnect(s, group)) return false;
                        }
                        break;

                    case Op.Chat:
                        if (s.RxLen < Packets.ChatRequestSize) return true;
                        {
                            string message = Util.FixedUtf8ToString(s.Rx, 1, Packets.MessageSize);
                            s.Consume(Packets.ChatRequestSize);
                            HandleChat(s, message);
                        }
                        break;

                    default:
                        Log.Warn("PROTO", "unknown opcode 0x" + op.ToString("X2", CultureInfo.InvariantCulture) +
                                          " from " + s.ToString() + " buffer=" + Util.HexPreview(s.Rx, 0, s.RxLen, 48));
                        _state.ProtocolErrors++;
                        if (!_cfg.IgnoreUnknownOpcodes)
                        {
                            RegisterFailure(s.RemoteIp);
                            return false;
                        }
                        // tolerant mode: skip one byte and keep the session alive
                        s.Consume(1);
                        break;
                }
            }
            return true;
        }

        // ---------------------------------------------------------- handlers

        private bool HandleLogin(ClientSession s, byte[] packet)
        {
            byte[] mac = new byte[Packets.MacSize];
            Buffer.BlockCopy(packet, 1, mac, 0, Packets.MacSize);
            string nickname = Util.FixedUtf8ToString(packet, 1 + Packets.MacSize, Packets.NicknameSize);
            string rawCode = Util.FixedUtf8ToString(packet, 1 + Packets.MacSize + Packets.NicknameSize, Packets.ProductCodeSize);

            string problem = null;
            if (Util.IsAllZero(mac) || Util.IsAllOnes(mac)) problem = "invalid-mac";
            else if (nickname.Length == 0) problem = "empty-nickname";
            else if (_cfg.RequireValidProductCode && !Util.IsAlnumAscii(rawCode)) problem = "invalid-product-code";

            if (problem != null)
            {
                Log.Warn("PROTO", "login rejected from " + s.RemoteIp + ": " + problem +
                                  " packet=" + Util.HexPreview(packet, 0, packet.Length, 64));
                _state.ProtocolErrors++;
                RegisterFailure(s.RemoteIp);
                _state.AddEvent("error", "rejected login from " + s.RemoteIp + " (" + problem + ")", "", "", nickname);
                return false;
            }

            s.Mac = mac;
            s.MacText = Util.Mac(mac);
            s.Nickname = Util.Clean(nickname, 64);
            s.Nickname128 = new byte[Packets.NicknameSize];
            Buffer.BlockCopy(packet, 1 + Packets.MacSize, s.Nickname128, 0, Packets.NicknameSize);
            s.RawProductCode = rawCode;

            _db.MaybeReload();

            string resolved = _db.Resolve(rawCode);
            s.ProductCode = resolved;
            s.GameName = _db.DisplayName(resolved);
            s.Hairpin = LocalNetwork.IsGateway(s.RemoteIp);

            if (GameDatabase.IsSynthetic(rawCode))
            {
                // Not an error: the game switched to Game Share mode, which has its own id.
                // Worth saying out loud, because the player then wonders why nobody joins.
                Log.Warn("PROTO", s.Nickname + " is in Game Share mode (product code " + rawCode +
                                  ", shown as \"" + s.GameName + "\"). Everyone must pick the same mode: " +
                                  "two players in Game Share see each other, but a Game Share host and a " +
                                  "player running the full game never meet.");
                _state.AddEvent("hint", s.Nickname + " is in Game Share mode - all players must use the same mode",
                                resolved, s.GameName, s.Nickname);
            }
            else if (!_db.HasTitle(rawCode))
            {
                // Unknown code: often a demo, a homebrew build or a modified rip.
                Log.Info("PROTO", s.Nickname + " uses product code " + rawCode +
                                  " which is not in the game database (demo, homebrew or a modified rip?)");
            }

            if (s.Hairpin)
            {
                Log.Warn("PROTO", "NAT hairpin suspected for " + s.Nickname + ": source " + s.RemoteIp +
                                  " is this machine's gateway, peers cannot reach it directly. " +
                                  "Enable 'Try to use server-provided packet relay' in PPSSPP.");
            }
            s.State = SessionState.LoggedIn;
            s.LastPacketText = "LOGIN " + rawCode;

            bool newGame = false;
            string duplicate = null;

            lock (_state.Sync)
            {
                // Duplicate MAC: games identify players by MAC, so flag it loudly.
                ClientSession dup = FindDuplicateMacLocked(s);
                if (dup != null)
                {
                    _state.DuplicateMacs++;
                    duplicate = dup.Nickname + "@" + dup.RemoteIp;
                    Log.Warn("PROTO", "duplicate MAC " + s.MacText + ": new=" + s.Nickname + "@" + s.RemoteIp +
                                      " existing=" + duplicate);
                    if (_cfg.KickDuplicateMac)
                    {
                        dup.RequestClose("duplicate-mac");
                        _state.Kicks++;
                    }
                }

                GameEntry game;
                if (!_state.Games.TryGetValue(resolved, out game))
                {
                    game = new GameEntry();
                    game.Id = resolved;
                    game.RawId = rawCode;
                    game.Name = _db.DisplayName(resolved);
                    game.CreatedUtc = DateTime.UtcNow;
                    // carry lifetime numbers over so the board does not forget previous runs
                    game.PeakPlayers = _app.Stats.PeakOf(resolved);
                    game.TotalPlayers = (int)Math.Min(int.MaxValue, _app.Stats.JoinsOf(resolved));
                    _state.Games[resolved] = game;
                    newGame = true;
                }

                // remember every regional copy that showed up (EU and US players in one lobby)
                if (rawCode.Length > 0 && !game.RawIds.Contains(rawCode)) game.RawIds.Add(rawCode);
                game.LastActivityUtc = DateTime.UtcNow;
                game.Sessions.Add(s);
                game.TotalPlayers++;
                if (game.Sessions.Count > game.PeakPlayers) game.PeakPlayers = game.Sessions.Count;
                s.Game = game;
                _state.TotalLogins++;
                _app.Stats.NoteLogin(resolved, game.Name);
                _app.Stats.NoteGamePeak(resolved, game.Sessions.Count);
                _app.Stats.NoteOnline(_state.Sessions.Count);
            }

            Log.Info("PROTO", "LOGIN " + s.Nickname + " mac=" + s.MacText + " ip=" + s.RemoteIp +
                              " game=" + rawCode + (resolved != rawCode ? "->" + resolved : "") +
                              " (" + s.GameName + ")");
            _state.AddEvent("join", s.Nickname + " started playing " + s.GameName, resolved, s.GameName, s.Nickname);
            if (s.Hairpin)
            {
                _state.AddEvent("hint",
                    s.Nickname + " looks like NAT hairpin (source " + s.RemoteIp +
                    ") - enable packet relay in PPSSPP settings", resolved, s.GameName, s.Nickname);
            }
            if (newGame)
                _state.AddEvent("game.new", "New game on the board: " + s.GameName, resolved, s.GameName, s.Nickname);
            if (duplicate != null)
                _state.AddEvent("error", "Duplicate MAC " + s.MacText + " (" + s.Nickname + " / " + duplicate + ")",
                                resolved, s.GameName, s.Nickname);

            return true;
        }

        private ClientSession FindDuplicateMacLocked(ClientSession s)
        {
            foreach (KeyValuePair<string, ClientSession> kv in _state.Sessions)
            {
                ClientSession other = kv.Value;
                if (other == s || other.State != SessionState.LoggedIn) continue;
                if (other.Mac == null || s.Mac == null) continue;
                if (other.Mac[0] != s.Mac[0] || other.Mac[1] != s.Mac[1] || other.Mac[2] != s.Mac[2] ||
                    other.Mac[3] != s.Mac[3] || other.Mac[4] != s.Mac[4] || other.Mac[5] != s.Mac[5]) continue;
                return other;
            }
            return null;
        }

        private bool HandleConnect(ClientSession s, byte[] groupBytes)
        {
            string groupName = Util.Clean(Util.FixedUtf8ToString(groupBytes, 0, Packets.GroupNameSize), Packets.GroupNameSize);

            // An empty name is legal (PPSSPP: sceNetAdhocctlCreate(NULL) -> 8 zero bytes,
            // used by Phantasy Star Portable 2). Only reject real garbage.
            if (!Util.IsValidGroupName(groupName))
            {
                Log.Warn("PROTO", s.ToString() + " requested invalid group name \"" + groupName + "\" hex=" +
                                  Util.Hex(groupBytes));
                _state.ProtocolErrors++;
                if (!_cfg.IgnoreUnknownOpcodes)
                {
                    RegisterFailure(s.RemoteIp);
                    return false;
                }
                return true;
            }

            string groupLabel = Util.GroupDisplayName(groupName);
            List<Outbound> deliveries = new List<Outbound>();
            byte[] bssidPacket = null;
            string leftOld = null;

            lock (_state.Sync)
            {
                if (s.Group != null)
                {
                    leftOld = s.Group.Name;
                    Log.Info("PROTO", s.ToString() + " joins " + groupLabel + " while still in " +
                                      Util.GroupDisplayName(leftOld) + " (auto leaving previous group)");
                    LeaveGroupLocked(s, deliveries);
                }

                GameEntry game = s.Game;
                if (game == null)
                {
                    Log.Warn("PROTO", s.ToString() + " CONNECT without login, ignored");
                    return true;
                }

                GroupEntry group = game.FindGroup(groupName);
                if (group == null)
                {
                    group = new GroupEntry();
                    group.Name = groupName;
                    group.Game = game;
                    group.CreatedUtc = DateTime.UtcNow;
                    game.Groups.Add(group);
                    Log.Info("PROTO", "group \"" + groupLabel + "\" created in " + game.Id +
                                      (groupName.Length == 0 ? " (client sent an empty name - normal for some games)" : ""));
                }

                byte[] founderMac = group.Founder != null ? group.Founder.Mac : s.Mac;
                bssidPacket = Packets.ConnectBssid(founderMac);

                List<ClientSession> peers = new List<ClientSession>(group.Sessions);
                for (int i = 0; i < peers.Count; i++)
                {
                    ClientSession peer = peers[i];
                    // tell the peer about the newcomer
                    deliveries.Add(new Outbound(peer,
                        Packets.ConnectBroadcast(s.Nickname128, s.Mac, s.RemoteIpBytes)));
                    // and tell the newcomer about the peer
                    deliveries.Add(new Outbound(s,
                        Packets.ConnectBroadcast(peer.Nickname128, peer.Mac, peer.RemoteIpBytes)));
                }

                group.Sessions.Add(s);
                group.EverJoined++;
                if (group.Founder == null) group.Founder = s;
                s.Group = group;
                game.LastActivityUtc = DateTime.UtcNow;
            }

            SendAll(deliveries);
            if (bssidPacket != null) s.Send(bssidPacket);

            Log.Info("PROTO", s.ToString() + " joined " + s.GameId + "/" + groupLabel);
            _state.AddEvent("group.join",
                s.Nickname + " joined group " + groupLabel +
                (leftOld != null ? " (left " + Util.GroupDisplayName(leftOld) + ")" : ""),
                s.GameId, s.GameName, s.Nickname);
            return true;
        }

        private void HandleLeaveGroup(ClientSession s, string cause)
        {
            List<Outbound> deliveries = new List<Outbound>();
            string groupName = null;
            lock (_state.Sync)
            {
                if (s.Group == null)
                {
                    Log.Debug("PROTO", s.ToString() + " DISCONNECT without a group (" + cause + ")");
                    return;
                }
                groupName = s.Group.Name;
                LeaveGroupLocked(s, deliveries);
            }

            SendAll(deliveries);
            string label = Util.GroupDisplayName(groupName);
            Log.Info("PROTO", s.ToString() + " left group " + label + " (" + cause + ")");
            _state.AddEvent("group.leave", s.Nickname + " left group " + label, s.GameId, s.GameName, s.Nickname);
        }

        /// <summary>Caller holds _state.Sync.</summary>
        private void LeaveGroupLocked(ClientSession s, List<Outbound> deliveries)
        {
            GroupEntry group = s.Group;
            if (group == null) return;

            group.Sessions.Remove(s);
            if (group.Founder == s) group.Founder = group.Sessions.Count > 0 ? group.Sessions[0] : null;
            s.Group = null;

            byte[] notice = Packets.DisconnectBroadcast(s.RemoteIpBytes);
            for (int i = 0; i < group.Sessions.Count; i++)
                deliveries.Add(new Outbound(group.Sessions[i], notice));

            GameEntry game = group.Game;
            if (group.Sessions.Count == 0)
            {
                game.Groups.Remove(group);
                Log.Debug("PROTO", "group \"" + Util.GroupDisplayName(group.Name) + "\" removed (empty)");

                // A player who creates a room, waits a moment and leaves was alone: the game
                // searched for peers, found none and gave up. Say so, because the log otherwise
                // looks like a clean join/leave and the real problem is invisible.
                double lived = (DateTime.UtcNow - group.CreatedUtc).TotalSeconds;
                if (group.EverJoined <= 1 && lived < 20)
                {
                    string label = Util.GroupDisplayName(group.Name);
                    string text = s.Nickname + " was alone in " + label + " (" + game.Id + ") for " +
                                  Math.Round(lived) + "s and left - the game found no peers. " +
                                  "Another player must be in the same game and the same mode.";
                    Log.Warn("PROTO", text);
                    _state.AddEvent("hint", text, game.Id, game.Name, s.Nickname);
                }
            }
            game.LastActivityUtc = DateTime.UtcNow;
        }

        private void HandleScan(ClientSession s)
        {
            List<byte[]> packets = new List<byte[]>();
            int groups = 0;
            lock (_state.Sync)
            {
                GameEntry game = s.Game;
                if (game != null && s.Group == null)
                {
                    for (int i = 0; i < game.Groups.Count; i++)
                    {
                        GroupEntry g = game.Groups[i];
                        byte[] host = g.Founder != null ? g.Founder.Mac : new byte[6];
                        packets.Add(Packets.ScanResult(g.Name, host));
                        groups++;
                    }
                }
                else if (s.Group != null)
                {
                    // Upstream kills the session here; we stay friendly and return an empty scan.
                    Log.Warn("PROTO", s.ToString() + " scanned while already in group " + Util.GroupDisplayName(s.Group.Name));
                }
            }

            for (int i = 0; i < packets.Count; i++) s.Send(packets[i]);
            s.Send(Packets.ScanComplete());

            Log.Proto("PROTO", "scan from " + s.ToString() + ": " + groups + " group(s) reported");
        }

        private void HandleChat(ClientSession s, string message)
        {
            string text = Util.Clean(message, Packets.MessageSize);
            if (text.Length == 0) return;

            List<ClientSession> targets = new List<ClientSession>();
            string groupName = "";
            lock (_state.Sync)
            {
                if (s.Group == null)
                {
                    Log.Warn("PROTO", s.ToString() + " chatted without a group, ignored");
                    return;
                }
                groupName = s.Group.Name;
                for (int i = 0; i < s.Group.Sessions.Count; i++)
                {
                    ClientSession peer = s.Group.Sessions[i];
                    if (peer != s) targets.Add(peer);
                }
            }

            byte[] packet = Packets.ChatBroadcast(text, s.Nickname128);
            for (int i = 0; i < targets.Count; i++) targets[i].Send(packet);

            _state.TotalChats++;
            _app.Stats.NoteChat();
            if (_app.Chat != null) _app.Chat.AppendFromGame(s.Nickname, text);
            Log.Info("PROTO", "chat " + s.Nickname + " -> " + Util.GroupDisplayName(groupName) + " (" + targets.Count + "): " + text);
            _state.AddEvent("chat", s.Nickname + ": " + text, s.GameId, s.GameName, s.Nickname);
        }

        // -------------------------------------------------------------- close

        private void CloseSession(ClientSession s, string reason, bool notifyPeers)
        {
            if (s == null) return;
            if (Interlocked.CompareExchange(ref s.CloseHandled, 1, 0) != 0) return;

            s.RequestClose(reason);

            List<Outbound> deliveries = new List<Outbound>();
            HistoryEntry history = null;

            lock (_state.Sync)
            {
                if (notifyPeers && s.Group != null) LeaveGroupLocked(s, deliveries);

                if (s.Game != null)
                {
                    s.Game.Sessions.Remove(s);
                    s.Game.LastActivityUtc = DateTime.UtcNow;
                }

                if (s.State == SessionState.LoggedIn)
                {
                    history = new HistoryEntry();
                    history.Name = s.Nickname;
                    history.GameId = s.GameId;
                    history.GameName = s.GameName;
                    history.Mac = s.MacText;
                    history.Ip = s.RemoteIp;
                    history.Group = s.GroupName;
                    history.ConnectUtc = s.ConnectUtc;
                    history.DisconnectUtc = DateTime.UtcNow;
                    history.DurationSec = Math.Round(s.ConnectedFor.TotalSeconds, 1);
                    history.Reason = reason;
                    history.BytesIn = s.BytesIn;
                    history.BytesOut = s.BytesOut;
                }

                s.State = SessionState.Closed;
            }

            SendAll(deliveries);
            _state.SessionClosed(s);

            if (string.Equals(reason, "timeout", StringComparison.OrdinalIgnoreCase)) _state.TimedOutSessions++;

            if (history != null)
            {
                _state.AddHistory(history, _cfg.HistorySize);
                _app.Stats.NoteSessionEnd(history);
                _app.Stats.NoteTraffic(history.BytesIn, history.BytesOut);
                _app.Stats.NoteOnline(_state.Sessions.Count);
                _state.AddEvent("leave", s.Nickname + " left " + history.GameName + " (" + reason + ")",
                                history.GameId, history.GameName, s.Nickname);
            }

            Log.Info("NET", "session closed id=" + s.Id + " ip=" + s.RemoteIp +
                            (s.Nickname.Length > 0 ? " nick=" + s.Nickname : "") +
                            " reason=" + reason + " duration=" + Util.HumanDuration(s.ConnectedFor) +
                            " in=" + Util.HumanSize(s.BytesIn) + " out=" + Util.HumanSize(s.BytesOut));
        }

        private void SendAll(List<Outbound> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                try { list[i].Target.Send(list[i].Data); }
                catch (Exception ex) { Log.Debug("PROTO", "delivery failed: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------- bans

        private bool IsBanned(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            lock (_banLock)
            {
                DateTime until;
                if (!_bans.TryGetValue(ip, out until)) return false;
                if (until <= DateTime.UtcNow) { _bans.Remove(ip); return false; }
                return true;
            }
        }

        private void RegisterFailure(string ip)
        {
            if (string.IsNullOrEmpty(ip) || _cfg.BanSeconds <= 0) return;
            bool banned = false;
            lock (_banLock)
            {
                int n;
                _failCount.TryGetValue(ip, out n);
                n++;
                _failCount[ip] = n;
                if (n >= _cfg.MaxFailedLoginsPerIp)
                {
                    _bans[ip] = DateTime.UtcNow.AddSeconds(_cfg.BanSeconds);
                    _failCount.Remove(ip);
                    banned = true;
                }
            }
            if (banned)
            {
                lock (_state.Sync) { _state.Bans++; }
                Log.Warn("NET", "temporary ban for " + ip + " (" + _cfg.BanSeconds + "s) after repeated protocol errors");
                _state.AddEvent("ban", "Temporary ban: " + ip + " for " + _cfg.BanSeconds + "s", "", "", "");
            }
        }

        public int ClearBans()
        {
            int n;
            lock (_banLock)
            {
                n = _bans.Count + _failCount.Count;
                _bans.Clear();
                _failCount.Clear();
            }
            if (n > 0) Log.Info("NET", "ban list cleared (" + n + " entries)");
            return n;
        }

        public List<string> BanSnapshot()
        {
            List<string> list = new List<string>();
            lock (_banLock)
            {
                DateTime now = DateTime.UtcNow;
                foreach (KeyValuePair<string, DateTime> kv in _bans)
                {
                    if (kv.Value <= now) continue;
                    list.Add(kv.Key + "|" + ((int)Math.Ceiling((kv.Value - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture));
                }
            }
            return list;
        }

        // --------------------------------------------------------- admin tools

        /// <summary>Send a chat message to every logged in player (used for shutdown notices and the web UI).</summary>
        public int BroadcastChat(string text, string sender)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            List<ClientSession> targets = new List<ClientSession>();
            lock (_state.Sync)
            {
                foreach (KeyValuePair<string, ClientSession> kv in _state.Sessions)
                    if (kv.Value.State == SessionState.LoggedIn) targets.Add(kv.Value);
            }

            byte[] packet = Packets.BuildChat(Util.Clean(text, Packets.MessageSize), sender);
            int sent = 0;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i].Send(packet)) sent++;

            if (sent > 0)
            {
                Log.Info("NET", "broadcast from " + sender + " to " + sent + " player(s): " + text);
                _state.AddEvent("chat", sender + ": " + text, "", "", sender);
            }
            return sent;
        }

        /// <summary>Kick by session id, MAC or nickname. Returns number of sessions kicked.</summary>
        public int Kick(string selector, out string detail)
        {
            detail = "";
            if (string.IsNullOrEmpty(selector))
            {
                detail = "empty selector";
                return 0;
            }

            string sel = selector.Trim();
            byte[] mac;
            bool byMac = Util.TryParseMac(sel, out mac) && mac != null;

            List<ClientSession> victims = new List<ClientSession>();
            lock (_state.Sync)
            {
                foreach (KeyValuePair<string, ClientSession> kv in _state.Sessions)
                {
                    ClientSession s = kv.Value;
                    bool match = string.Equals(s.Id, sel, StringComparison.OrdinalIgnoreCase) ||
                                 (byMac && string.Equals(s.MacText, Util.Mac(mac), StringComparison.OrdinalIgnoreCase)) ||
                                 string.Equals(s.Nickname, sel, StringComparison.OrdinalIgnoreCase);
                    if (match) victims.Add(s);
                }
            }

            for (int i = 0; i < victims.Count; i++)
            {
                ClientSession s = victims[i];
                detail = s.Nickname + " / " + s.MacText + " / " + s.RemoteIp;
                Log.Warn("NET", "kick requested: " + detail);
                s.RequestClose("kicked");
                lock (_state.Sync) { _state.Kicks++; }
                _state.AddEvent("kick", "Kicked " + (s.Nickname.Length > 0 ? s.Nickname : s.RemoteIp), s.GameId, s.GameName, s.Nickname);
            }
            return victims.Count;
        }

        // -------------------------------------------------------- maintenance

        /// <summary>
        /// PPSSPP relay is a per-client choice: if one player tunnels and another sends UDP directly,
        /// they cannot reach each other. Detect that state and put an actionable hint on the board.
        /// </summary>
        private void CheckMixedTransports()
        {
            if (_app.Relay == null || !_app.Relay.IsRunning) return;

            List<GameEntry> games;
            lock (_state.Sync) { games = new List<GameEntry>(_state.Games.Values); }

            for (int gi = 0; gi < games.Count; gi++)
            {
                GameEntry game = games[gi];
                List<GroupEntry> groups;
                lock (_state.Sync) { groups = new List<GroupEntry>(game.Groups); }

                for (int g = 0; g < groups.Count; g++)
                {
                    GroupEntry group = groups[g];
                    List<ClientSession> members;
                    DateTime created;
                    lock (_state.Sync)
                    {
                        members = new List<ClientSession>(group.Sessions);
                        created = group.CreatedUtc;
                    }

                    if (members.Count < 2) continue;
                    if ((DateTime.UtcNow - created).TotalSeconds < 10) continue;

                    int relayCount = 0;
                    List<string> withoutRelay = new List<string>();
                    DateTime now = DateTime.UtcNow;
                    for (int m = 0; m < members.Count; m++)
                    {
                        ClientSession ms = members[m];
                        if (_app.Relay.CountForMac(ms.Mac) > 0) { relayCount++; continue; }
                        // games recreate their PDP sockets when the lobby is redone; a short gap
                        // must not look like "this player turned the relay off"
                        double idle = (now - _app.Relay.LastActivityForMac(ms.Mac)).TotalSeconds;
                        if (idle < 30) { relayCount++; continue; }
                        withoutRelay.Add(ms.Nickname);
                    }

                    if (relayCount == 0 || relayCount == members.Count) continue;

                    string key = game.Id + "|" + group.Name;
                    lock (_hintLock)
                    {
                        DateTime last;
                        if (_mixedHints.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalSeconds < 120) continue;
                        _mixedHints[key] = DateTime.UtcNow;
                        if (_mixedHints.Count > 200) _mixedHints.Clear();
                    }

                    string text = "Mixed transports in " + game.Name + " / " + Util.GroupDisplayName(group.Name) + ": " +
                                  relayCount + " of " + members.Count +
                                  " players use the relay" +
                                  (withoutRelay.Count > 0 ? " (no relay: " + string.Join(", ", withoutRelay.ToArray()) + ")" : "") +
                                  ". Turn on 'Try to use server-provided packet relay' " +
                                  "for every player (or for none) - a mixed group cannot see each other.";
                    Log.Warn("RELAY", text);
                    _state.AddEvent("hint", text, game.Id, game.Name, "");
                }
            }
        }

        private void MaintenanceLoop()
        {
            DateTime lastTrim = DateTime.UtcNow;
            DateTime lastFailPurge = DateTime.UtcNow;
            DateTime lastMixedCheck = DateTime.UtcNow;
            while (_running)
            {
                try { Thread.Sleep(2000); }
                catch { }
                if (!_running) break;

                try
                {
                    // lifetime stats: flush at most every 15 s, and only when something changed
                    if (_app.Stats != null) _app.Stats.SaveIfDue(15);
                    if (_app.Visits != null) _app.Visits.SaveIfDue(30);
                    if (_app.Chat != null) _app.Chat.SaveIfDue(10);

                    if ((DateTime.UtcNow - lastTrim).TotalSeconds >= 30)
                    {
                        lastTrim = DateTime.UtcNow;
                        int removed = _state.TrimEmptyGames(_cfg.EmptyGameTtlMin);
                        if (removed > 0) Log.Debug("NET", "idle games trimmed: " + removed);
                    }

                    if ((DateTime.UtcNow - lastMixedCheck).TotalSeconds >= 10)
                    {
                        lastMixedCheck = DateTime.UtcNow;
                        CheckMixedTransports();
                    }

                    if ((DateTime.UtcNow - lastFailPurge).TotalSeconds >= 300)
                    {
                        lastFailPurge = DateTime.UtcNow;
                        lock (_banLock)
                        {
                            List<string> gone = new List<string>();
                            DateTime now = DateTime.UtcNow;
                            foreach (KeyValuePair<string, DateTime> kv in _bans)
                                if (kv.Value <= now) gone.Add(kv.Key);
                            for (int i = 0; i < gone.Count; i++) _bans.Remove(gone[i]);
                            if (_failCount.Count > 0) _failCount.Clear();
                        }
                    }

                    // Detect sessions whose thread died without closing (should not happen, cheap safety net).
                    List<ClientSession> all = _state.AllSessionsSnapshot();
                    for (int i = 0; i < all.Count; i++)
                    {
                        ClientSession s = all[i];
                        if (s.IsClosing && s.State != SessionState.Closed)
                            CloseSession(s, "closing-sweep", true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("NET", "maintenance loop: " + ex.Message);
                }
            }
        }

        // --------------------------------------------------------------- stats

        public string StatusLine()
        {
            int online;
            int games;
            int groups;
            int history;
            int events;
            lock (_state.Sync)
            {
                online = _state.Sessions.Count;
                games = _state.Games.Count;
                groups = 0;
                foreach (KeyValuePair<string, GameEntry> kv in _state.Games) groups += kv.Value.Groups.Count;
                history = _state.History.Count;
                events = _state.Events.Count;
            }
            return "online " + online + "/" + _cfg.MaxUsers + " | games " + games + " | groups " + groups +
                   " | history " + history + " | events " + events + " | bans " + BanCount +
                   " | port " + _port;
        }
    }
}