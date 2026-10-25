using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Distraint.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;

namespace Distraint
{
    /// <summary>
    /// Application class. Runs once per process: single-instance guard, theme, tray icon, the Blocker, then the windows.
    /// Closing the main window only hides it (like leaving the app on Android): the Blocker keeps running from the tray.
    /// </summary>
    public partial class App : Application
    {
        private Mutex mutex;
        private EventWaitHandle showEvent;
        private System.Windows.Forms.NotifyIcon tray;
        private Blocker blocker;
        private DispatcherTimer watchdogTimer;
        public SyncService Sync { get; private set; }
        private MainWindow main;

        /// <summary>True once the user really exits (so MainWindow stops turning "close" into "hide").</summary>
        public bool IsExiting { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            int wd = Array.IndexOf(e.Args, "--watchdog");
            if (wd >= 0)
            {
                string mainExe = e.Args.Length > wd + 1 ? e.Args[wd + 1] : null;   // where the real app lives
                Watchdog.Run(() => Watchdog.StartMain(mainExe));
                Environment.Exit(0);
                return;
            }
            // Optional: relaunch elevated (Settings > Protection > Run as administrator).
            if (StudyState.Instance.RunAsAdmin && !IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath, string.Join(" ", e.Args))
                    { UseShellExecute = true, Verb = "runas" });
                    Environment.Exit(0);
                    return;
                }
                catch (Win32Exception) { /* UAC was declined: carry on normally */ }
            }
            // Second launch: wake the first one and quit.
            mutex = new Mutex(true, @"Local\Distraint.Single", out bool first);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Distraint.Show");
            if (!first) { showEvent.Set(); Environment.Exit(0); return; }

            base.OnStartup(e);
            var state = StudyState.Instance;
            state.Rollover();
            ApplyTheme();
            BuildTray();

            blocker = new Blocker();
            blocker.Start();
            Sync = new SyncService(state, a => Dispatcher.Invoke(a));
            if (state.SyncEnabled) Sync.Start();
            watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            watchdogTimer.Tick += (s, a) =>
            {
                if (state.WatchdogEnabled && !IsExiting && !Watchdog.WatchdogAlive()) Watchdog.StartWatchdog();
            };
            watchdogTimer.Start();
            main = new MainWindow();

            bool firstRun = !state.SetupDone;
            if (firstRun) new SetupWindow().ShowDialog();             // asks the first-run questions
            if (!(e.Args.Contains("--tray") && !firstRun)) main.Show();   // Windows startup launches with --tray: no window

            var waiter = new Thread(() =>
            {
                while (true) { showEvent.WaitOne(); Dispatcher.BeginInvoke(new Action(ShowMain)); }
            }) { IsBackground = true };
            waiter.Start();
        }

        private static bool IsAdmin() =>
    new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public void ShowMain()
        {
            if (main == null) return;
            main.Show();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate();
        }

        // ---------------- theme ----------------
        /// <summary>Applies the saved Light/Dark/System theme to every open window at once.</summary>
        public void ApplyTheme()
        {
            int mode = StudyState.Instance.ThemeMode;
            bool dark = mode == StudyState.ThemeDark || (mode == StudyState.ThemeSystem && SystemUsesDark());
            Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
            };
        }

        private static bool SystemUsesDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return true; }
        }

        // ---------------- tray + exit ----------------
        private void BuildTray()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Distraint", null, (s, e) => ShowMain());
            menu.Items.Add("Exit", null, (s, e) => RequestExit());
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/distraint.ico")).Stream),
                Text = "Distraint",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += (s, e) => ShowMain();
        }

        private static System.Drawing.Icon MakeIcon()
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x49, 0xB7, 0xF5)))
            using (var font = new System.Drawing.Font("Segoe UI", 18, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
            using (var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center })
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                g.FillEllipse(fill, 1, 1, 30, 30);
                g.DrawString("D", font, System.Drawing.Brushes.Black, new System.Drawing.RectangleF(0, 0, 32, 32), fmt);
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        /// <summary>
        /// Exit is the one way to stop the blocker, so it is guarded: impossible during a strict lock, and with
        /// protection on it needs the typed code. (Task Manager can still kill the process; see the README.)
        /// </summary>
        public void RequestExit()
        {
            var s = StudyState.Instance;
            if (s.StrictActiveNow())
            {
                tray.ShowBalloonTip(3000, "Distraint", "Can't exit during a strict lock.", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
            if (s.ProtectEnabled)
            {
                var dlg = new CodeDialog("Exit Distraint?",
                    "While Distraint is closed, nothing is locked. To exit anyway, type the 16 characters below by hand.");
                if (dlg.ShowDialog() != true) return;
            }
            IsExiting = true;
            watchdogTimer?.Stop();
            Watchdog.RequestQuit();
            blocker.Stop();
            Sync?.Stop();
            main?.Close();
            Shutdown();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            watchdogTimer?.Stop();
            Watchdog.RequestQuit();
            StudyState.Instance.FlushIfDirty(true);
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StudyState.Instance.FlushIfDirty(true);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            try { mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
