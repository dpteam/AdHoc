using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc.Net
{
    public enum SessionState
    {
        Waiting = 0,
        LoggedIn = 1,
        Closed = 2
    }

    /// <summary>
    /// One TCP connection speaking the adhocctl protocol.
    /// Read side is driven by a single owner thread, write side is serialised by _sendLock.
    /// </summary>
    public sealed class ClientSession
    {
        private readonly object _sendLock = new object();
        private int _closing;
        private long _lastRecvTicks;
        private long _lastSendTicks;
        private long _bytesIn;
        private long _bytesOut;
        private int _pings;
        private int _packetsIn;
        private int _packetsOut;
        private string _closeReason = "";

        public readonly string Id;
        public readonly TcpClient Client;
        public readonly Socket Socket;
        public readonly NetworkStream Stream;
        public readonly byte[] RemoteIpBytes;
        public readonly string RemoteIp;
        public readonly int RemotePort;

        public byte[] Rx = new byte[512];
        public int RxLen;

        public string Nickname = "";
        public byte[] Nickname128 = new byte[Packets.NicknameSize];
        public byte[] Mac = new byte[6];
        public string MacText = "00:00:00:00:00:00";
        public string RawProductCode = "";
        public string ProductCode = "";
        public string GameName = "";
        public SessionState State = SessionState.Waiting;
        public DateTime ConnectUtc = DateTime.UtcNow;

        // Owned by ServerState.Sync
        public GameEntry Game;
        public GroupEntry Group;

        public string LastPacketText = "";
        public int CloseHandled; // set exactly once by AdhocServer.CloseSession
        public string CloseReason { get { return _closeReason; } }

        public ClientSession(TcpClient client, Config cfg)
        {
            Client = client;
            Socket = client.Client;
            Stream = client.GetStream();
            Id = Guid.NewGuid().ToString("N").Substring(0, 10);

            try { client.NoDelay = true; } catch { }
            try { client.ReceiveTimeout = cfg.ReadTimeoutMs; } catch { }
            try { client.SendTimeout = cfg.SendTimeoutMs; } catch { }
            try { client.ReceiveBufferSize = 16384; } catch { }
            try { client.SendBufferSize = 16384; } catch { }
            try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

            IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
            if (ep != null)
            {
                RemoteIpBytes = ep.Address.GetAddressBytes();
                RemoteIp = Util.Ip(ep.Address);
                RemotePort = ep.Port;
            }
            else
            {
                RemoteIpBytes = new byte[] { 0, 0, 0, 0 };
                RemoteIp = "0.0.0.0";
                RemotePort = 0;
            }

            ConnectUtc = DateTime.UtcNow;
            _lastRecvTicks = ConnectUtc.Ticks;
            _lastSendTicks = ConnectUtc.Ticks;
        }

        public bool IsOpen
        {
            get
            {
                if (Interlocked.CompareExchange(ref _closing, 0, 0) != 0) return false;
                try { return Client.Connected; }
                catch { return false; }
            }
        }

        public bool IsClosing { get { return Interlocked.CompareExchange(ref _closing, 0, 0) != 0; } }

        public DateTime LastRecvUtc
        {
            get { return new DateTime(Interlocked.Read(ref _lastRecvTicks), DateTimeKind.Utc); }
        }

        public DateTime LastSendUtc
        {
            get { return new DateTime(Interlocked.Read(ref _lastSendTicks), DateTimeKind.Utc); }
        }

        public long BytesIn { get { return Interlocked.Read(ref _bytesIn); } }
        public long BytesOut { get { return Interlocked.Read(ref _bytesOut); } }
        public int Pings { get { return Interlocked.CompareExchange(ref _pings, 0, 0); } }
        public int PacketsIn { get { return Interlocked.CompareExchange(ref _packetsIn, 0, 0); } }
        public int PacketsOut { get { return Interlocked.CompareExchange(ref _packetsOut, 0, 0); } }

        public TimeSpan ConnectedFor
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - ConnectUtc;
                return span.TotalSeconds < 0 ? TimeSpan.Zero : span;
            }
        }

        public TimeSpan IdleFor
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - LastRecvUtc;
                return span.TotalSeconds < 0 ? TimeSpan.Zero : span;
            }
        }

        public void TouchRecv() { Interlocked.Exchange(ref _lastRecvTicks, DateTime.UtcNow.Ticks); }

        public void AddReceived(int bytes)
        {
            Interlocked.Add(ref _bytesIn, bytes);
            Interlocked.Increment(ref _packetsIn);
        }

        public void CountPing() { Interlocked.Increment(ref _pings); }

        public bool Send(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            if (IsClosing) return false;
            lock (_sendLock)
            {
                if (IsClosing) return false;
                try
                {
                    Stream.Write(data, 0, data.Length);
                    Interlocked.Exchange(ref _lastSendTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Add(ref _bytesOut, data.Length);
                    Interlocked.Increment(ref _packetsOut);
                    Log.Proto("PROTO", "-> {0} [{1}] {2}", RemoteIp, Id,
                        Packets.Describe(data, 0, data.Length, true));
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Debug("PROTO", "send to " + RemoteIp + " failed: " + ex.Message);
                    RequestClose("send-failed");
                    return false;
                }
            }
        }

        public void RequestClose(string reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0) return;
            _closeReason = reason == null ? "" : reason;
            try { Socket.Shutdown(SocketShutdown.Both); } catch { }
            try { Stream.Close(); } catch { }
            try { Client.Close(); } catch { }
        }

        public void EnsureRxCapacity(int needed)
        {
            if (needed <= Rx.Length) return;
            int next = Rx.Length;
            while (next < needed) next *= 2;
            byte[] bigger = new byte[next];
            Buffer.BlockCopy(Rx, 0, bigger, 0, RxLen);
            Rx = bigger;
        }

        public void Consume(int count)
        {
            if (count <= 0) return;
            if (count >= RxLen) { RxLen = 0; return; }
            Buffer.BlockCopy(Rx, count, Rx, 0, RxLen - count);
            RxLen -= count;
        }

        public string GroupName { get { GroupEntry g = Group; return g == null ? "" : g.Name; } }

        public string GameId { get { GameEntry g = Game; return g == null ? "" : g.Id; } }

        public override string ToString()
        {
            return (Nickname.Length > 0 ? Nickname : "?") + "@" + RemoteIp + " [" + Id + "]";
        }
    }
}