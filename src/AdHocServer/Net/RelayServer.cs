using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc.Net
{
    /// <summary>
    /// PPSSPP "aemu_postoffice" relay (TCP).
    ///
    /// Why it exists: when PPSSPP plays through your own public IP from inside the LAN (NAT
    /// hairpin), the router rewrites the source to its own address, so peers get an IP that
    /// cannot reach the player. In relay mode both sides only need an outgoing TCP connection
    /// to us, and we forward the game bytes - exactly like a plain TCP tunnel.
    ///
    /// Wire format (little endian, compatible with Kethen/aemu_postoffice, branch ppsspp):
    ///   INIT   24 bytes: int32 type | src[8] | u16 sport | dst[8] | u16 dport
    ///          type: 0=PDP, 1=PTP_LISTEN, 2=PTP_CONNECT, 3=PTP_ACCEPT, 4=PDP_UNRELIABLE
    ///   PDP    14 bytes: dstMac[8] | u16 dport | u32 size, then payload
    ///          server rewrites dstMac/port to the sender's and forwards to the PDP session
    ///   LISTEN receives a 10-byte notice: srcMac[8] | u16 sport
    ///   CONNECT receives a 10-byte ack:    peerMac[8] | u16 peerPort
    ///   ACCEPT  receives a 10-byte ack:    peerMac[8] | u16 peerPort
    ///   PTP data: u32 size | payload (relayed as is, both directions)
    ///
    /// PPSSPP hardcodes the relay port (AEMU_POSTOFFICE_PORT 27313) and uses it only when its
    /// "Try to use server-provided packet relay" option is Yes (or the host is in its relay list).
    /// </summary>
    public sealed class RelayServer
    {
        private const int InitSize = 24;
        private const int PdpHeaderSize = 14;
        private const int NoticeSize = 10;
        private const int MaxInitWaitMs = 15000;
        private const int ListenWaitMs = 2000;
        private const int AcceptWaitMs = 20000;
        private const int MaxIpSessionsGrace = 0;

        private sealed class RelayConn
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public string Id;
            public string RemoteIp = "0.0.0.0";
            public int RemotePort;
            public readonly object SendLock = new object();
            public readonly byte[] Src = new byte[6];
            public readonly byte[] Dst = new byte[6];
            public ushort Sport;
            public ushort Dport;
            public string Name = "";
            public int Kind;
            public volatile RelayConn Peer;
            public volatile bool WaitingAccept;
            public volatile bool Closed;
            public int CloseHandled;
            public long BytesIn;
            public long BytesOut;
            public long Frames;
            public DateTime CreatedUtc = DateTime.UtcNow;
            private long _lastActivityTicks = DateTime.UtcNow.Ticks;
            public string CloseReason = "";

            public void Touch() { Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks); }

            public TimeSpan Idle
            {
                get
                {
                    TimeSpan span = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                    return span.TotalSeconds < 0 ? TimeSpan.Zero : span;
                }
            }

            public TimeSpan Age
            {
                get
                {
                    TimeSpan span = DateTime.UtcNow - CreatedUtc;
                    return span.TotalSeconds < 0 ? TimeSpan.Zero : span;
                }
            }

            public void Close(string reason)
            {
                if (Interlocked.Exchange(ref CloseHandled, 1) != 0) return;
                Closed = true;
                CloseReason = reason ?? "";
                try { if (Stream != null) Stream.Close(); } catch { }
                try { if (Client != null) Client.Close(); } catch { }
            }
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly object _sync = new object();
        private readonly Dictionary<string, RelayConn> _sessions = new Dictionary<string, RelayConn>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _perIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byMac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _byMacSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private UdpClient _udpProbe;
        private Thread _acceptThread;
        private Thread _maintenanceThread;
        private volatile bool _running;
        private int _port;

        private int _active;
        private long _totalConnections;
        private long _pdpFrames;
        private long _pdpDrops;
        private long _ptpPairs;
        private long _bytesIn;
        private long _bytesOut;
        private DateTime _lastUdpWarnUtc = DateTime.MinValue;

        public event EventHandler Failed;

        public RelayServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
        }

        public int Port { get { return _port; } }
        public bool IsRunning { get { return _running && _acceptThread != null && _acceptThread.IsAlive; } }
        public int SessionCount { get { return Interlocked.CompareExchange(ref _active, 0, 0); } }
        public long TotalConnections { get { return Interlocked.Read(ref _totalConnections); } }
        public long PdpFrames { get { return Interlocked.Read(ref _pdpFrames); } }
        public long PdpDrops { get { return Interlocked.Read(ref _pdpDrops); } }
        public long BytesIn { get { return Interlocked.Read(ref _bytesIn); } }
        public long BytesOut { get { return Interlocked.Read(ref _bytesOut); } }
        public long PtpPairs { get { return Interlocked.Read(ref _ptpPairs); } }

        /// <summary>How many relay sessions this MAC has right now (0 = likely a direct/P2P player).</summary>
        public int CountForMac(byte[] mac)
        {
            if (mac == null) return 0;
            string key = MacKey(mac, 0);
            lock (_sync)
            {
                int count;
                return _byMac.TryGetValue(key, out count) ? count : 0;
            }
        }

        /// <summary>
        /// When this MAC last had a relay session. Games reopen their PDP sockets every time the
        /// in-game lobby is redone, so a momentary count of 0 does not mean "relay is off".
        /// </summary>
        public DateTime LastActivityForMac(byte[] mac)
        {
            if (mac == null) return DateTime.MinValue;
            string key = MacKey(mac, 0);
            lock (_sync)
            {
                DateTime seen;
                return _byMacSeen.TryGetValue(key, out seen) ? seen : DateTime.MinValue;
            }
        }

        /// <summary>PDP ports this MAC has open right now (for the public status feed).</summary>
        public List<int> PdpPortsForMac(byte[] mac)
        {
            List<int> ports = new List<int>();
            if (mac == null) return ports;
            string macKey = MacKey(mac, 0);
            lock (_sync)
            {
                foreach (KeyValuePair<string, RelayConn> kv in _sessions)
                {
                    RelayConn c = kv.Value;
                    if (c.Kind != 0 || c.Closed) continue;
                    if (!string.Equals(MacKey(c.Src, 0), macKey, StringComparison.Ordinal)) continue;
                    int port = c.Sport;
                    if (!ports.Contains(port)) ports.Add(port);
                }
            }
            ports.Sort();
            return ports;
        }

        // ------------------------------------------------------------ lifecycle

        public bool Start()
        {
            if (!_cfg.RelayEnabled) return false;
            if (_running) return true;

            IPAddress bind = ResolveBind(_cfg.RelayBind);
            int port = _cfg.RelayPort;

            try
            {
                TcpListener listener = new TcpListener(bind, port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start(128);
                _listener = listener;
                _port = port;
                _running = true;

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "relay-accept";
                _acceptThread.Start();

                _maintenanceThread = new Thread(MaintenanceLoop);
                _maintenanceThread.IsBackground = true;
                _maintenanceThread.Name = "relay-maintenance";
                _maintenanceThread.Start();

                Log.Info("RELAY", "PPSSPP relay (aemu_postoffice) listening on tcp/" + bind + ":" +
                                 port.ToString(CultureInfo.InvariantCulture) +
                                 " - enable 'Try to use server-provided packet relay' in PPSSPP");
            }
            catch (Exception ex)
            {
                Log.Warn("RELAY", "cannot start relay on " + _cfg.RelayBind + ":" + port + ": " + ex.Message);
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
                _running = false;
                return false;
            }

            StartUdpProbe(bind, port);
            return true;
        }

        private static IPAddress ResolveBind(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "*" || text == "0.0.0.0") return IPAddress.Any;
            IPAddress ip;
            if (IPAddress.TryParse(text, out ip)) return ip;
            return IPAddress.Any;
        }

        /// <summary>
        /// PPSSPP can also use ENet/UDP for the relay (AdhocUseUdp=Yes). We do not implement that,
        /// but a UDP socket on the same port lets us tell the user why it does not work.
        /// </summary>
        private void StartUdpProbe(IPAddress bind, int port)
        {
            if (!_cfg.RelayUdpProbe) return;
            try
            {
                UdpClient udp = new UdpClient(port);
                _udpProbe = udp;
                Thread t = new Thread(delegate ()
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    while (_running)
                    {
                        try
                        {
                            byte[] data = udp.Receive(ref remote);
                            if (data == null) continue;
                            if ((DateTime.UtcNow - _lastUdpWarnUtc).TotalSeconds > 60)
                            {
                                _lastUdpWarnUtc = DateTime.UtcNow;
                                Log.Warn("RELAY", "UDP/ENet packet on the relay port from " + remote +
                                                  " - PPSSPP is using UDP relay mode; set AdhocUseUdp=No " +
                                                  "(the relay speaks TCP)");
                            }
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (SocketException) { break; }
                        catch { }
                    }
                });
                t.IsBackground = true;
                t.Name = "relay-udp-probe";
                t.Start();
            }
            catch (Exception ex)
            {
                Log.Debug("RELAY", "UDP probe not available: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (!_running && _listener == null) return;
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            try { if (_udpProbe != null) _udpProbe.Close(); } catch { }
            _udpProbe = null;

            List<RelayConn> all;
            lock (_sync) { all = new List<RelayConn>(_sessions.Values); }
            for (int i = 0; i < all.Count; i++) all[i].Close("shutdown");

            Join(_acceptThread, 2000);
            Join(_maintenanceThread, 2000);
            _acceptThread = null;
            _maintenanceThread = null;
            Log.Info("RELAY", "relay stopped");
        }

        public bool Restart()
        {
            Log.Warn("RELAY", "restarting relay");
            try { Stop(); } catch { }
            Thread.Sleep(300);
            return Start();
        }

        private static void Join(Thread t, int ms)
        {
            try { if (t != null && t.IsAlive) t.Join(ms); } catch { }
        }

        // -------------------------------------------------------------- accept

        private void AcceptLoop()
        {
            int failures = 0;
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    failures = 0;
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException sex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Warn("RELAY", "accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25)
                    {
                        try { EventHandler h = Failed; if (h != null) h(this, EventArgs.Empty); } catch { }
                        break;
                    }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Error("RELAY", "accept error: " + ex.Message);
                    Thread.Sleep(100);
                    continue;
                }

                if (client == null) continue;

                string ip = "0.0.0.0";
                int remotePort = 0;
                try
                {
                    IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
                    if (ep != null)
                    {
                        ip = Util.Ip(ep.Address);
                        remotePort = ep.Port;
                    }
                }
                catch { }

                bool allowed;
                lock (_sync)
                {
                    if (SessionCount >= _cfg.RelayMaxSessions) allowed = false;
                    else
                    {
                        int count;
                        _perIp.TryGetValue(ip, out count);
                        allowed = count < _cfg.RelayMaxPerIp;
                    }
                }

                if (!allowed)
                {
                    Log.Warn("RELAY", "rejecting " + ip + ": limit reached (sessions " + SessionCount +
                                      "/" + _cfg.RelayMaxSessions + ")");
                    try { client.Close(); } catch { }
                    continue;
                }

                RelayConn conn = new RelayConn();
                conn.Client = client;
                conn.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                conn.RemoteIp = ip;
                conn.RemotePort = remotePort;
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 1000;
                    client.SendTimeout = 10000;
                    client.ReceiveBufferSize = 32768;
                    client.SendBufferSize = 32768;
                    conn.Stream = client.GetStream();
                }
                catch (Exception ex)
                {
                    Log.Warn("RELAY", "cannot prepare connection from " + ip + ": " + ex.Message);
                    conn.Close("setup-failed");
                    continue;
                }

                Interlocked.Increment(ref _active);
                Interlocked.Increment(ref _totalConnections);

                Thread t = new Thread(ConnectionLoop);
                t.IsBackground = true;
                t.Name = "relay-" + conn.Id;
                try { t.Start(conn); }
                catch (Exception ex)
                {
                    Log.Error("RELAY", "cannot start relay thread: " + ex.Message);
                    conn.Close("thread-start-failed");
                }
            }
        }

        // ------------------------------------------------------------ session

        private void ConnectionLoop(object state)
        {
            RelayConn c = (RelayConn)state;
            string reason = "closed";
            try
            {
                byte[] init = new byte[InitSize];
                if (!ReadExact(c, init, InitSize, MaxInitWaitMs))
                {
                    reason = "init-timeout";
                    return;
                }

                int type = ReadI32(init, 0);
                Buffer.BlockCopy(init, 4, c.Src, 0, 6);
                c.Sport = ReadU16(init, 12);
                Buffer.BlockCopy(init, 14, c.Dst, 0, 6);
                c.Dport = ReadU16(init, 22);

                switch (type)
                {
                    case 0: // PDP
                    case 4: // PDP_UNRELIABLE
                        c.Kind = 0;
                        reason = HandlePdp(c);
                        break;
                    case 1: // PTP_LISTEN
                        c.Kind = 1;
                        reason = HandleListen(c);
                        break;
                    case 2: // PTP_CONNECT
                        c.Kind = 2;
                        reason = HandleConnect(c);
                        break;
                    case 3: // PTP_ACCEPT
                        c.Kind = 3;
                        reason = HandleAccept(c);
                        break;
                    default:
                        reason = "bad-init-type";
                        Log.Warn("RELAY", "bad init type " + type + " from " + c.RemoteIp);
                        break;
                }
            }
            catch (Exception ex)
            {
                reason = "exception";
                Log.Debug("RELAY", "connection " + c.Id + " crashed: " + ex.Message);
            }
            finally
            {
                c.Close(reason);
                Unregister(c);
                Interlocked.Decrement(ref _active);
                Log.Info("RELAY", "session closed " + c.RemoteIp + ":" + c.RemotePort +
                                  " kind=" + c.Kind + " name=\"" + c.Name + "\" reason=" + (c.CloseReason.Length > 0 ? c.CloseReason : reason) +
                                  " in=" + Util.HumanSize(c.BytesIn) + " out=" + Util.HumanSize(c.BytesOut));
            }
        }

        private string HandlePdp(RelayConn c)
        {
            string name = "PDP " + MacKey(c.Src, 0) + " " + KeyPort(c.Sport).ToString(CultureInfo.InvariantCulture);
            Register(c, name);

            byte[] header = new byte[PdpHeaderSize];
            while (_running && !c.Closed)
            {
                if (!ReadExact(c, header, PdpHeaderSize, 0)) return "peer-closed";

                ushort dport = ReadU16(header, 8);
                uint size = ReadU32(header, 10);
                if (size > (uint)_cfg.RelayMaxPdpBytes)
                {
                    Log.Warn("RELAY", "PDP frame too big (" + size + "B) from " + c.RemoteIp + ", dropping session");
                    return "frame-too-big";
                }

                byte[] payload = size > 0 ? new byte[size] : new byte[0];
                if (size > 0 && !ReadExact(c, payload, (int)size, 0)) return "peer-closed";

                Interlocked.Increment(ref c.Frames);
                Interlocked.Increment(ref _pdpFrames);

                short delta;
                RelayConn target = FindPdpTarget(header, dport, out delta);

                if (target == null || target.Closed)
                {
                    Interlocked.Increment(ref _pdpDrops);
                    NotePdpDrop(header, dport);
                    continue;
                }

                // Different PPSSPP "Port offset" settings shift every port by a constant.
                // Tell the receiver the sender port in its own offset space.
                ushort outPort = c.Sport;
                if (_cfg.RelayPortTranslation && delta != 0)
                {
                    outPort = unchecked((ushort)(c.Sport + delta));
                    NotePortTranslation(c, target, delta);
                }

                byte[] frame = new byte[PdpHeaderSize + size];
                Buffer.BlockCopy(c.Src, 0, frame, 0, 6);
                WriteU16(frame, 8, outPort);
                WriteU32(frame, 10, size);
                if (size > 0) Buffer.BlockCopy(payload, 0, frame, PdpHeaderSize, (int)size);

                if (!Send(target, frame, 0, frame.Length)) target.Close("send-failed");
            }
            return "peer-closed";
        }

        private string HandleListen(RelayConn c)
        {
            string name = "PTP_LISTEN " + MacKey(c.Src, 0) + " " + KeyPort(c.Sport).ToString(CultureInfo.InvariantCulture);
            Register(c, name);

            // The client never sends anything on a listen socket; it only waits for 10-byte
            // connection notices. Any data is ignored, EOF means the game closed the socket.
            byte[] buf = new byte[256];
            while (_running && !c.Closed)
            {
                int n;
                try { n = c.Stream.Read(buf, 0, buf.Length); }
                catch (IOException io) { if (IsTimeout(io)) continue; return "io-error"; }
                catch (ObjectDisposedException) { return "closed"; }
                catch (SocketException) { return "socket-error"; }
                if (n <= 0) return "peer-closed";
                c.Touch();
            }
            return "shutdown";
        }

        private string HandleConnect(RelayConn c)
        {
            string connector = MacKey(c.Src, 0);
            string listener = MacKey(c.Dst, 0);
            string listenKey = "PTP_LISTEN " + listener + " " + KeyPort(c.Dport).ToString(CultureInfo.InvariantCulture);

            RelayConn listenSession = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ListenWaitMs);
            while (_running && !c.Closed && DateTime.UtcNow < deadline)
            {
                lock (_sync)
                {
                    RelayConn candidate;
                    if (_sessions.TryGetValue(listenKey, out candidate) && !candidate.Closed)
                        listenSession = candidate;
                }
                if (listenSession == null) listenSession = FindUniqueSession(1, c.Dst);
                if (listenSession != null) break;
                Thread.Sleep(50);
            }

            if (listenSession == null) return "no-listener";

            string name = "PTP_CONNECT " + connector + " " + KeyPort(c.Sport).ToString(CultureInfo.InvariantCulture) +
                          " " + listener + " " + KeyPort(c.Dport).ToString(CultureInfo.InvariantCulture);
            Register(c, name);
            c.WaitingAccept = true;

            short delta = PortDelta(listenSession.Sport, c.Dport);
            ushort notifyPort = c.Sport;
            if (_cfg.RelayPortTranslation && delta != 0)
            {
                notifyPort = unchecked((ushort)(c.Sport + delta));
                NotePortTranslation(c, listenSession, delta);
            }

            byte[] notice = new byte[NoticeSize];
            Buffer.BlockCopy(c.Src, 0, notice, 0, 6);
            WriteU16(notice, 8, notifyPort);
            if (!Send(listenSession, notice, 0, notice.Length))
            {
                listenSession.Close("send-failed");
                return "notify-failed";
            }

            deadline = DateTime.UtcNow.AddMilliseconds(AcceptWaitMs);
            while (_running && !c.Closed && c.Peer == null && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (c.Peer == null) return "accept-timeout";

            Interlocked.Increment(ref _ptpPairs);
            RelayRaw(c);
            return "peer-closed";
        }

        private string HandleAccept(RelayConn c)
        {
            string connectKey = "PTP_CONNECT " + MacKey(c.Dst, 0) + " " + KeyPort(c.Dport).ToString(CultureInfo.InvariantCulture) +
                                " " + MacKey(c.Src, 0) + " " + KeyPort(c.Sport).ToString(CultureInfo.InvariantCulture);

            RelayConn connectSession = null;
            lock (_sync)
            {
                RelayConn candidate;
                if (_sessions.TryGetValue(connectKey, out candidate) &&
                    !candidate.Closed && candidate.WaitingAccept && candidate.Peer == null)
                    connectSession = candidate;
            }

            if (connectSession == null)
            {
                // fall back to the single waiting connector for this pair (offset mismatch)
                lock (_sync)
                {
                    foreach (KeyValuePair<string, RelayConn> kv in _sessions)
                    {
                        RelayConn candidate = kv.Value;
                        if (candidate.Kind != 2 || candidate.Closed || !candidate.WaitingAccept || candidate.Peer != null)
                            continue;
                        if (MacKey(candidate.Src, 0) != MacKey(c.Dst, 0)) continue;
                        if (MacKey(candidate.Dst, 0) != MacKey(c.Src, 0)) continue;
                        if (connectSession != null) { connectSession = null; break; } // ambiguous
                        connectSession = candidate;
                    }
                }
            }

            if (connectSession == null) return "no-connect-waiting";

            string name = "PTP_ACCEPT " + MacKey(c.Src, 0) + " " + KeyPort(c.Sport).ToString(CultureInfo.InvariantCulture) +
                          " " + MacKey(c.Dst, 0) + " " + KeyPort(c.Dport).ToString(CultureInfo.InvariantCulture);
            Register(c, name);

            // connector expects the listener port in its own offset space (stored at connect time)
            byte[] ackForConnect = new byte[NoticeSize];
            Buffer.BlockCopy(c.Src, 0, ackForConnect, 0, 6);
            WriteU16(ackForConnect, 8, connectSession.Dport);

            // listener expects the connector port in its own offset space (already translated
            // by the connect notice, so the ACCEPT init carries it)
            byte[] ackForAccept = new byte[NoticeSize];
            Buffer.BlockCopy(c.Dst, 0, ackForAccept, 0, 6);
            WriteU16(ackForAccept, 8, c.Dport);

            if (!Send(connectSession, ackForConnect, 0, ackForConnect.Length)) return "ack-failed";
            if (!Send(c, ackForAccept, 0, ackForAccept.Length)) return "ack-failed";

            c.Peer = connectSession;
            connectSession.Peer = c;
            connectSession.WaitingAccept = false;

            Interlocked.Increment(ref _ptpPairs);
            RelayRaw(c);
            return "peer-closed";
        }

        // ------------------------------------------------------- port matching

        private static ushort NormPort(ushort port) { return (ushort)(port % 10000); }

        private ushort KeyPort(ushort port)
        {
            return _cfg.RelayPortTranslation ? NormPort(port) : port;
        }

        /// <summary>Signed 16-bit distance between the registered port and the port a sender used.</summary>
        private static short PortDelta(ushort registered, ushort wanted)
        {
            int d = registered - wanted;
            while (d > 32767) d -= 65536;
            while (d < -32768) d += 65536;
            return (short)d;
        }

        private RelayConn FindPdpTarget(byte[] header, ushort dport, out short delta)
        {
            delta = 0;
            string key = "PDP " + MacKey(header, 0) + " " + KeyPort(dport).ToString(CultureInfo.InvariantCulture);
            RelayConn target;
            lock (_sync) { _sessions.TryGetValue(key, out target); }

            if (target == null || target.Closed)
            {
                byte[] mac = new byte[6];
                Buffer.BlockCopy(header, 0, mac, 0, 6);
                target = FindUniqueSession(0, mac);
            }

            if (target == null || target.Closed) return null;
            delta = PortDelta(target.Sport, dport);
            return target;
        }

        /// <summary>Last resort for offsets that are not multiples of 10000: one session per MAC.</summary>
        private RelayConn FindUniqueSession(int kind, byte[] mac)
        {
            string macKey = MacKey(mac, 0);
            RelayConn found = null;
            lock (_sync)
            {
                foreach (KeyValuePair<string, RelayConn> kv in _sessions)
                {
                    RelayConn c = kv.Value;
                    if (c.Kind != kind || c.Closed) continue;
                    if (!string.Equals(MacKey(c.Src, 0), macKey, StringComparison.Ordinal)) continue;
                    if (found != null) return null; // ambiguous, better to drop than to guess
                    found = c;
                }
            }
            return found;
        }

        private readonly object _portHintLock = new object();
        private readonly Dictionary<string, DateTime> _portHints = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private void NotePortTranslation(RelayConn from, RelayConn to, short delta)
        {
            string key = MacKey(from.Src, 0) + ">" + MacKey(to.Src, 0);
            lock (_portHintLock)
            {
                DateTime last;
                if (_portHints.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalSeconds < 600) return;
                _portHints[key] = DateTime.UtcNow;
                if (_portHints.Count > 200) _portHints.Clear();
            }

            string text = "PPSSPP port offsets differ (delta " + delta + ") between " +
                          MacKey(from.Src, 0) + " and " + MacKey(to.Src, 0) +
                          "; the relay translates them, but set the same 'Port offset' on every client " +
                          "for the cleanest result.";
            Log.Warn("RELAY", text);
            try { _app.State.AddEvent("hint", text, "", "", ""); } catch { }
        }

        private readonly object _dropHintLock = new object();
        private readonly Dictionary<string, DateTime> _dropHints = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A PDP frame arrived for a MAC that never opened a relay session. In practice that means
        /// the receiver plays direct UDP (relay off on their side), or its session is not up yet.
        /// Warn once per destination MAC instead of silently dropping frames.
        /// </summary>
        private void NotePdpDrop(byte[] header, ushort dport)
        {
            byte[] mac = new byte[6];
            Buffer.BlockCopy(header, 0, mac, 0, 6);
            string dst = MacKey(mac, 0);
            lock (_dropHintLock)
            {
                DateTime last;
                if (_dropHints.TryGetValue(dst, out last) && (DateTime.UtcNow - last).TotalSeconds < 60) return;
                _dropHints[dst] = DateTime.UtcNow;
                if (_dropHints.Count > 200) _dropHints.Clear();
            }

            // Name the player when we can: "no relay for NickBock" is actionable, a MAC is not.
            string who = "";
            try { who = _app.State.NicknameForMac(mac); } catch { }
            string label = who.Length > 0 ? who + " (" + dst + ")" : dst;

            string text = "PDP frame (dst port " + dport + ") for " + label +
                          " dropped: no relay session for that player. They probably have " +
                          "'Try to use server-provided packet relay' turned off, or their game has not " +
                          "opened the session yet - enable the relay for every player.";
            Log.Warn("RELAY", text);
            try { _app.State.AddEvent("hint", text, "", "", who); } catch { }
        }

        /// <summary>Bidirectional raw byte pump for a linked PTP pair.</summary>
        private void RelayRaw(RelayConn c)
        {
            byte[] buf = new byte[16384];
            while (_running && !c.Closed)
            {
                RelayConn peer = c.Peer;
                if (peer == null || peer.Closed) return;

                int n;
                try { n = c.Stream.Read(buf, 0, buf.Length); }
                catch (IOException io) { if (IsTimeout(io)) continue; return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }

                if (n <= 0) return;
                c.Touch();
                Interlocked.Add(ref c.BytesIn, n);
                Interlocked.Add(ref _bytesIn, n);

                if (!Send(peer, buf, 0, n))
                {
                    peer.Close("send-failed");
                    return;
                }
            }
        }

        // ------------------------------------------------------------ registry

        private void Register(RelayConn c, string name)
        {
            List<RelayConn> replaced = null;
            lock (_sync)
            {
                RelayConn existing;
                if (_sessions.TryGetValue(name, out existing) && existing != c)
                {
                    replaced = new List<RelayConn>();
                    replaced.Add(existing);
                }

                if (replaced != null) _sessions.Remove(name);
                _sessions[name] = c;
                c.Name = name;

                int count;
                _perIp.TryGetValue(c.RemoteIp, out count);
                _perIp[c.RemoteIp] = count + 1;

                string macKey = MacKey(c.Src, 0);
                int macCount;
                _byMac.TryGetValue(macKey, out macCount);
                _byMac[macKey] = macCount + 1;
                _byMacSeen[macKey] = DateTime.UtcNow;
            }

            c.Name = name;
            Log.Debug("RELAY", "session " + c.RemoteIp + ":" + c.RemotePort + " registered as \"" + name + "\"");

            if (replaced != null)
            {
                for (int i = 0; i < replaced.Count; i++) replaced[i].Close("replaced");
            }
        }

        private void Unregister(RelayConn c)
        {
            List<RelayConn> peers = new List<RelayConn>();
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(c.Name))
                {
                    RelayConn current;
                    if (_sessions.TryGetValue(c.Name, out current) && current == c)
                        _sessions.Remove(c.Name);
                }

                int count;
                if (_perIp.TryGetValue(c.RemoteIp, out count))
                {
                    if (count <= 1) _perIp.Remove(c.RemoteIp);
                    else _perIp[c.RemoteIp] = count - 1;
                }

                string macKey = MacKey(c.Src, 0);
                int macCount;
                if (_byMac.TryGetValue(macKey, out macCount))
                {
                    if (macCount <= 1) _byMac.Remove(macKey);
                    else _byMac[macKey] = macCount - 1;
                }

                // Stamp the moment the relay was last used, not just the moment it was opened:
                // games keep one PDP session alive for minutes, and a stale stamp made the
                // mixed-transport check report "no relay" for a player who had it on.
                _byMacSeen[macKey] = DateTime.UtcNow;

                RelayConn peer = c.Peer;
                if (peer != null)
                {
                    c.Peer = null;
                    peer.Peer = null;
                    peers.Add(peer);
                }
            }

            for (int i = 0; i < peers.Count; i++) peers[i].Close("peer-gone");
        }

        // ---------------------------------------------------------------- io

        private bool Send(RelayConn c, byte[] data, int offset, int count)
        {
            if (c == null || count <= 0) return true;
            if (c.Closed) return false;
            lock (c.SendLock)
            {
                if (c.Closed) return false;
                try
                {
                    c.Stream.Write(data, offset, count);
                    c.Touch();
                    Interlocked.Add(ref c.BytesOut, count);
                    Interlocked.Add(ref _bytesOut, count);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Reads exactly count bytes. deadlineMs 0 means "wait forever" (checks shutdown every second).</summary>
        private bool ReadExact(RelayConn c, byte[] buffer, int count, int deadlineMs)
        {
            int got = 0;
            DateTime deadline = deadlineMs > 0 ? DateTime.UtcNow.AddMilliseconds(deadlineMs) : DateTime.MinValue;
            while (got < count)
            {
                if (!_running || c.Closed) return false;
                if (deadlineMs > 0 && DateTime.UtcNow > deadline) return false;

                int n;
                try { n = c.Stream.Read(buffer, got, count - got); }
                catch (IOException io) { if (IsTimeout(io)) continue; return false; }
                catch (ObjectDisposedException) { return false; }
                catch (SocketException) { return false; }

                if (n <= 0) return false;
                got += n;
                c.Touch();
                Interlocked.Add(ref c.BytesIn, n);
                Interlocked.Add(ref _bytesIn, n);
            }
            return true;
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

        // -------------------------------------------------------- little endian

        private static int ReadI32(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }

        private static ushort ReadU16(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        private static uint ReadU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void WriteU16(byte[] b, int o, int value)
        {
            b[o] = (byte)(value & 0xff);
            b[o + 1] = (byte)((value >> 8) & 0xff);
        }

        private static void WriteU32(byte[] b, int o, uint value)
        {
            b[o] = (byte)(value & 0xff);
            b[o + 1] = (byte)((value >> 8) & 0xff);
            b[o + 2] = (byte)((value >> 16) & 0xff);
            b[o + 3] = (byte)((value >> 24) & 0xff);
        }

        private static string MacKey(byte[] data, int offset)
        {
            return data[offset].ToString("x2", CultureInfo.InvariantCulture) + ":" +
                   data[offset + 1].ToString("x2", CultureInfo.InvariantCulture) + ":" +
                   data[offset + 2].ToString("x2", CultureInfo.InvariantCulture) + ":" +
                   data[offset + 3].ToString("x2", CultureInfo.InvariantCulture) + ":" +
                   data[offset + 4].ToString("x2", CultureInfo.InvariantCulture) + ":" +
                   data[offset + 5].ToString("x2", CultureInfo.InvariantCulture);
        }

        // --------------------------------------------------------- maintenance

        private void MaintenanceLoop()
        {
            while (_running)
            {
                try { Thread.Sleep(5000); } catch { }
                if (!_running) break;

                try
                {
                    List<RelayConn> all;
                    lock (_sync) { all = new List<RelayConn>(_sessions.Values); }

                    int idleLimit = _cfg.RelayIdleSeconds;
                    int lifetimeLimit = _cfg.RelayLifetimeSeconds;

                    for (int i = 0; i < all.Count; i++)
                    {
                        RelayConn c = all[i];
                        if (c.Closed) continue;
                        if (lifetimeLimit > 0 && c.Age.TotalSeconds >= lifetimeLimit)
                        {
                            c.Close("lifetime");
                            continue;
                        }
                        if (idleLimit <= 0 || c.Peer != null) continue;

                        // A PTP_LISTEN socket is silent by design: the client opens it and waits
                        // for 10-byte notices, it never sends anything itself. Reaping it after
                        // idleSeconds killed live matches (Killzone Liberation crashed after
                        // ~10 minutes because its listen socket was closed mid-game).
                        if (c.Kind == 1) continue;

                        // Sessions that already relayed frames may just be sitting in a menu;
                        // give them a much longer grace before assuming the client is gone.
                        // A session that never carried a single frame (only the INIT handshake)
                        // is a leftover from a client that vanished - reap it at the short limit.
                        double limit = c.Frames > 0 ? Math.Max(idleLimit * 4, 1800) : idleLimit;

                        if (c.Idle.TotalSeconds >= limit) c.Close("idle");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("RELAY", "maintenance: " + ex.Message);
                }
            }
        }

        // --------------------------------------------------------------- stats

        public string StatusLine()
        {
            return "relay tcp/" + _port + " | sessions " + SessionCount + "/" + _cfg.RelayMaxSessions +
                   " | pairs " + Interlocked.Read(ref _ptpPairs) +
                   " | pdp " + Interlocked.Read(ref _pdpFrames) + " (" + Interlocked.Read(ref _pdpDrops) + " dropped)" +
                   " | in " + Util.HumanSize(Interlocked.Read(ref _bytesIn)) +
                   " out " + Util.HumanSize(Interlocked.Read(ref _bytesOut));
        }

        public List<string> SessionSnapshot(int limit)
        {
            List<string> list = new List<string>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, RelayConn> kv in _sessions)
                {
                    if (list.Count >= limit) break;
                    RelayConn c = kv.Value;
                    list.Add(c.RemoteIp + "|" + c.Kind + "|" + c.Name + "|" + (int)c.Idle.TotalSeconds +
                             "|" + c.BytesIn + "|" + c.BytesOut);
                }
            }
            return list;
        }
    }
}