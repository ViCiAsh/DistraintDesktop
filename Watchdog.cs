using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Distraint.Core
{
    /// <summary>
    /// Keeps Distraint running. The same Distraint.exe can run in a second, invisible "watchdog" mode (--watchdog):
    ///   - the watchdog restarts the main app within a couple of seconds if it disappears (killed, crashed);
    ///   - the main app restarts the watchdog if THAT disappears, so ending one process doesn't end the protection.
    /// A deliberate exit (tray Exit with the code, turning the watchdog off in Settings, logging off) first signals
    /// the watchdog to quit, so it doesn't bring the app back.
    /// Each side proves it's alive with a named mutex, so no process lists or command lines are needed.
    /// This is a speed bump, not a wall: see the README for what it can't stop.
    /// </summary>
    public static class Watchdog
    {
        public const string MainMutex = @"Local\Distraint.Single";        // created by the main app (App.OnStartup)
        public const string WatchdogMutex = @"Local\Distraint.Watchdog";

        /// <summary>
        /// "Please quit, this is deliberate" signal: a file holding the UTC time it was written. A file (not an event or
        /// mutex) because it must outlive the main app, which exits right after asking.
        /// </summary>
        public static string QuitFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Distraint", "watchdog.quit");
        private const int QuitValiditySeconds = 30;

        private static bool Exists(string mutexName)
        {
            try
            {
                if (Mutex.TryOpenExisting(mutexName, out Mutex m)) { m.Dispose(); return true; }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }          // exists, but owned at another privilege level
            catch { return false; }
        }

        public static bool MainAlive() => Exists(MainMutex);
        public static bool WatchdogAlive() => Exists(WatchdogMutex);

        public const string AntikillName = "Distraint-Antikill.exe";

        /// <summary>
        /// Launches the invisible watchdog from its own renamed copy of the exe (Distraint-Antikill.exe), so killing by the
        /// name "Distraint.exe" doesn't hit it. It is told where the real app lives so it can restart it. Clears any old quit
        /// request first. If the copy can't be made it falls back to running the normal exe in watchdog mode.
        /// </summary>
        public static void StartWatchdog()
        {
            ClearQuit();
            string main = Environment.ProcessPath;
            Launch(EnsureAntikillCopy() ?? main, "--watchdog \"" + main + "\"");
        }

        /// <summary>Launches the real app hidden in the tray. This is what the watchdog runs when the app has vanished.</summary>
        public static void StartMain(string mainExe)
        {
            if (!string.IsNullOrEmpty(mainExe)) Launch(mainExe, "--tray");
        }

        /// <summary>
        /// Makes (or refreshes) the renamed copy and returns its path, or null if that isn't possible.
        /// A normal build needs the exe to sit next to Distraint.dll, so the copy goes in the same folder. A single-file
        /// build is fully self-contained, so if that folder isn't writable the copy may go to %AppData%\Distraint instead.
        /// `src` is only for testing.
        /// </summary>
        public static string EnsureAntikillCopy(string src = null)
        {
            try
            {
                src = src ?? Environment.ProcessPath;
                string dir = Path.GetDirectoryName(src);
                bool singleFile = !File.Exists(Path.Combine(dir, "Distraint.dll"));

                string beside = Path.Combine(dir, AntikillName);
                if (TryCopy(src, beside)) return beside;
                if (!singleFile) return null;

                string alt = Path.Combine(Path.GetDirectoryName(QuitFile), AntikillName);
                return TryCopy(src, alt) ? alt : null;
            }
            catch { return null; }
        }

        /// <summary>Copies src to dst unless dst is already identical. A copy that is running right now can't be replaced; it is kept.</summary>
        private static bool TryCopy(string src, string dst)
        {
            try
            {
                var s = new FileInfo(src);
                if (File.Exists(dst))
                {
                    var d = new FileInfo(dst);
                    if (d.Length == s.Length && d.LastWriteTimeUtc == s.LastWriteTimeUtc) return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                File.SetLastWriteTimeUtc(dst, s.LastWriteTimeUtc);
                return true;
            }
            catch (IOException) { return File.Exists(dst); }      // in use: keep the existing copy
            catch { return false; }                               // folder not writable
        }

        /// <summary>
        /// Starts an exe with arguments. It goes through "cmd /c start" so the new process is NOT a child of this one:
        /// ending this process's tree (Task Manager > End process tree) then doesn't take the other process with it.
        /// The new process inherits this one's privileges (so an administrator watchdog starts an administrator app).
        /// </summary>
        private static void Launch(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c start \"\" \"" + exe + "\" " + args)
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi)?.Dispose();
            }
            catch { /* could not start now; the other side tries again */ }
        }

        /// <summary>Tells a running watchdog to exit without restarting anything. Safe to call when none is running.</summary>
        public static void RequestQuit()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(QuitFile));
                File.WriteAllText(QuitFile, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static void ClearQuit() { try { File.Delete(QuitFile); } catch { } }

        /// <summary>True if a recent quit request exists (an old, stale one is ignored).</summary>
        private static bool QuitRequested()
        {
            try
            {
                if (!File.Exists(QuitFile)) return false;
                if (!long.TryParse(File.ReadAllText(QuitFile), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) return false;
                return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(QuitValiditySeconds);
            }
            catch { return false; }
        }

        /// <summary>Sleeps up to ms, but returns true early (and stops) as soon as a quit request appears.</summary>
        private static bool WaitOrQuit(int ms)
        {
            for (int waited = 0; waited < ms; waited += 100)
            {
                if (QuitRequested()) return true;
                Thread.Sleep(Math.Min(100, ms - waited));
            }
            return QuitRequested();
        }

        /// <summary>
        /// The watchdog loop (blocks until told to quit). Checks every pollMs; when the main app is gone it waits graceMs
        /// (it may be mid-restart, and a quit request wins) and then calls startMain. If starting keeps failing
        /// (5 times within a minute) it slows down to one try every 30 s instead of spinning.
        /// </summary>
        public static void Run(Action startMain, int pollMs = 1000, int graceMs = 1500, int backoffMs = 30_000)
        {
            using (var single = new Mutex(true, WatchdogMutex, out bool first))
            {
                if (!first) return;                                       // another watchdog is already on duty
                var starts = new Queue<DateTime>();
                while (!WaitOrQuit(pollMs))
                {
                    if (MainAlive()) continue;
                    if (WaitOrQuit(graceMs)) break;
                    if (MainAlive()) continue;

                    DateTime now = DateTime.UtcNow;
                    while (starts.Count > 0 && now - starts.Peek() > TimeSpan.FromMinutes(1)) starts.Dequeue();
                    if (starts.Count >= 5 && WaitOrQuit(backoffMs)) break;

                    starts.Enqueue(DateTime.UtcNow);
                    startMain();
                    if (WaitOrQuit(3000)) break;                          // give it time to come up and create its mutex
                }
                ClearQuit();                                              // the request has been handled
            }
        }
    }
}