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
    public enum WebRole
    {
        Public = 0,
        Admin = 1
    }

    /// <summary>Result of checking the admin request signature.</summary>
    public sealed class AuthContext
    {
        public bool Ok;
        public bool Encrypted;
        public bool Blocked;
        public string Error = "";
    }

    /// <summary>
    /// Embedded HTTP server built directly on TcpListener.
    ///
    /// Two instances live side by side and share the same ServerState:
    ///   * Public  - read-only board, redacted data, meant to be exposed through a reverse proxy
    ///   * Admin   - full board (logs, kick, chat, catalog editing), signed + encrypted API
    ///
    /// Reverse proxy aware: trusts X-Forwarded-For / X-Real-IP only from private/loopback peers,
    /// strips X-Forwarded-Prefix and an optional configured base path, rate limits per real client.
    /// </summary>
    public sealed class WebServer
    {
        private sealed class RateWindow
        {
            public DateTime StartUtc;
            public int Count;
        }

        private sealed class FailWindow
        {
            public DateTime StartUtc;
            public int Count;
            public DateTime BlockedUntilUtc;
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly WebRole _role;
        private readonly byte[] _key;
        private readonly string _basePath;
        private readonly NonceCache _nonces = new NonceCache(TimeSpan.FromMinutes(10), 20000);

        private readonly object _rateLock = new object();
        private readonly Dictionary<string, RateWindow> _rate = new Dictionary<string, RateWindow>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FailWindow> _authFails = new Dictionary<string, FailWindow>(StringComparer.OrdinalIgnoreCase);

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

        public WebServer(ServerApp app, WebRole role)
        {
            _app = app;
            _cfg = app.Cfg;
            _role = role;
            _root = ResolveWebRoot(app.Cfg);
            _basePath = role == WebRole.Admin ? app.Cfg.AdminBasePath : app.Cfg.PublicBasePath;
            _bind = role == WebRole.Admin ? app.Cfg.AdminBind : app.Cfg.PublicBind;
            if (string.IsNullOrEmpty(_bind)) _bind = "0.0.0.0";

            if (role == WebRole.Admin)
            {
                try
                {
                    _key = ApiCrypto.DeriveKey(app.Cfg.AdminToken, app.Cfg.AdminSalt, app.Cfg.AdminKdfIterations);
                }
                catch (Exception ex)
                {
                    Log.Error("WEB", "cannot derive admin key: " + ex.Message);
                    _key = null;
                }
            }
        }

        public ServerApp App { get { return _app; } }
        public Config Config { get { return _cfg; } }
        public WebRole Role { get { return _role; } }
        public bool IsAdmin { get { return _role == WebRole.Admin; } }
        public string RoleName { get { return _role == WebRole.Admin ? "admin" : "public"; } }
        public int Port { get { return _port; } }
        public string BindAddress { get { return _bind; } }
        public string BasePath { get { return _basePath; } }
        public string Root { get { return _root; } }
        public byte[] AdminKey { get { return _key; } }
        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public int Inflight { get { return Interlocked.CompareExchange(ref _inflight, 0, 0); } }

        public string BaseUrl
        {
            get
            {
                string host = _bind == "0.0.0.0" ? "localhost" : _bind;
                return "http://" + host + ":" + _port.ToString(CultureInfo.InvariantCulture) + _basePath + "/";
            }
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

            int primary = _role == WebRole.Admin ? _cfg.AdminPort : _cfg.PublicPort;
            int[] fallbacks = _role == WebRole.Admin ? _cfg.AdminPortFallbacks : _cfg.PublicPortFallbacks;

            List<int> ports = new List<int>();
            ports.Add(primary);
            if (fallbacks != null)
                for (int i = 0; i < fallbacks.Length; i++)
                {
                    int p = fallbacks[i];
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
                    _acceptThread.Name = "web-" + RoleName + "-accept";
                    _acceptThread.Start();

                    Log.Info("WEB", RoleName + " ui listening on " + _bind + ":" + port.ToString(CultureInfo.InvariantCulture) +
                                    (_basePath.Length > 0 ? " base=" + _basePath : "") + " (root: " + _root + ")");
                    if (port != primary)
                        Log.Warn("WEB", RoleName + " port " + primary + " is busy, using " + port);
                    if (_role == WebRole.Admin && _cfg.AdminPrivateOnly)
                        Log.Info("WEB", "admin ui accepts private/LAN addresses only (web.adminPrivateOnly=true)");

                    string[] lan = LocalAddresses();
                    for (int a = 0; a < lan.Length; a++)
                        Log.Info("WEB", RoleName + " url: http://" + lan[a] + ":" + port.ToString(CultureInfo.InvariantCulture) + _basePath + "/");
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    Log.Warn("WEB", RoleName + " cannot bind " + _bind + ":" + port.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                }
            }

            Log.Error("WEB", RoleName + " ui failed to start on every candidate port. Last error: " +
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
            Log.Warn("WEB", "restarting " + RoleName + " listener");
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
            if (was) Log.Info("WEB", RoleName + " ui stopped");
        }

        // ------------------------------------------------------- proxy helpers

        /// <summary>Real client address. Honours X-Forwarded-For / X-Real-IP from trusted peers only.</summary>
        public string EffectiveClientIp(HttpRequest req)
        {
            string peer = req.RemoteIp ?? "0.0.0.0";
            if (!_cfg.TrustProxyHeaders) return peer;
            if (!IsTrustedProxy(peer)) return peer;

            string chain = req.Header("X-Forwarded-For");
            if (string.IsNullOrEmpty(chain)) chain = req.Header("X-Real-IP");
            if (string.IsNullOrEmpty(chain)) return peer;

            string[] parts = chain.Split(',');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string candidate = StripPort(parts[i].Trim());
                if (candidate.Length == 0) continue;
                if (!IsTrustedProxy(candidate)) return candidate;
            }
            return peer;
        }

        private static string StripPort(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // "1.2.3.4:5678" but keep IPv6 like "::1" or "[::1]:80"
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int end = value.IndexOf(']');
                return end > 1 ? value.Substring(1, end - 1) : value;
            }
            int first = value.IndexOf(':');
            int last = value.LastIndexOf(':');
            if (first > 0 && first == last) return value.Substring(0, first);
            return value;
        }

        private bool IsTrustedProxy(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (ip == "::1" || ip == "127.0.0.1") return true;
            if (IsPrivateIpv4(ip)) return true;
            List<Cidr> list = ParseCidrs(_cfg.TrustedProxies);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Contains(ip)) return true;
            return false;
        }

        public static bool IsPrivateIpv4(string ip)
        {
            IPAddress addr;
            if (!IPAddress.TryParse(ip, out addr)) return false;
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return ip == "::1";
            if (b[0] == 10) return true;
            if (b[0] == 127) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        private sealed class Cidr
        {
            public uint Network;
            public int Bits;

            public bool Contains(string ip)
            {
                IPAddress addr;
                if (!IPAddress.TryParse(ip, out addr)) return false;
                byte[] b = addr.GetAddressBytes();
                if (b.Length != 4) return false;
                uint value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                if (Bits == 0) return true;
                uint mask = 0xFFFFFFFFu << (32 - Bits);
                return (value & mask) == (Network & mask);
            }
        }

        private static List<Cidr> ParseCidrs(string text)
        {
            List<Cidr> list = new List<Cidr>();
            if (string.IsNullOrEmpty(text)) return list;
            string[] parts = text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string item = parts[i].Trim();
                if (item.Length == 0) continue;
                int slash = item.IndexOf('/');
                string ipPart = slash < 0 ? item : item.Substring(0, slash);
                int bits = 32;
                if (slash > 0) int.TryParse(item.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out bits);
                IPAddress addr;
                if (!IPAddress.TryParse(ipPart, out addr)) continue;
                byte[] b = addr.GetAddressBytes();
                if (b.Length != 4) continue;
                list.Add(new Cidr
                {
                    Network = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3],
                    Bits = Config.Clamp(bits, 0, 32)
                });
            }
            return list;
        }

        /// <summary>Strips the configured base path and X-Forwarded-Prefix from the request path.</summary>
        public string MapPath(HttpRequest req)
        {
            string path = req.Path ?? "/";
            string prefix = null;

            if (!string.IsNullOrEmpty(_basePath) &&
                (path.Equals(_basePath, StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(_basePath + "/", StringComparison.OrdinalIgnoreCase)))
            {
                prefix = _basePath;
            }
            else
            {
                string forwarded = req.Header("X-Forwarded-Prefix");
                if (!string.IsNullOrEmpty(forwarded))
                {
                    string f = Config.NormalizeBasePath(forwarded);
                    if (!string.IsNullOrEmpty(f) &&
                        (path.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))
                        prefix = f;
                }
            }

            if (prefix != null)
            {
                path = path.Substring(prefix.Length);
                if (path.Length == 0) path = "/";
            }
            return path.Length == 0 ? "/" : path;
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
                    Log.Warn("WEB", RoleName + " accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Error("WEB", RoleName + " unexpected accept error: " + ex.Message);
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
            Log.Debug("WEB", RoleName + " accept loop finished");
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

                if (_role == WebRole.Admin && _cfg.AdminPrivateOnly && !IsPrivateIpv4(conn.RemoteIp) && conn.RemoteIp != "::1")
                {
                    Log.Warn("WEB", "admin request from non private address " + conn.RemoteIp + " rejected");
                    try { conn.WriteSimple(403, "Admin board is LAN only (web.adminPrivateOnly).", true); } catch { }
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
                    catch (IOException) { break; }
                    catch (SocketException) { break; }

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

                    string clientIp = EffectiveClientIp(req);
                    if (!RateAllowed(clientIp))
                    {
                        HttpResponse busy = new HttpResponse();
                        busy.Status = 429;
                        busy.ContentType = "application/json; charset=utf-8";
                        busy.Body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"rate limit\"}");
                        busy.Set("Retry-After", "5");
                        ApplySecurityHeaders(busy);
                        conn.WriteResponse(busy, false, false);
                        break;
                    }

                    HttpResponse resp = new HttpResponse();
                    try
                    {
                        Route(req, resp, clientIp);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _errors);
                        Log.Error("WEB", RoleName + " request " + req.Method + " " + req.Target + " failed: " + ex);
                        resp = new HttpResponse();
                        resp.Status = 500;
                        resp.ContentType = "application/json; charset=utf-8";
                        resp.Body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"internal server error\"}");
                        ApplySecurityHeaders(resp);
                    }

                    bool keep = req.KeepAlive && served < 100;
                    conn.WriteResponse(resp, string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase), keep);
                    if (!keep) break;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
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
                if (_rate.Count > 4000 && w.Count == 1) PruneRateLocked(now);
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

        private bool AuthBlocked(string ip)
        {
            lock (_rateLock)
            {
                FailWindow w;
                if (!_authFails.TryGetValue(ip, out w)) return false;
                if (w.BlockedUntilUtc > DateTime.UtcNow) return true;
                return false;
            }
        }

        private void RegisterAuthFailure(string ip)
        {
            DateTime now = DateTime.UtcNow;
            lock (_rateLock)
            {
                FailWindow w;
                if (!_authFails.TryGetValue(ip, out w) || (now - w.StartUtc).TotalSeconds > 300)
                {
                    w = new FailWindow();
                    w.StartUtc = now;
                    w.Count = 0;
                    _authFails[ip] = w;
                }
                w.Count++;
                if (w.Count >= 20)
                {
                    w.BlockedUntilUtc = now.AddMinutes(5);
                    w.Count = 0;
                    w.StartUtc = now;
                    Log.Warn("WEB", "admin auth failures from " + ip + ": blocked for 5 minutes");
                }
            }
        }

        // ---------------------------------------------------------------- auth

        public AuthContext Authenticate(HttpRequest req)
        {
            AuthContext ctx = new AuthContext();
            if (_role != WebRole.Admin) { ctx.Ok = true; return ctx; }

            string ip = EffectiveClientIp(req);
            if (AuthBlocked(ip))
            {
                ctx.Blocked = true;
                ctx.Error = "too many failed attempts";
                return ctx;
            }
            if (_key == null)
            {
                ctx.Error = "server has no admin key";
                return ctx;
            }

            string ts = req.Header("X-Auth-Ts");
            string nonce = req.Header("X-Auth-Nonce");
            string sig = req.Header("X-Auth-Sig");

            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(sig))
            {
                ctx.Error = "signed request required";
                RegisterAuthFailure(ip);
                return ctx;
            }
            if (!ApiCrypto.IsFreshTimestamp(ts, DateTime.UtcNow))
            {
                ctx.Error = "stale or skewed timestamp";
                RegisterAuthFailure(ip);
                return ctx;
            }
            if (!ApiCrypto.IsValidNonce(nonce))
            {
                ctx.Error = "malformed nonce";
                RegisterAuthFailure(ip);
                return ctx;
            }

            // Canonical target is the *application relative* path+query, so it works both when the
            // reverse proxy strips its prefix and when it forwards it (we strip it too).
            string canonicalTarget = req.Path ?? "/";
            if (!string.IsNullOrEmpty(req.Query)) canonicalTarget += "?" + req.Query;

            string expected = ApiCrypto.Signature(_key, req.Method, canonicalTarget, ts, nonce, req.Body);
            if (!ApiCrypto.FixedEquals(expected, sig))
            {
                ctx.Error = "bad signature";
                RegisterAuthFailure(ip);
                return ctx;
            }
            if (!_nonces.TryUse(nonce))
            {
                ctx.Error = "replayed request";
                RegisterAuthFailure(ip);
                return ctx;
            }

            bool encrypted = string.Equals(req.Header("X-Auth-Enc") ?? "", "1", StringComparison.Ordinal);
            if (encrypted)
            {
                if (!_cfg.AdminEncrypt)
                {
                    ctx.Error = "encrypted bodies are disabled";
                    return ctx;
                }
                byte[] plain = ApiCrypto.Open(_key, req.BodyText());
                if (plain == null)
                {
                    ctx.Error = "cannot decrypt body";
                    RegisterAuthFailure(ip);
                    return ctx;
                }
                req.Body = plain;
            }

            ctx.Ok = true;
            ctx.Encrypted = encrypted;
            return ctx;
        }

        /// <summary>Encrypts a JSON body when the request came in encrypted.</summary>
        public void SealResponse(AuthContext auth, HttpResponse resp)
        {
            if (auth == null || !auth.Ok) return;
            if (!_cfg.AdminEncrypt || _key == null) return;
            if (resp == null || resp.Status != 200) return;
            if (resp.Body == null || resp.Body.Length == 0) return;

            // the body may have been gzipped for the wire - seal the plain payload instead
            string encoding;
            if (resp.Headers.TryGetValue("Content-Encoding", out encoding) &&
                encoding != null && encoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { resp.Body = Gunzip(resp.Body); }
                catch { return; } // never send a half-broken body
            }

            resp.Body = Encoding.ASCII.GetBytes(ApiCrypto.Seal(_key, resp.Body));
            resp.Set("X-Enc-Resp", "1");
            resp.Set("Content-Encoding", null);
            resp.Set("Vary", null);
            resp.ContentType = "text/plain; charset=utf-8";
        }

        // --------------------------------------------------------------- routing

        private void Route(HttpRequest req, HttpResponse resp, string clientIp)
        {
            string path = MapPath(req);
            req.Path = path;

            if (path.Equals("/status.xml", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/data.json", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/status.json", StringComparison.OrdinalIgnoreCase))
            {
                WebApi.Handle(this, req, resp, clientIp);
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                Log.Verbose("WEB", RoleName + " api " + req.Method + " " + path + " from " + clientIp);
                if (!WebApi.Handle(this, req, resp, clientIp))
                {
                    resp.Status = 404;
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.Body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"unknown endpoint\"}");
                    ApplySecurityHeaders(resp);
                }
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
            if (relative == "/" || relative.Length == 0)
                relative = _role == WebRole.Admin ? "/admin.html" : "/index.html";

            // the public board never serves the admin shell
            if (_role != WebRole.Admin &&
                (relative.Equals("/admin.html", StringComparison.OrdinalIgnoreCase) ||
                 relative.Equals("/admin", StringComparison.OrdinalIgnoreCase)))
            {
                SendText(req, resp, 404, "Not found");
                return;
            }

            if (relative.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                string svg = Path.Combine(_root, "assets", "img", "favicon.svg");
                if (File.Exists(svg)) { SendFile(req, resp, svg, "image/svg+xml", true); return; }
            }

            string file = ResolveSafe(relative);
            if (file == null)
            {
                Log.Warn("WEB", "path traversal attempt: " + req.Target);
                SendText(req, resp, 400, "Bad request");
                return;
            }

            if (Directory.Exists(file))
            {
                string index = Path.Combine(file, _role == WebRole.Admin ? "admin.html" : "index.html");
                if (!File.Exists(index)) index = Path.Combine(file, "index.html");
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
                ApplySecurityHeaders(resp);
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
            ApplySecurityHeaders(resp);
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
                    if (NormalizeEtag(candidates[i]) == wanted) return true;
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

        private static string NormalizeEtag(string value)
        {
            if (value == null) return "";
            string v = value.Trim();
            if (v.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }

        // ----------------------------------------------------------- output

        public void ApplySecurityHeaders(HttpResponse resp)
        {
            if (resp == null) return;
            resp.Set("X-Content-Type-Options", "nosniff");
            resp.Set("X-Frame-Options", "DENY");
            resp.Set("Referrer-Policy", "no-referrer");
            resp.Set("Cross-Origin-Resource-Policy", "same-origin");
            resp.Set("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");
            resp.Set("Content-Security-Policy",
                "default-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; " +
                "object-src 'none'; img-src 'self' data:; " +
                "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
                "font-src 'self' https://cdnjs.cloudflare.com data:; " +
                "script-src 'self'; connect-src 'self'");
        }

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
            SendBytes(req, resp, status, data, contentType, gzipAllowed, longCache, etag, null);
        }

        /// <summary>
        /// JSON meant to be read by other programs (the PPSSPP server browser, third-party status
        /// pages): short cache lifetime and CORS allowed, unlike the board's own API.
        /// </summary>
        public void SendPublicJson(HttpRequest req, HttpResponse resp, int status, string json, int maxAgeSec)
        {
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["Cache-Control"] = "public, max-age=" + maxAgeSec.ToString(CultureInfo.InvariantCulture);
            extra["Access-Control-Allow-Origin"] = "*";
            extra["Cross-Origin-Resource-Policy"] = "cross-origin";
            SendBytes(req, resp, status, Encoding.UTF8.GetBytes(json ?? ""),
                      "application/json; charset=utf-8", true, false, null, extra);
        }

        public void SendBytes(HttpRequest req, HttpResponse resp, int status, byte[] data, string contentType,
                              bool gzipAllowed, bool longCache, string etag,
                              Dictionary<string, string> extraHeaders)
        {
            if (data == null) data = new byte[0];
            resp.Status = status;
            resp.ContentType = contentType;
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
            ApplySecurityHeaders(resp);
            if (extraHeaders != null)
                foreach (KeyValuePair<string, string> kv in extraHeaders)
                    resp.Set(kv.Key, kv.Value);
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

        public static byte[] Gunzip(byte[] input)
        {
            using (MemoryStream inputStream = new MemoryStream(input))
            using (GZipStream gz = new GZipStream(inputStream, CompressionMode.Decompress))
            using (MemoryStream outStream = new MemoryStream(input.Length * 3 + 64))
            {
                byte[] buffer = new byte[8192];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0) outStream.Write(buffer, 0, n);
                return outStream.ToArray();
            }
        }

        public string StatusLine()
        {
            return RoleName + " http://" + _bind + ":" + _port.ToString(CultureInfo.InvariantCulture) +
                   (_basePath.Length > 0 ? _basePath : "") +
                   " | requests " + Interlocked.Read(ref _requests).ToString(CultureInfo.InvariantCulture) +
                   " | errors " + Interlocked.Read(ref _errors).ToString(CultureInfo.InvariantCulture) +
                   " | inflight " + Inflight;
        }
    }
}