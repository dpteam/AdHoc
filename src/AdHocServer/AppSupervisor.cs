using System;
using System.Globalization;
using System.Threading;
using Dpt.AdHoc.Core;

namespace Dpt.AdHoc
{
    /// <summary>
    /// Watchdog: keeps the two listeners alive and restarts them with exponential backoff.
    /// This is the "dynamic fault tolerance" part - a dead accept loop never leaves the
    /// process running as a zombie that silently eats connections.
    /// </summary>
    public sealed class AppSupervisor
    {
        private readonly ServerApp _app;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private int _adhocRestarts;
        private int _webRestarts;
        private DateTime _adhocBackoffUntil = DateTime.MinValue;
        private DateTime _webBackoffUntil = DateTime.MinValue;
        private bool _adhocFault;
        private bool _webFault;

        public AppSupervisor(ServerApp app)
        {
            _app = app;
        }

        public int AdhocRestarts { get { return _adhocRestarts; } }
        public int WebRestarts { get { return _webRestarts; } }

        public void Start()
        {
            if (_app.Adhoc != null) _app.Adhoc.Failed += OnAdhocFailed;
            if (_app.Web != null) _app.Web.Failed += OnWebFailed;

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
            try { if (_app.Web != null) _app.Web.Failed -= OnWebFailed; } catch { }
        }

        private void OnAdhocFailed(object sender, EventArgs e)
        {
            _adhocFault = true;
            Log.Error("APP", "supervisor: adhoc listener reported a failure");
        }

        private void OnWebFailed(object sender, EventArgs e)
        {
            _webFault = true;
            Log.Error("APP", "supervisor: web listener reported a failure");
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

                    if ((_adhocFault || !_app.Adhoc.IsRunning) && now >= _adhocBackoffUntil)
                    {
                        _adhocFault = false;
                        _adhocRestarts++;
                        int delay = BackoffSeconds(_adhocRestarts);
                        _adhocBackoffUntil = now.AddSeconds(delay);
                        Log.Warn("APP", "supervisor: restarting adhoc listener (attempt " + _adhocRestarts +
                                        ", next retry not before " + delay + "s)");
                        bool ok = _app.Adhoc.Restart();
                        Log.Warn("APP", "supervisor: adhoc restart " + (ok ? "succeeded" : "failed"));
                    }

                    if ((_webFault || !_app.Web.IsRunning) && now >= _webBackoffUntil)
                    {
                        _webFault = false;
                        _webRestarts++;
                        int delay = BackoffSeconds(_webRestarts);
                        _webBackoffUntil = now.AddSeconds(delay);
                        Log.Warn("APP", "supervisor: restarting web listener (attempt " + _webRestarts +
                                        ", next retry not before " + delay + "s)");
                        bool ok = _app.Web.Restart();
                        Log.Warn("APP", "supervisor: web restart " + (ok ? "succeeded" : "failed"));
                    }

                    if ((now - lastMemoryLog).TotalMinutes >= 10)
                    {
                        lastMemoryLog = now;
                        Log.Info("APP", "health: " + _app.Adhoc.StatusLine() + " || " + _app.Web.StatusLine() +
                                        " || mem " + (_app.WorkingSetBytes / 1048576).ToString(CultureInfo.InvariantCulture) + "MB");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("APP", "supervisor loop: " + ex.Message);
                }
            }
        }

        private static int BackoffSeconds(int attempt)
        {
            int v = 1 << Math.Min(attempt, 5);
            return Math.Min(30, v);
        }
    }
}