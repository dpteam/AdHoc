using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc.Web
{
    /// <summary>
    /// Embedded HTTP server built directly on TcpListener.
    ///
    /// Why not HttpListener: on Windows it requires an URL ACL (admin), refuses to bind
    /// to a LAN address without it and behaves differently on XP. A game server must
    /// simply work after double click, so we speak HTTP/1.1 ourselves.
    /// </summary>
    public sealed class WebServer
    {
        private sealed class RateWindow
        {
            public DateTime StartUtc;
            public int Count;
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly object _rateLock = new object();
        private readonly Dictionary<string, RateWindow> _rate = new Dictionary<string, RateWindow>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private string _bind = "0.0.0.0";
        private string _root = "";
        private int _inflight;
        private long _requests;
        private long _errors;

        public event EventHandler Failed;

        public WebServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
            _root = ResolveWebRoot(app.Cfg);
            _bind = string.IsNullOrEmpty(app.Cfg.WebBindAddress) ? "0.0.0.0" : app.Cfg.WebBindAddress.Trim();
        }

        public ServerApp App { get { return _app; } }
        public Config Config { get { return _cfg; } }
        public int Port { get { return _port; } }
        public string BindAddress { get { return _bind; } }
        public string Root { get { return _root; } }
        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public int Inflight { get { return Interlocked.CompareExchange(ref _inflight, 0, 0); } }

        public string BaseUrl
        {
            get { return "http://" + (_bind == "0.0.0.0" ? "localhost" : _bind) + ":" + _port.ToString(CultureInfo.InvariantCulture) + "/"; }
        }

        public bool IsRunning
        {
            get { return _running && _acceptThread != null && _acceptThread.IsAlive; }
        }

        private static string ResolveWebRoot(Config cfg)
        {
            List<string> candidates = new List<string>();
            candidates.Add(cfg.WebRoot);
            try { candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public")); } catch { }
            try { candidates.Add(Path.Combine(Environment.CurrentDirectory, "public")); } catch { }
            try { candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "public")); } catch { }
            try { candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "public")); } catch { }

            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    string full = Path.GetFullPath(candidates[i]);
                    if (Directory.Exists(full)) return full;
                }
                catch { }
            }
            Log.Error("WEB", "public/ directory not found, static UI disabled. Looked in: " +
                            string.Join(", ", candidates.ToArray()));
            return Path.GetFullPath(cfg.WebRoot);
        }

        // ------------------------------------------------------------ lifecycle

        public bool Start()
        {
            if (_running) return true;

            List<int> ports = new List<int>();
            ports.Add(_cfg.WebPort);
            if (_cfg.WebPortFallbacks != null)
                for (int i = 0; i < _cfg.WebPortFallbacks.Length; i++)
                {
                    int p = _cfg.WebPortFallbacks[i];
                    if (p > 0 && p <= 65535 && !ports.Contains(p)) ports.Add(p);
                }

            IPAddress bindAddress = ResolveBindAddress(_bind);

            Exception last = null;
            for (int i = 0; i < ports.Count; i++)
            {
                int port = ports[i];
                try
                {
                    TcpListener listener = new TcpListener(bindAddress, port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start(256);

                    _listener = listener;
                    _port = port;
                    _running = true;

                    _acceptThread = new Thread(AcceptLoop);
                    _acceptThread.IsBackground = true;
                    _acceptThread.Name = "web-accept";
                    _acceptThread.Start();

                    Log.Info("WEB", "web ui listening on " + _bind + ":" + port.ToString(CultureInfo.InvariantCulture) +
                                    " (root: " + _root + ")");
                    if (port != _cfg.WebPort)
                        Log.Warn("WEB", "port " + _cfg.WebPort + " is busy, web ui moved to " + port);
                    string[] lan = LocalAddresses();
                    for (int a = 0; a < lan.Length; a++)
                        Log.Info("WEB", "open http://" + lan[a] + ":" + port.ToString(CultureInfo.InvariantCulture) + "/");
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    Log.Warn("WEB", "cannot bind " + _bind + ":" + port.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                }
            }

            Log.Error("WEB", "web ui failed to start on every candidate port. Last error: " +
                             (last == null ? "unknown" : last.Message));
            return false;
        }

        private static IPAddress ResolveBindAddress(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "*" || text == "+" || text == "0.0.0.0") return IPAddress.Any;
            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
            IPAddress ip;
            if (IPAddress.TryParse(text, out ip)) return ip;
            return IPAddress.Any;
        }

        /// <summary>All usable IPv4 addresses of this machine (for the logs / console).</summary>
        public string[] LocalAddresses()
        {
            List<string> list = new List<string>();
            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                for (int i = 0; i < host.AddressList.Length; i++)
                {
                    IPAddress a = host.AddressList[i];
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    list.Add(s);
                }
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list.ToArray();
        }

        public bool Restart()
        {
            Log.Warn("WEB", "restarting web listener");
            try { Stop(); } catch { }
            Thread.Sleep(300);
            return Start();
        }

        public void Stop()
        {
            bool was = _running;
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(2000); } catch { }
            _acceptThread = null;
            if (was) Log.Info("WEB", "web ui stopped");
        }

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
                    Log.Warn("WEB", "accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Error("WEB", "unexpected accept error: " + ex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }

                if (client == null) continue;

                int now = Interlocked.Increment(ref _inflight);
                if (now > _cfg.WebMaxConcurrent)
                {
                    Interlocked.Decrement(ref _inflight);
                    try
                    {
                        client.Client.Send(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    }
                    catch { }
                    try { client.Close(); } catch { }
                    continue;
                }

                try { ThreadPool.QueueUserWorkItem(ProcessConnection, client); }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _inflight);
                    Log.Warn("WEB", "cannot queue connection: " + ex.Message);
                    try { client.Close(); } catch { }
                }
            }
            Log.Debug("WEB", "accept loop finished");
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

        // ------------------------------------------------------------ connection

        private void ProcessConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            HttpConnection conn = null;
            try
            {
                try { client.NoDelay = true; } catch { }
                try { client.ReceiveTimeout = 15000; } catch { }
                try { client.SendTimeout = 20000; } catch { }

                conn = new HttpConnection(client);
                IPEndPoint ep = null;
                try { ep = client.Client.RemoteEndPoint as IPEndPoint; } catch { }
                if (ep != null)
                {
                    conn.RemoteIp = Util.Ip(ep.Address);
                    conn.RemotePort = ep.Port;
                }

                if (!RateAllowed(conn.RemoteIp))
                {
                    HttpResponse busy = new HttpResponse();
                    busy.Status = 429;
                    busy.ContentType = "application/json; charset=utf-8";
                    busy.Body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"rate limit\"}");
                    busy.Set("Retry-After", "5");
                    conn.WriteResponse(busy, false, false);
                    return;
                }

                int served = 0;
                while (_running && served < 100)
                {
                    HttpRequest req;
                    int errorStatus;
                    bool ok;
                    try
                    {
                        ok = conn.TryReadRequest(out req, out errorStatus);
                    }
                    catch (HttpProtocolException hpe)
                    {
                        try { conn.WriteSimple(hpe.Status, HttpResponse.Reason(hpe.Status), true); } catch { }
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    if (!ok)
                    {
                        if (errorStatus != 0)
                        {
                            try { conn.WriteSimple(errorStatus, HttpResponse.Reason(errorStatus), true); } catch { }
                        }
                        break;
                    }

                    served++;
                    Interlocked.Increment(ref _requests);

                    HttpResponse resp = new HttpResponse();
                    try
                    {
                        Route(req, resp);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _errors);
                        Log.Error("WEB", "request " + req.Method + " " + req.Path + " failed: " + ex);
                        resp = new HttpResponse();
                        resp.Status = 500;
                        resp.ContentType = "application/json; charset=utf-8";
                        resp.Body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"internal server error\"}");
                    }

                    bool keep = req.KeepAlive && served < 100;
                    conn.WriteResponse(resp, string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase), keep);
                    if (!keep) break;
                }
            }
            catch (IOException)
            {
                // client vanished mid-request
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log.Warn("WEB", "connection error: " + ex.Message);
            }
            finally
            {
                if (conn != null) conn.Dispose();
                else { try { client.Close(); } catch { } }
                Interlocked.Decrement(ref _inflight);
            }
        }

        private bool RateAllowed(string ip)
        {
            int limit = _cfg.WebRequestsPerMinute;
            if (limit <= 0) return true;
            DateTime now = DateTime.UtcNow;
            lock (_rateLock)
            {
                RateWindow w;
                if (!_rate.TryGetValue(ip, out w) || (now - w.StartUtc).TotalSeconds >= 60)
                {
                    w = new RateWindow();
                    w.StartUtc = now;
                    w.Count = 0;
                    _rate[ip] = w;
                }
                w.Count++;
                if (_rate.Count > 2000 && w.Count == 1) PruneRateLocked(now);
                return w.Count <= limit;
            }
        }

        private void PruneRateLocked(DateTime now)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, RateWindow> kv in _rate)
                if ((now - kv.Value.StartUtc).TotalSeconds > 180) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _rate.Remove(dead[i]);
        }

        // --------------------------------------------------------------- routing

        private void Route(HttpRequest req, HttpResponse resp)
        {
            string path = req.Path;
            if (string.IsNullOrEmpty(path)) path = "/";

            if (path.Equals("/status.xml", StringComparison.OrdinalIgnoreCase))
            {
                WebApi.Handle(this, req, resp);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!WebApi.Handle(this, req, resp))
                    SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"unknown endpoint\"}");
                return;
            }

            ServeStatic(req, resp, path);
        }

        // --------------------------------------------------------------- static

        private static readonly Dictionary<string, string> MimeTypes = BuildMimeTypes();

        private static Dictionary<string, string> BuildMimeTypes()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            m[".html"] = "text/html; charset=utf-8";
            m[".htm"] = "text/html; charset=utf-8";
            m[".css"] = "text/css; charset=utf-8";
            m[".js"] = "application/javascript; charset=utf-8";
            m[".json"] = "application/json; charset=utf-8";
            m[".svg"] = "image/svg+xml";
            m[".png"] = "image/png";
            m[".jpg"] = "image/jpeg";
            m[".jpeg"] = "image/jpeg";
            m[".gif"] = "image/gif";
            m[".ico"] = "image/x-icon";
            m[".woff"] = "font/woff";
            m[".woff2"] = "font/woff2";
            m[".ttf"] = "font/ttf";
            m[".txt"] = "text/plain; charset=utf-8";
            m[".map"] = "application/json; charset=utf-8";
            m[".xml"] = "application/xml; charset=utf-8";
            m[".webmanifest"] = "application/manifest+json";
            return m;
        }

        public static string MimeFor(string path)
        {
            string ext = Path.GetExtension(path);
            string type;
            return !string.IsNullOrEmpty(ext) && MimeTypes.TryGetValue(ext, out type) ? type : "application/octet-stream";
        }

        private void ServeStatic(HttpRequest req, HttpResponse resp, string urlPath)
        {
            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                SendJson(req, resp, 405, "{\"ok\":false,\"error\":\"method not allowed\"}");
                return;
            }

            string relative = urlPath;
            if (relative == "/" || relative.Length == 0) relative = "/index.html";

            if (relative.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                string svg = Path.Combine(_root, "assets", "img", "favicon.svg");
                if (File.Exists(svg)) { SendFile(req, resp, svg, "image/svg+xml", true); return; }
            }

            string file = ResolveSafe(relative);
            if (file == null)
            {
                Log.Warn("WEB", "path traversal attempt: " + urlPath);
                SendText(req, resp, 400, "Bad request");
                return;
            }

            if (Directory.Exists(file))
            {
                string index = Path.Combine(file, "index.html");
                if (File.Exists(index)) { SendFile(req, resp, index, MimeFor(index), false); return; }
                SendText(req, resp, 403, "Forbidden");
                return;
            }

            if (!File.Exists(file))
            {
                SendText(req, resp, 404, "Not found");
                return;
            }

            bool longCache = relative.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);
            SendFile(req, resp, file, MimeFor(file), longCache);
        }

        private string ResolveSafe(string urlPath)
        {
            try
            {
                string clean = urlPath.Replace('\\', '/');
                if (clean.IndexOf('\0') >= 0) return null;
                if (clean.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
                clean = clean.TrimStart('/');
                string combined = Path.Combine(_root, clean.Replace('/', Path.DirectorySeparatorChar));
                string full = Path.GetFullPath(combined);
                string rootFull = Path.GetFullPath(_root);
                if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    rootFull += Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch
            {
                return null;
            }
        }

        public void SendFile(HttpRequest req, HttpResponse resp, string file, string contentType, bool longCache)
        {
            FileInfo fi = new FileInfo(file);
            if (!fi.Exists) { SendText(req, resp, 404, "Not found"); return; }

            DateTime lastWrite = fi.LastWriteTimeUtc;
            string etag = "\"" + fi.Length.ToString(CultureInfo.InvariantCulture) + "-" +
                          lastWrite.Ticks.ToString(CultureInfo.InvariantCulture) + "\"";

            if (CheckNotModified(req, etag, lastWrite))
            {
                resp.Status = 304;
                resp.ContentType = null;
                resp.Set("ETag", etag);
                resp.Set("Cache-Control", longCache ? "public, max-age=300" : "no-cache");
                return;
            }

            byte[] data = File.ReadAllBytes(file);
            bool gzip = _cfg.WebCompress && data.Length >= 1024 &&
                        (req.Header("Accept-Encoding") ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;

            resp.Status = 200;
            resp.ContentType = contentType;
            resp.Set("ETag", etag);
            resp.Set("Last-Modified", lastWrite.ToString("R", CultureInfo.InvariantCulture));
            resp.Set("Cache-Control", longCache ? "public, max-age=300" : "no-cache, must-revalidate");
            resp.Set("X-Content-Type-Options", "nosniff");
            resp.Set("Referrer-Policy", "no-referrer");

            if (gzip)
            {
                resp.Body = Gzip(data);
                resp.Set("Content-Encoding", "gzip");
                resp.Set("Vary", "Accept-Encoding");
            }
            else
            {
                resp.Body = data;
            }
        }

        private static bool CheckNotModified(HttpRequest req, string etag, DateTime lastWriteUtc)
        {
            string inm = req.Header("If-None-Match");
            if (!string.IsNullOrEmpty(inm))
            {
                if (inm.Trim() == "*") return true;
                string[] candidates = inm.Split(',');
                string wanted = NormalizeEtag(etag);
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (NormalizeEtag(candidates[i]) == wanted) return true;
                }
            }
            string ims = req.Header("If-Modified-Since");
            if (!string.IsNullOrEmpty(ims))
            {
                DateTime since;
                if (DateTime.TryParse(ims, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out since))
                {
                    if (lastWriteUtc.AddSeconds(-1) <= since) return true;
                }
            }
            return false;
        }

        /// <summary>Weak compare: ignore W/ prefix, quotes and whitespace, so any client works.</summary>
        private static string NormalizeEtag(string value)
        {
            if (value == null) return "";
            string v = value.Trim();
            if (v.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }

        // ----------------------------------------------------------- output

        public void SendText(HttpRequest req, HttpResponse resp, int status, string text)
        {
            SendBytes(req, resp, status, Encoding.UTF8.GetBytes(text ?? ""), "text/plain; charset=utf-8", false, false, null);
        }

        public void SendJson(HttpRequest req, HttpResponse resp, int status, string json)
        {
            SendBytes(req, resp, status, Encoding.UTF8.GetBytes(json ?? ""), "application/json; charset=utf-8", true, false, null);
        }

        public void SendXml(HttpRequest req, HttpResponse resp, int status, string xml)
        {
            SendBytes(req, resp, status, Encoding.UTF8.GetBytes(xml ?? ""), "application/xml; charset=utf-8", true, false, null);
        }

        public void SendBytes(HttpRequest req, HttpResponse resp, int status, byte[] data, string contentType,
                              bool gzipAllowed, bool longCache, string etag)
        {
            if (data == null) data = new byte[0];
            resp.Status = status;
            resp.ContentType = contentType;
            resp.Set("X-Content-Type-Options", "nosniff");
            resp.Set("Cache-Control", longCache ? "public, max-age=300" : "no-store, no-cache, must-revalidate");
            if (!string.IsNullOrEmpty(etag)) resp.Set("ETag", etag);

            bool gzip = gzipAllowed && _cfg.WebCompress && data.Length >= 1024 &&
                        (req.Header("Accept-Encoding") ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
            if (gzip)
            {
                resp.Body = Gzip(data);
                resp.Set("Content-Encoding", "gzip");
                resp.Set("Vary", "Accept-Encoding");
            }
            else
            {
                resp.Body = data;
            }
        }

        public static byte[] Gzip(byte[] input)
        {
            using (MemoryStream outStream = new MemoryStream(input.Length / 2 + 64))
            {
                using (GZipStream gz = new GZipStream(outStream, CompressionMode.Compress, true))
                {
                    gz.Write(input, 0, input.Length);
                }
                return outStream.ToArray();
            }
        }

        public string StatusLine()
        {
            return "http " + _bind + ":" + _port.ToString(CultureInfo.InvariantCulture) + " | requests " +
                   Interlocked.Read(ref _requests).ToString(CultureInfo.InvariantCulture) + " | errors " +
                   Interlocked.Read(ref _errors).ToString(CultureInfo.InvariantCulture) + " | inflight " + Inflight;
        }
    }
}