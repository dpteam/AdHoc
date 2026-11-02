using System;
using System.Globalization;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc
{
    /// <summary>
    /// Watchdog: keeps the adhoc listener and both web listeners alive.
    /// A dead accept loop never leaves the process running as a zombie that silently
    /// eats connections; restarts use exponential backoff.
    /// </summary>
    public sealed class AppSupervisor
    {
        private sealed class Watched
        {
            public string Name;
            public Func<bool> IsRunning;
            public Func<bool> Restart;
            public DateTime BackoffUntil = DateTime.MinValue;
            public bool Fault;
            public int Restarts;
        }

        private readonly ServerApp _app;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private readonly Watched _adhoc;
        private readonly Watched _publicWeb;
        private readonly Watched _adminWeb;

        public AppSupervisor(ServerApp app)
        {
            _app = app;
            _adhoc = new Watched
            {
                Name = "adhoc",
                IsRunning = delegate { return app.Adhoc != null && app.Adhoc.IsRunning; },
                Restart = delegate { return app.Adhoc != null && app.Adhoc.Restart(); }
            };
            _publicWeb = new Watched
            {
                Name = "web-public",
                IsRunning = delegate { return app.WebPublic != null && app.WebPublic.IsRunning; },
                Restart = delegate { return app.WebPublic != null && app.WebPublic.Restart(); }
            };
            _adminWeb = new Watched
            {
                Name = "web-admin",
                IsRunning = delegate { return app.WebAdmin != null && app.WebAdmin.IsRunning; },
                Restart = delegate { return app.WebAdmin != null && app.WebAdmin.Restart(); }
            };
        }

        public int AdhocRestarts { get { return _adhoc.Restarts; } }
        public int PublicRestarts { get { return _publicWeb.Restarts; } }
        public int AdminRestarts { get { return _adminWeb.Restarts; } }

        public void Start()
        {
            if (_app.Adhoc != null) _app.Adhoc.Failed += OnAdhocFailed;
            if (_app.WebPublic != null) _app.WebPublic.Failed += OnPublicFailed;
            if (_app.WebAdmin != null) _app.WebAdmin.Failed += OnAdminFailed;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "supervisor";
            _thread.Start();
            Log.Debug("APP", "supervisor started");
        }

        public void Stop()
        {
            try { _stop.Set(); } catch { }
            try { if (_thread != null && _thread.IsAlive) _thread.Join(2000); } catch { }
            try { if (_app.Adhoc != null) _app.Adhoc.Failed -= OnAdhocFailed; } catch { }
            try { if (_app.WebPublic != null) _app.WebPublic.Failed -= OnPublicFailed; } catch { }
            try { if (_app.WebAdmin != null) _app.WebAdmin.Failed -= OnAdminFailed; } catch { }
        }

        private void OnAdhocFailed(object sender, EventArgs e)
        {
            _adhoc.Fault = true;
            Log.Error("APP", "supervisor: adhoc listener reported a failure");
        }

        private void OnPublicFailed(object sender, EventArgs e)
        {
            _publicWeb.Fault = true;
            Log.Error("APP", "supervisor: public web listener reported a failure");
        }

        private void OnAdminFailed(object sender, EventArgs e)
        {
            _adminWeb.Fault = true;
            Log.Error("APP", "supervisor: admin web listener reported a failure");
        }

        private void Loop()
        {
            DateTime lastMemoryLog = DateTime.UtcNow;

            while (!_stop.WaitOne(2000))
            {
                if (_app.ShuttingDown) return;

                try
                {
                    DateTime now = DateTime.UtcNow;
                    Check(_adhoc, now);
                    Check(_publicWeb, now);
                    Check(_adminWeb, now);

                    if ((now - lastMemoryLog).TotalMinutes >= 10)
                    {
                        lastMemoryLog = now;
                        Log.Info("APP", "health: " + Prefix(_app.Adhoc) + " || " + Prefix(_app.WebPublic) + " || " +
                                        Prefix(_app.WebAdmin) + " || mem " +
                                        (_app.WorkingSetBytes / 1048576).ToString(CultureInfo.InvariantCulture) + "MB");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("APP", "supervisor loop: " + ex.Message);
                }
            }
        }

        private static string Prefix(object server)
        {
            if (server == null) return "n/a";
            try
            {
                Dpt.AdHoc.Net.AdhocServer adhoc = server as Dpt.AdHoc.Net.AdhocServer;
                if (adhoc != null) return adhoc.StatusLine();
                Dpt.AdHoc.Web.WebServer web = server as Dpt.AdHoc.Web.WebServer;
                if (web != null) return web.StatusLine();
            }
            catch { }
            return "?";
        }

        private void Check(Watched w, DateTime now)
        {
            bool running = false;
            try { running = w.IsRunning(); } catch { running = false; }
            if (running) return;
            if (now < w.BackoffUntil) return;

            w.Fault = false;
            w.Restarts++;
            int delay = BackoffSeconds(w.Restarts);
            w.BackoffUntil = now.AddSeconds(delay);
            Log.Warn("APP", "supervisor: restarting " + w.Name + " (attempt " + w.Restarts +
                            ", next retry not before " + delay + "s)");
            bool ok = false;
            try { ok = w.Restart(); } catch (Exception ex) { Log.Error("APP", "supervisor: " + w.Name + " restart threw: " + ex.Message); }
            Log.Warn("APP", "supervisor: " + w.Name + " restart " + (ok ? "succeeded" : "failed"));
        }

        private static int BackoffSeconds(int attempt)
        {
            int v = 1 << Math.Min(attempt, 5);
            return Math.Min(30, v);
        }
    }
}