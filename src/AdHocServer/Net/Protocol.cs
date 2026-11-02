using System;
using System.Globalization;
using System.Text;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc.Net
{
    /// <summary>PRO Online / PPSSPP adhocctl opcodes (SceNetAdhocctl* packets).</summary>
    public static class Op
    {
        public const byte Ping = 0;          // C2S keepalive, 1 byte
        public const byte Login = 1;         // C2S 144 bytes
        public const byte Connect = 2;       // C2S 9 bytes  | S2C 139 bytes
        public const byte Disconnect = 3;    // C2S 1 byte   | S2C 5 bytes
        public const byte Scan = 4;          // C2S 1 byte   | S2C 15 bytes
        public const byte ScanComplete = 5;  // S2C 1 byte
        public const byte ConnectBssid = 6;  // S2C 7 bytes
        public const byte Chat = 7;          // C2S 65 bytes | S2C 193 bytes
    }

    public static class Packets
    {
        public const int MacSize = 6;
        public const int NicknameSize = 128;
        public const int GroupNameSize = 8;
        public const int ProductCodeSize = 9;
        public const int MessageSize = 64;
        public const int IpSize = 4;

        public const int LoginSize = 1 + MacSize + NicknameSize + ProductCodeSize;     // 144
        public const int ConnectRequestSize = 1 + GroupNameSize;                        // 9
        public const int ChatRequestSize = 1 + MessageSize;                             // 65

        public const int ConnectBroadcastSize = 1 + NicknameSize + MacSize + IpSize;     // 139
        public const int DisconnectBroadcastSize = 1 + IpSize;                           // 5
        public const int ScanResultSize = 1 + GroupNameSize + MacSize;                   // 15
        public const int ChatBroadcastSize = 1 + MessageSize + NicknameSize;             // 193

        public static byte[] ConnectBssid(byte[] mac)
        {
            byte[] p = new byte[7];
            p[0] = Op.ConnectBssid;
            CopyMac(mac, p, 1);
            return p;
        }

        public static byte[] ConnectBroadcast(byte[] nickname128, byte[] mac, byte[] ip4)
        {
            byte[] p = new byte[ConnectBroadcastSize];
            p[0] = Op.Connect;
            if (nickname128 != null) Buffer.BlockCopy(nickname128, 0, p, 1, Math.Min(NicknameSize, nickname128.Length));
            CopyMac(mac, p, 1 + NicknameSize);
            if (ip4 != null) Buffer.BlockCopy(ip4, 0, p, 1 + NicknameSize + MacSize, Math.Min(IpSize, ip4.Length));
            return p;
        }

        public static byte[] DisconnectBroadcast(byte[] ip4)
        {
            byte[] p = new byte[DisconnectBroadcastSize];
            p[0] = Op.Disconnect;
            if (ip4 != null) Buffer.BlockCopy(ip4, 0, p, 1, Math.Min(IpSize, ip4.Length));
            return p;
        }

        public static byte[] ScanResult(string groupName, byte[] hostMac)
        {
            byte[] p = new byte[ScanResultSize];
            p[0] = Op.Scan;
            byte[] g = Util.FixedUtf8(groupName, GroupNameSize);
            Buffer.BlockCopy(g, 0, p, 1, GroupNameSize);
            CopyMac(hostMac, p, 1 + GroupNameSize);
            return p;
        }

        public static byte[] ScanComplete()
        {
            return new byte[] { Op.ScanComplete };
        }

        public static byte[] ChatBroadcast(string message, byte[] senderNickname128)
        {
            byte[] p = new byte[ChatBroadcastSize];
            p[0] = Op.Chat;
            byte[] msg = Util.FixedUtf8(Util.Clean(message, MessageSize - 1), MessageSize);
            Buffer.BlockCopy(msg, 0, p, 1, MessageSize);
            if (senderNickname128 != null)
                Buffer.BlockCopy(senderNickname128, 0, p, 1 + MessageSize, Math.Min(NicknameSize, senderNickname128.Length));
            return p;
        }

        public static byte[] Ping()
        {
            return new byte[] { Op.Ping };
        }

        private static void CopyMac(byte[] mac, byte[] target, int offset)
        {
            if (mac == null) return;
            Buffer.BlockCopy(mac, 0, target, offset, Math.Min(MacSize, mac.Length));
        }

        /// <summary>Human readable packet description for the protocol log.</summary>
        public static string Describe(byte[] buf, int offset, int count, bool outbound)
        {
            if (buf == null || count <= 0) return "(empty)";
            byte op = buf[offset];
            string dir = outbound ? "S2C" : "C2S";
            switch (op)
            {
                case Op.Ping:
                    return dir + " PING";
                case Op.Login:
                    if (count < LoginSize) return dir + " LOGIN (truncated " + count + "B)";
                    {
                        byte[] mac = new byte[6];
                        Buffer.BlockCopy(buf, offset + 1, mac, 0, 6);
                        string nick = Util.FixedUtf8ToString(buf, offset + 7, NicknameSize);
                        string game = Util.FixedUtf8ToString(buf, offset + 135, ProductCodeSize);
                        return dir + " LOGIN mac=" + Util.Mac(mac) + " nick=\"" + nick + "\" game=" + game;
                    }
                case Op.Connect:
                    if (outbound)
                    {
                        if (count < ConnectBroadcastSize) return dir + " CONNECT (truncated " + count + "B)";
                        string nick = Util.FixedUtf8ToString(buf, offset + 1, NicknameSize);
                        byte[] mac = new byte[6];
                        Buffer.BlockCopy(buf, offset + 1 + NicknameSize, mac, 0, 6);
                        byte[] ip = new byte[4];
                        Buffer.BlockCopy(buf, offset + 1 + NicknameSize + MacSize, ip, 0, 4);
                        return dir + " CONNECT nick=\"" + nick + "\" mac=" + Util.Mac(mac) + " ip=" + Util.Ip(ip);
                    }
                    else
                    {
                        if (count < ConnectRequestSize) return dir + " CONNECT (truncated " + count + "B)";
                        string group = Util.FixedUtf8ToString(buf, offset + 1, GroupNameSize);
                        return dir + " CONNECT group=\"" + group + "\"";
                    }
                case Op.Disconnect:
                    if (outbound)
                    {
                        if (count < DisconnectBroadcastSize) return dir + " DISCONNECT (truncated)";
                        byte[] ip = new byte[4];
                        Buffer.BlockCopy(buf, offset + 1, ip, 0, 4);
                        return dir + " DISCONNECT ip=" + Util.Ip(ip);
                    }
                    return dir + " DISCONNECT";
                case Op.Scan:
                    if (outbound)
                    {
                        if (count < ScanResultSize) return dir + " SCAN (truncated)";
                        string group = Util.FixedUtf8ToString(buf, offset + 1, GroupNameSize);
                        byte[] mac = new byte[6];
                        Buffer.BlockCopy(buf, offset + 1 + GroupNameSize, mac, 0, 6);
                        return dir + " SCAN group=\"" + group + "\" host=" + Util.Mac(mac);
                    }
                    return dir + " SCAN";
                case Op.ScanComplete:
                    return dir + " SCAN_COMPLETE";
                case Op.ConnectBssid:
                    {
                        if (count < 7) return dir + " CONNECT_BSSID (truncated)";
                        byte[] mac = new byte[6];
                        Buffer.BlockCopy(buf, offset + 1, mac, 0, 6);
                        return dir + " CONNECT_BSSID " + Util.Mac(mac);
                    }
                case Op.Chat:
                    if (outbound)
                    {
                        if (count < ChatBroadcastSize) return dir + " CHAT (truncated " + count + "B)";
                        string msg = Util.FixedUtf8ToString(buf, offset + 1, MessageSize);
                        string nick = Util.FixedUtf8ToString(buf, offset + 1 + MessageSize, NicknameSize);
                        return dir + " CHAT \"" + msg + "\" from \"" + nick + "\"";
                    }
                    else
                    {
                        if (count < ChatRequestSize) return dir + " CHAT (truncated " + count + "B)";
                        string msg = Util.FixedUtf8ToString(buf, offset + 1, MessageSize);
                        return dir + " CHAT \"" + msg + "\"";
                    }
                default:
                    return dir + " UNKNOWN op=0x" + op.ToString("X2", CultureInfo.InvariantCulture) + " len=" + count;
            }
        }

        public static string OpcodeName(byte op)
        {
            switch (op)
            {
                case Op.Ping: return "PING";
                case Op.Login: return "LOGIN";
                case Op.Connect: return "CONNECT";
                case Op.Disconnect: return "DISCONNECT";
                case Op.Scan: return "SCAN";
                case Op.ScanComplete: return "SCAN_COMPLETE";
                case Op.ConnectBssid: return "CONNECT_BSSID";
                case Op.Chat: return "CHAT";
                default: return "0x" + op.ToString("X2", CultureInfo.InvariantCulture);
            }
        }

        public static string NicknameToHex(string nickname)
        {
            byte[] b = Util.FixedUtf8(nickname, NicknameSize);
            return Util.Hex(b, 0, NicknameSize);
        }

        public static byte[] BuildChat(string message, string senderNickname)
        {
            return ChatBroadcast(message, Util.FixedUtf8(senderNickname, NicknameSize));
        }

        public static string SafeNickname(string raw)
        {
            return Util.Clean(raw, 127);
        }

        public static string SafeGroup(string raw)
        {
            return Util.Clean(raw, GroupNameSize);
        }

        public static Encoding Utf8 { get { return Encoding.UTF8; } }
    }
}