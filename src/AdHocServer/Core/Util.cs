using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace Dpt.AdHoc.Core
{
    public static class Util
    {
        public static string Mac(byte[] mac)
        {
            if (mac == null || mac.Length < 6) return "00:00:00:00:00:00";
            StringBuilder sb = new StringBuilder(17);
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(mac[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string Ip(byte[] ip4)
        {
            if (ip4 == null || ip4.Length < 4) return "0.0.0.0";
            return ip4[0].ToString(CultureInfo.InvariantCulture) + "." + ip4[1].ToString(CultureInfo.InvariantCulture) + "." +
                   ip4[2].ToString(CultureInfo.InvariantCulture) + "." + ip4[3].ToString(CultureInfo.InvariantCulture);
        }

        public static string Ip(IPAddress address)
        {
            if (address == null) return "0.0.0.0";
            byte[] b = address.GetAddressBytes();
            if (b.Length == 16)
            {
                // unwrap ::ffff:a.b.c.d
                bool mapped = true;
                for (int i = 0; i < 10; i++) if (b[i] != 0) { mapped = false; break; }
                if (mapped && b[10] == 0xff && b[11] == 0xff)
                {
                    byte[] v4 = new byte[4];
                    Array.Copy(b, 12, v4, 0, 4);
                    return Ip(v4);
                }
            }
            return address.ToString();
        }

        public static string Hex(byte[] data)
        {
            if (data == null) return "";
            return Hex(data, 0, data.Length);
        }

        public static string Hex(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return "";
            StringBuilder sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>One line hex + ascii, truncated with a marker.</summary>
        public static string HexPreview(byte[] data, int offset, int count, int maxBytes)
        {
            if (data == null || count <= 0) return "";
            int n = Math.Min(count, maxBytes);
            StringBuilder sb = new StringBuilder(n * 4 + 8);
            sb.Append(Hex(data, offset, n));
            if (n < count) sb.Append(" ...(+").Append((count - n).ToString(CultureInfo.InvariantCulture)).Append("B)");
            sb.Append("  |");
            for (int i = 0; i < n; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.Append('|');
            return sb.ToString();
        }

        public static string Clean(string s, int maxLen)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(Math.Min(s.Length, maxLen));
            for (int i = 0; i < s.Length && sb.Length < maxLen; i++)
            {
                char c = s[i];
                if (c == '\0') break;
                if (c < 32 && c != '\t') { sb.Append(' '); continue; }
                if (c == 127) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public static bool TryParseMac(string text, out byte[] mac)
        {
            mac = null;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim().Replace("-", ":").Replace(".", ":").Replace(" ", "");
            if (s.Length == 12)
            {
                // AABBCCDDEEFF
                byte[] tmp = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    if (!byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tmp[i])) return false;
                }
                mac = tmp;
                return true;
            }
            string[] parts = s.Split(':');
            if (parts.Length != 6) return false;
            byte[] res = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out res[i])) return false;
            }
            mac = res;
            return true;
        }

        public static bool IsAlnumAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Group name check that mirrors PPSSPP's adhoc server: an empty name is legal
        /// (sceNetAdhocctlCreate(NULL) sends eight zero bytes - Phantasy Star Portable 2
        /// does exactly that), everything else must be A-Z a-z 0-9.
        /// </summary>
        public static bool IsValidGroupName(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            return IsAlnumAscii(s);
        }

        /// <summary>How a group is shown in boards and the status feed (empty = no named room).</summary>
        public static string GroupDisplayName(string name)
        {
            return string.IsNullOrEmpty(name) ? "Groupless" : name;
        }

        public static bool IsAllZero(byte[] data)
        {
            if (data == null) return true;
            for (int i = 0; i < data.Length; i++) if (data[i] != 0) return false;
            return true;
        }

        public static bool IsAllOnes(byte[] data)
        {
            if (data == null) return true;
            for (int i = 0; i < data.Length; i++) if (data[i] != 0xFF) return false;
            return true;
        }

        public static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = -1;
            do { v /= 1024.0; u++; } while (v >= 1024.0 && u < units.Length - 1);
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }

        public static string HumanDuration(TimeSpan span)
        {
            if (span.TotalSeconds < 1) return "0s";
            if (span.TotalMinutes < 1) return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
            if (span.TotalHours < 1)
                return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " +
                       span.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            if (span.TotalDays < 1)
                return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " +
                       span.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            return ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " +
                   span.Hours.ToString(CultureInfo.InvariantCulture) + "h";
        }

        /// <summary>Copy a string into a fixed size UTF-8 buffer (zero padded).</summary>
        public static byte[] FixedUtf8(string text, int size)
        {
            byte[] buf = new byte[size];
            if (string.IsNullOrEmpty(text)) return buf;
            byte[] src = Encoding.UTF8.GetBytes(text);
            int n = Math.Min(src.Length, size);
            Array.Copy(src, 0, buf, 0, n);
            return buf;
        }

        /// <summary>Decode a fixed size UTF-8 buffer, stopping at the first NUL.</summary>
        public static string FixedUtf8ToString(byte[] buf, int offset, int size)
        {
            if (buf == null) return "";
            int len = 0;
            while (len < size && offset + len < buf.Length && buf[offset + len] != 0) len++;
            if (len <= 0) return "";
            return Clean(Encoding.UTF8.GetString(buf, offset, len), size);
        }

        public static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}