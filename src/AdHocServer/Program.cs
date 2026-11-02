using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Dpt.AdHoc.Core;
using Dpt.AdHoc.Net;
using Dpt.AdHoc.Web;

namespace Dpt.AdHoc
{
    internal static class Program
    {
        private static ServerApp _app;
        private static AppSupervisor _supervisor;
        private static ConsoleUi _ui;
        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static int _exitCode;

        private static int Main(string[] args)
        {
            try { Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }

            bool headless = false;
            string levelOverride = null;
            int? adhocPort = null;
            int? webPort = null;
            int? adminPort = null;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase)) headless = true;
                    else if (string.Equals(a, "--level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) levelOverride = args[++i];
                    else if (string.Equals(a, "--adhoc-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) adhocPort = ParseInt(args[++i]);
                    else if (string.Equals(a, "--web-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) webPort = ParseInt(args[++i]);
                    else if (string.Equals(a, "--admin-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) adminPort = ParseInt(args[++i]);
                    else if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintUsage();
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Bad arguments: " + ex.Message);
                return 1;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            Console.CancelKeyPress += OnCancelKey;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Config cfg = Config.Load(baseDir);
            if (levelOverride != null) cfg.LogLevel = Config.ParseLevel(levelOverride);
            if (adhocPort.HasValue && adhocPort.Value > 0) cfg.AdhocPort = adhocPort.Value;
            if (webPort.HasValue && webPort.Value > 0) cfg.PublicPort = webPort.Value;
            if (adminPort.HasValue && adminPort.Value > 0) cfg.AdminPort = adminPort.Value;
            cfg.Normalize();
            cfg.EnsureSecrets();

            Log.Configure(cfg);

            Banner(cfg);

            try
            {
                Directory.CreateDirectory(cfg.LogDir);
                Directory.CreateDirectory(cfg.DataDir);
            }
            catch (Exception ex)
            {
                Log.Error("APP", "cannot prepare data/log directories: " + ex.Message);
            }

            _app = new ServerApp();
            _app.Cfg = cfg;
            _app.Db = new GameDatabase(cfg.DataDir);
            _app.State = new ServerState(cfg);

            _app.Adhoc = new AdhocServer(_app);
            _app.Relay = new RelayServer(_app);
            _app.WebPublic = new WebServer(_app, WebRole.Public);
            _app.WebAdmin = new WebServer(_app, WebRole.Admin);

            Log.Info("APP", "starting " + _app.Name + " v" + _app.Version +
                            " (CLR " + Environment.Version + ", " + Environment.OSVersion.VersionString + ")");
            Log.Info("APP", "working directory: " + Environment.CurrentDirectory);
            Log.Info("APP", "adhoc port(s): " + cfg.AdhocPortList() +
                            " | public web: " + cfg.PublicPort.ToString(CultureInfo.InvariantCulture) +
                            " | admin web: " + cfg.AdminPort.ToString(CultureInfo.InvariantCulture));

            if (!_app.Adhoc.Start())
            {
                Log.Error("APP", "adhoc protocol server could not start - exiting");
                _exitCode = 2;
                Shutdown(headless);
                return _exitCode;
            }

            if (cfg.RelayEnabled)
            {
                if (!_app.Relay.Start())
                    Log.Warn("APP", "packet relay did not start (port " + cfg.RelayPort + " busy?); " +
                                    "hairpin/NAT players will not be able to use relay mode");
            }
            else
            {
                Log.Info("APP", "packet relay is disabled in config (relay.enabled=false)");
            }

            if (!_app.WebPublic.Start())
                Log.Error("APP", "public web board could not start; the protocol server keeps running");

            if (!_app.WebAdmin.Start())
                Log.Error("APP", "admin web board could not start; the protocol server keeps running");

            _supervisor = new AppSupervisor(_app);
            _supervisor.Start();

            Log.Info("APP", "ready. adhoc=" + _app.Adhoc.Port +
                            " public=" + _app.WebPublic.Port + " admin=" + _app.WebAdmin.Port +
                            " relay=" + (cfg.RelayEnabled ? cfg.RelayPort.ToString(CultureInfo.InvariantCulture) : "off") +
                            " webRoot=" + _app.WebPublic.Root);
            Log.Info("APP", "public board: " + _app.WebPublic.BaseUrl);
            Log.Info("APP", "admin board : " + _app.WebAdmin.BaseUrl + "  (token: config.ini web.adminToken)");

            if (!headless)
            {
                ConsoleUi ui = new ConsoleUi(_app, RequestShutdown);
                _ui = ui;
                ui.Start();
                PrintAdminHint(cfg);
            }
            else
            {
                Log.Info("APP", "headless mode: no console commands, press Ctrl+C to stop");
            }

            StopEvent.WaitOne();
            Shutdown(headless);
            return _exitCode;
        }

        private static int ParseInt(string s)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            throw new ArgumentException("expected a number, got '" + s + "'");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("DPT.PSP AdHoc Server");
            Console.WriteLine("  --adhoc-port N   override server.adhocPort");
            Console.WriteLine("  --web-port N     override web.publicPort");
            Console.WriteLine("  --admin-port N   override web.adminPort");
            Console.WriteLine("  --level X        Error|Warn|Info|Debug|Verbose");
            Console.WriteLine("  --headless       no interactive console (Ctrl+C to stop)");
            Console.WriteLine("  --help           this text");
        }

        private static void Banner(Config cfg)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine();
                Console.WriteLine("  ____  ____ _____    _         _                    _ ");
                Console.WriteLine(" |  _ \\|  _ \\_   _|  / \\   __ _| | ___   _ __   __ _| |");
                Console.WriteLine(" | | | | |_) || |   / _ \\ / _` | |/ / | | '_ \\ / _` | |");
                Console.WriteLine(" | |_| |  __/ | |  / ___ \\ (_| |   <| |_| | | | (_| | |");
                Console.WriteLine(" |____/|_|    |_| /_/   \\_\\__,_|_|\\_\\\\__|_| |_|\\__,_|_|");
                Console.ResetColor();
                Console.WriteLine("  DPT.PSP AdHoc Server - PRO Online / PPSSPP compatible");
                Console.WriteLine("  adhoc " + cfg.AdhocPort + "  |  public " + cfg.PublicPort + "  |  admin " + cfg.AdminPort +
                                  "  |  log level " + cfg.LogLevel + "  |  .NET 4.0 / XP ready");
                Console.WriteLine();
            }
            catch { }
        }

        private static void RequestShutdown()
        {
            try { StopEvent.Set(); } catch { }
        }

        /// <summary>Prints the admin URL + token to this console only (never to the log file).</summary>
        private static void PrintAdminHint(Config cfg)
        {
            try
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ADMIN BOARD (LAN only):  " + _app.WebAdmin.BaseUrl);
                Console.WriteLine("  admin token:             " + cfg.AdminToken);
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  token lives in config.ini (web.adminToken); the browser derives a key");
                Console.WriteLine("  from it (PBKDF2 + salt) and signs/encrypts every admin API call.");
                Console.ResetColor();
                Console.WriteLine();
            }
            catch { }
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Log.Warn("APP", "Ctrl+C received, shutting down");
            RequestShutdown();
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            if (_app == null) return;
            try { Log.Shutdown(); } catch { }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("APP", "UNHANDLED: " + (ex == null ? "unknown" : ex.ToString()));
                Log.Shutdown();
            }
            catch { }
            if (e.IsTerminating) _exitCode = 3;
        }

        private static void Shutdown(bool headless)
        {
            if (_app == null) return;
            _app.ShuttingDown = true;
            Log.Info("APP", "shutdown requested");

            try { if (_supervisor != null) _supervisor.Stop(); } catch { }
            try { if (_ui != null) _ui.Stop(); } catch { }

            try { if (_app.WebPublic != null) _app.WebPublic.Stop(); } catch (Exception ex) { Log.Warn("APP", "public web stop: " + ex.Message); }
            try { if (_app.WebAdmin != null) _app.WebAdmin.Stop(); } catch (Exception ex) { Log.Warn("APP", "admin web stop: " + ex.Message); }
            try { if (_app.Relay != null) _app.Relay.Stop(); } catch (Exception ex) { Log.Warn("APP", "relay stop: " + ex.Message); }
            try { if (_app.Adhoc != null) _app.Adhoc.Stop(); } catch (Exception ex) { Log.Warn("APP", "adhoc stop: " + ex.Message); }

            Log.Info("APP", "goodbye");
            Log.Shutdown();

            if (!headless)
            {
                try
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Server stopped. Press ENTER to close this window.");
                    Console.ResetColor();
                    Console.ReadLine();
                }
                catch { }
            }
        }
    }
}