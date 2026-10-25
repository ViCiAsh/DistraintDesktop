using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace Distraint.Core
{
    /// <summary>
    /// The enforcement engine (port of BlockerService). Every 500 ms it looks at the foreground window:
    ///   1. adds the time to the right app (study / locked / strict) in StudyState,
    ///   2. decides whether that app may be used right now, and
    ///   locked app, no time left   -> lock cover on every monitor, then the app is minimized / closed / terminated
    ///   locked app, still has time -> remember it; 5 min before the end a warning banner appears
    ///   its time runs out          -> "Time's up" banner counts down 30 s, then lock
    ///   any other app              -> forget the locked app, cancel the countdown
    /// Polling replaces Android's window-change events, and also replaces the "re-check when a window starts" timers.
    /// Everything runs on the UI thread (a DispatcherTimer), so there are no threading concerns.
    /// </summary>
    public sealed class Blocker
    {
        private const long WarnMs = 5 * 60_000L;          // warning this long before an unlock ends
        private const long WarnBannerMs = 6_000L;         // how long the warning stays up
        private const long GraceMs = 30_000L;             // length of the "Time's up" countdown
        private const long IdleLimitMs = 15 * 60_000L;    // study apps stop counting after this long without keyboard/mouse

        private readonly StudyState state = StudyState.Instance;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string selfExe;
        private long lastMs;

        // lock cover
        private readonly List<OverlayWindow> overlays = new List<OverlayWindow>();
        private DispatcherTimer autoClose;
        private bool closing;
        private IntPtr overlayHwnd;
        private string overlayExe;

        // an open locked app that still has time
        private string currentLocked;

        // "Time's up" grace period
        private bool graceActive;
        private long graceEndsAt;
        private IntPtr graceHwnd;
        private BannerWindow graceBanner;

        // warning banner
        private BannerWindow warnBanner;
        private long warnHideAt;
        private readonly Dictionary<string, long> warnedFor = new Dictionary<string, long>();   // key -> unlock_at it was shown for

        public Blocker()
        {
            string self = Process.GetCurrentProcess().MainModule?.FileName;
            selfExe = self == null ? "distraint.exe" : System.IO.Path.GetFileName(self).ToLowerInvariant();
            timer.Tick += (s, e) => Tick();
        }

        public void Start() { lastMs = clock.ElapsedMilliseconds; timer.Start(); }

        public void Stop() { timer.Stop(); CloseOverlayNow(); RemoveGrace(); RemoveWarn(); }

        // ------------------------------------------------------------------
        private void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            long delta = Math.Min(now - lastMs, 2000);        // a sleep/hibernate gap must not count as use
            lastMs = now;

            state.Rollover();
            var fg = Native.Foreground();
            if (fg.exe != null && fg.exe != selfExe)
            {
                string exe = ResolveLocked(fg.pid, fg.exe);       // the locked app this window belongs to, if any
                Track(exe, delta);
                Enforce(fg.hwnd, exe, exe == fg.exe ? fg.path : state.PathOf(exe));
            }
            UpdateGrace();
            if (warnBanner != null && Environment.TickCount64 >= warnHideAt) RemoveWarn();
            state.FlushIfDirty();
        }

        /// <summary>If the window in front isn't locked but a parent process is (a launcher starting a game), it counts as that locked app.</summary>
        private string ResolveLocked(uint pid, string direct)
        {
            if (state.IsBlocked(direct) || state.IsStrictApp(direct)) return direct;
            if (state.Blocked().Count == 0 && state.StrictApps().Count == 0) return direct;
            var procs = Native.Snapshot();
            for (int i = 0; i < 8 && procs.TryGetValue(pid, out var me); i++)
            {
                pid = me.parent;
                if (!procs.TryGetValue(pid, out var parent)) break;
                if (state.IsBlocked(parent.exe) || state.IsStrictApp(parent.exe)) return parent.exe;
            }
            return direct;
        }

        /// <summary>Adds foreground time. Study-only apps pause when you've been idle (reading a PDF counts; walking away doesn't).
        /// Locked apps are always counted, because a controller doesn't register as keyboard/mouse input.</summary>
        private void Track(string exe, long deltaMs)
        {
            if (!state.IsTracked(exe)) return;
            if (state.IsStudyApp(exe) && !state.IsBlocked(exe) && Native.IdleMs() > IdleLimitMs) return;
            state.AddUsage(exe, deltaMs);
        }

        private void Enforce(IntPtr hwnd, string exe, string path)
        {
            if (overlays.Count > 0) return;                   // lock cover already showing

            // STRICT LOCK comes first and ignores study progress and unlocks completely.
            if (state.IsStrictLocked(exe))
            {
                currentLocked = null;
                RemoveWarn();
                RemoveGrace();
                ShowStrict(hwnd, exe, path);
                return;
            }

            if (!state.IsBlocked(exe))
            {
                // The user left the locked app: forget it and cancel any countdown.
                currentLocked = null;
                RemoveWarn();
                RemoveGrace();
                return;
            }

            long left = state.MsLeftFor(exe);
            if (left > 0)
            {
                currentLocked = exe;                          // open and still has time
                MaybeWarn(exe, left);
                return;
            }

            // No time left. If it was open WITH time a moment ago, the time just ran out: grace period first.
            if (currentLocked == exe) { StartGrace(hwnd); return; }
            if (graceActive) return;

            RemoveWarn();
            currentLocked = null;
            ShowLock(hwnd, exe, path);
        }

        // ---------------- warning 5 minutes before the end ----------------
        private void MaybeWarn(string exe, long leftMs)
        {
            if (leftMs > WarnMs || warnBanner != null || graceActive) return;
            if (state.Mode == StudyState.ModeTimed && state.TimedMin * 60_000L <= WarnMs) return;   // whole unlock is shorter than the warning
            string key = state.PerAppActive ? exe : "shared";
            if (warnedFor.TryGetValue(key, out long done) && done == state.UnlockAt) return;       // already warned for this unlock
            warnedFor[key] = state.UnlockAt;

            long mins = (long)Math.Round(leftMs / 60_000.0);
            string title = mins >= 1 ? mins + (mins == 1 ? " minute left" : " minutes left") : "Less than a minute left";
            warnBanner = new BannerWindow(title, "before your unlock ends", Color.FromRgb(0xFF, 0xC1, 0x07));
            warnBanner.Show();
            warnHideAt = Environment.TickCount64 + WarnBannerMs;
        }

        private void RemoveWarn()
        {
            if (warnBanner == null) return;
            try { warnBanner.Close(); } catch { }
            warnBanner = null;
        }

        // ---------------- "Time's up" grace period ----------------
        private void StartGrace(IntPtr hwnd)
        {
            if (graceActive) return;
            graceActive = true;
            graceHwnd = hwnd;
            graceEndsAt = Environment.TickCount64 + GraceMs;
            RemoveWarn();
            graceBanner = new BannerWindow("Time's up", "Locking in " + GraceMs / 1000 + " s", Color.FromRgb(0x19, 0xCE, 0xFB));
            graceBanner.Show();
        }

        private void UpdateGrace()
        {
            if (!graceActive) return;
            long left = graceEndsAt - Environment.TickCount64;
            if (left > 0) { graceBanner?.SetSubtitle("Locking in " + (left + 999) / 1000 + " s"); return; }

            // Grace over: lock the app unless it got time again in the meantime.
            string exe = currentLocked;
            IntPtr hwnd = graceHwnd;
            RemoveGrace();
            currentLocked = null;
            if (exe == null) return;
            state.Rollover();
            if (state.MsLeftFor(exe) <= 0) ShowLock(hwnd, exe, state.PathOf(exe));
        }

        private void RemoveGrace()
        {
            if (!graceActive) return;
            graceActive = false;
            try { graceBanner?.Close(); } catch { }
            graceBanner = null;
        }

        // ---------------- lock cover ----------------
        private static string Clock(long s) => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", s / 60, s % 60);

        /// <summary>Works out what the cover should say (four situations, as on Android) and shows it.</summary>
        private void ShowLock(IntPtr hwnd, string exe, string path)
        {
            string label = state.LabelOf(exe);
            long goal = Math.Max(1, state.GoalSec());
            long total = state.TotalStudiedSec();
            long remaining = goal - total;
            string title, message, progressText = null;
            int progress = -1;

            if (state.PerAppActive && state.IsUnlocked())
            {
                title = "Time's up";
                message = "You've used your " + state.TimedMin + " minutes in " + label + " for today.";
            }
            else if (remaining > 0)
            {
                long mins = (remaining + 59) / 60;
                title = "Study first";
                message = mins + (mins == 1 ? " more minute" : " more minutes") + " of studying unlocks your apps.";
                progress = (int)(1000 * Math.Min(total, goal) / goal);
                progressText = Clock(total) + " / " + Clock(goal);
            }
            else if (state.CanUnlock())
            {
                title = "You've earned it";
                message = "Open Distraint and unlock your apps.";
            }
            else
            {
                title = "Not today";
                message = "Today's unlock has ended. Apps open again after the daily reset.";
            }
            ShowCover(hwnd, exe, path, label + " is locked", title, message, progress, progressText);
        }

        private void ShowStrict(IntPtr hwnd, string exe, string path)
        {
            string label = state.LabelOf(exe);
            long end = state.StrictEndsAt();
            string message;
            if (end > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                DateTime endLocal = DateTimeOffset.FromUnixTimeMilliseconds(end).LocalDateTime;
                string when = endLocal.ToString(endLocal.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);
                message = "This app can't be opened until " + when + ". Studying won't unlock it.";
            }
            else message = "This app is locked right now.";
            ShowCover(hwnd, exe, path, label + " is locked", "Strict lock", message, -1, null);
        }

        private void ShowCover(IntPtr hwnd, string exe, string path, string appLine, string title,
                               string message, int progress, string progressText)
        {
            overlayHwnd = hwnd;
            overlayExe = exe;
            closing = false;
            ImageSource icon = IconCache.Get(path ?? state.PathOf(exe));
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                var w = new OverlayWindow(screen.Bounds, icon, appLine, title, message, progress, progressText, CloseOverlay);
                overlays.Add(w);
                w.Show();
            }
            autoClose?.Stop();
            autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            autoClose.Tick += (s, e) => { autoClose.Stop(); CloseOverlay(); };
            autoClose.Start();
        }

        /// <summary>Pushes the app away first, then removes the cover 300 ms later so the app is never seen again.</summary>
        private void CloseOverlay()
        {
            autoClose?.Stop();
            if (overlays.Count == 0 || closing) return;
            closing = true;
            PushAway(overlayHwnd, overlayExe);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (s, e) => { t.Stop(); CloseOverlayNow(); };
            t.Start();
        }

        private void CloseOverlayNow()
        {
            foreach (OverlayWindow w in overlays) { try { w.Close(); } catch { } }
            overlays.Clear();
            closing = false;
        }

        /// <summary>What happens to the locked app (Settings > When a locked app opens). Android's equivalent is "go home".</summary>
        private void PushAway(IntPtr hwnd, string exe)
        {
            try
            {
                switch (state.BlockAction)
                {
                    case StudyState.ActionTerminate:
                        string name = System.IO.Path.GetFileNameWithoutExtension(exe);
                        foreach (Process p in Process.GetProcessesByName(name))
                        {
                            try { p.Kill(true); } catch { }
                            p.Dispose();
                        }
                        break;
                    case StudyState.ActionClose:
                        Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        break;
                    default:
                        Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
                        break;
                }
            }
            catch { }
        }
    }
}
