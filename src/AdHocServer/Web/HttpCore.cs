using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Dpt.AdHoc.Web
{
    /// <summary>
    /// Minimal HTTP/1.1 request. We parse it ourselves because HttpListener needs
    /// an URL ACL (admin rights) on Windows and behaves differently on XP.
    /// </summary>
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public string Target = "/";
        public string Path = "/";
        public string Query = "";
        public string HttpVersion = "HTTP/1.1";
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string RemoteIp = "0.0.0.0";
        public int RemotePort;
        public bool KeepAlive;

        private Dictionary<string, string> _query;

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string QueryString(string name)
        {
            if (_query == null) _query = ParseQuery(Query);
            string v;
            return _query.TryGetValue(name, out v) ? v : null;
        }

        public string BodyText()
        {
            if (Body == null || Body.Length == 0) return "";
            return Encoding.UTF8.GetString(Body);
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return map;
            string[] parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                int eq = parts[i].IndexOf('=');
                string key = eq < 0 ? parts[i] : parts[i].Substring(0, eq);
                string val = eq < 0 ? "" : parts[i].Substring(eq + 1);
                try
                {
                    key = Uri.UnescapeDataString(key.Replace('+', ' '));
                    val = Uri.UnescapeDataString(val.Replace('+', ' '));
                }
                catch { }
                map[key] = val;
            }
            return map;
        }
    }

    /// <summary>Minimal HTTP response holder.</summary>
    public sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public void Set(string name, string value)
        {
            if (value == null) Headers.Remove(name);
            else Headers[name] = value;
        }

        public static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                case 414: return "URI Too Long";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status " + status.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    internal sealed class HttpProtocolException : Exception
    {
        public readonly int Status;
        public HttpProtocolException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Buffered socket reader/writer for one TCP connection.</summary>
    internal sealed class HttpConnection : IDisposable
    {
        public const int MaxRequestLine = 8192;
        public const int MaxHeaders = 65536;
        public const int MaxBody = 2 * 1024 * 1024;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly byte[] _buf = new byte[8192];
        private int _pos;
        private int _len;
        private bool _disposed;

        public string RemoteIp = "0.0.0.0";
        public int RemotePort;

        public HttpConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public bool Fill()
        {
            if (_pos < _len) return true;
            _pos = 0;
            _len = 0;
            int n = _stream.Read(_buf, 0, _buf.Length);
            if (n <= 0) return false;
            _len = n;
            return true;
        }

        public int ReadByte()
        {
            if (!Fill()) return -1;
            return _buf[_pos++];
        }

        public string ReadLine(int maxLen)
        {
            StringBuilder sb = new StringBuilder(64);
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
                if (sb.Length > maxLen) throw new HttpProtocolException(414, "line too long");
            }
        }

        public byte[] ReadExact(int count)
        {
            byte[] outBuf = new byte[count];
            int off = 0;
            while (off < count)
            {
                if (_pos >= _len)
                {
                    if (!Fill()) throw new HttpProtocolException(400, "unexpected end of request body");
                }
                int take = Math.Min(count - off, _len - _pos);
                Buffer.BlockCopy(_buf, _pos, outBuf, off, take);
                _pos += take;
                off += take;
            }
            return outBuf;
        }

        /// <summary>Reads a chunked body (browsers only use it for streaming uploads).</summary>
        public byte[] ReadChunked()
        {
            MemoryStream ms = new MemoryStream();
            while (true)
            {
                string sizeLine = ReadLine(128);
                if (sizeLine == null) throw new HttpProtocolException(400, "chunked: eof");
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                int size;
                if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
                    throw new HttpProtocolException(400, "chunked: bad size");
                if (size == 0)
                {
                    // trailers
                    while (true)
                    {
                        string t = ReadLine(1024);
                        if (string.IsNullOrEmpty(t)) break;
                    }
                    break;
                }
                if (ms.Length + size > MaxBody) throw new HttpProtocolException(413, "body too large");
                ms.Write(ReadExact(size), 0, size);
                ReadLine(8); // CRLF
            }
            return ms.ToArray();
        }

        public bool TryReadRequest(out HttpRequest request, out int errorStatus)
        {
            request = null;
            errorStatus = 0;

            string line;
            int skip = 0;
            do
            {
                line = ReadLine(MaxRequestLine);
                if (line == null) return false;         // clean close
                if (line.Length > 0) break;
            } while (++skip < 4);

            if (line == null) return false;

            string[] parts = line.Split(' ');
            if (parts.Length < 3) { errorStatus = 400; return false; }

            HttpRequest req = new HttpRequest();
            req.Method = parts[0].ToUpperInvariant();
            req.Target = parts[1];
            req.HttpVersion = parts[2];
            req.RemoteIp = RemoteIp;
            req.RemotePort = RemotePort;

            int q = req.Target.IndexOf('?');
            string rawPath = q < 0 ? req.Target : req.Target.Substring(0, q);
            req.Query = q < 0 ? "" : req.Target.Substring(q + 1);
            try { req.Path = Uri.UnescapeDataString(rawPath); }
            catch { req.Path = rawPath; }
            if (req.Path.Length == 0) req.Path = "/";

            int headerBytes = 0;
            while (true)
            {
                string header = ReadLine(8192);
                if (header == null) { errorStatus = 400; return false; }
                if (header.Length == 0) break;
                headerBytes += header.Length + 2;
                if (headerBytes > MaxHeaders) { errorStatus = 431; return false; }
                int colon = header.IndexOf(':');
                if (colon <= 0) continue;
                string name = header.Substring(0, colon).Trim();
                string value = header.Substring(colon + 1).Trim();
                string existing;
                if (req.Headers.TryGetValue(name, out existing)) req.Headers[name] = existing + ", " + value;
                else req.Headers[name] = value;
            }

            string connection = req.Header("Connection") ?? "";
            bool http11 = req.HttpVersion.IndexOf("1.1", StringComparison.Ordinal) >= 0;
            req.KeepAlive = http11
                ? connection.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0
                : connection.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;

            string expect = req.Header("Expect") ?? "";
            if (expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                _stream.Write(cont, 0, cont.Length);
                _stream.Flush();
            }

            string te = req.Header("Transfer-Encoding") ?? "";
            if (te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                req.Body = ReadChunked();
            }
            else
            {
                string cl = req.Header("Content-Length");
                if (!string.IsNullOrEmpty(cl))
                {
                    long len;
                    if (!long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out len) || len < 0)
                    { errorStatus = 400; return false; }
                    if (len > MaxBody) { errorStatus = 413; return false; }
                    if (len > 0) req.Body = ReadExact((int)len);
                }
            }

            request = req;
            return true;
        }

        public void WriteResponse(HttpResponse response, bool headOnly, bool keepAlive)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(HttpResponse.Reason(response.Status)).Append("\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Server: DPT.PSP-AdHoc\r\n");
            if (!string.IsNullOrEmpty(response.ContentType))
                sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");

            bool noBodyStatus = response.Status == 204 || response.Status == 304;
            foreach (KeyValuePair<string, string> kv in response.Headers)
            {
                if (string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "Date", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "Server", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            }

            if (!noBodyStatus)
                sb.Append("Content-Length: ").Append((response.Body == null ? 0 : response.Body.Length).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            sb.Append("\r\n");

            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            _stream.Write(head, 0, head.Length);
            if (!headOnly && !noBodyStatus && response.Body != null && response.Body.Length > 0)
                _stream.Write(response.Body, 0, response.Body.Length);
            _stream.Flush();
        }

        public void WriteSimple(int status, string text, bool close)
        {
            HttpResponse resp = new HttpResponse();
            resp.Status = status;
            resp.ContentType = "text/plain; charset=utf-8";
            resp.Body = Encoding.UTF8.GetBytes(text ?? "");
            resp.Set("Connection", close ? "close" : "keep-alive");
            WriteResponse(resp, false, !close);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream.Close(); } catch { }
            try { _client.Close(); } catch { }
        }
    }
}