using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Distraint.Core
{
    /// <summary>Display name and full path of a program, so lists and icons still work when it isn't running.</summary>
    public sealed class AppInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public sealed class PeerInfo
    {
        public string Name { get; set; }
        public string Day { get; set; }
        public long Seconds { get; set; }
        public long LastSyncMs { get; set; }
        public int ResetHour { get; set; }
    }

    /// <summary>Everything Distraint remembers. Serialized as JSON to %AppData%\Distraint\state.json.</summary>
    public sealed class StateData
    {
        // Live settings
        public int RequiredMin { get; set; } = 30;
        public int Mode { get; set; }                 // 0 until reset / 1 timed
        public int TimedMin { get; set; } = 60;
        public int Combine { get; set; }              // 0 total / 1 individual
        public int LimitStyle { get; set; }           // 0 shared / 1 per app
        public int RolloverHour { get; set; } = 4;
        public int BlockAction { get; set; }          // 0 minimize / 1 close window / 2 terminate
        public int BgAction { get; set; }             // 0 leave alone / 1 close windows / 2 terminate
        public int Theme { get; set; }                // 0 system / 1 light / 2 dark
        public bool ProtectOn { get; set; } = true;
        public bool WatchdogOn { get; set; } = true;
        public bool RunAsAdmin { get; set; }
        public double WindowW { get; set; }
        public double WindowH { get; set; }
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public bool SyncEnabled { get; set; }
        public string Secret { get; set; }
        public Dictionary<string, PeerInfo> Peers { get; set; } = new Dictionary<string, PeerInfo>();

        // Queued setting changes (key -> value), applied at the next reset
        public Dictionary<string, int> Pending { get; set; } = new Dictionary<string, int>();

        // Lists (values are lower-case exe names, e.g. "chrome.exe")
        public HashSet<string> Blocked { get; set; } = new HashSet<string>();
        public HashSet<string> PendingRemove { get; set; } = new HashSet<string>();
        public HashSet<string> StudyApps { get; set; } = new HashSet<string>();
        public HashSet<string> PendingStudyAdd { get; set; } = new HashSet<string>();
        public HashSet<string> StrictApps { get; set; } = new HashSet<string>();
        public HashSet<string> PendingStrictAppsRemove { get; set; } = new HashSet<string>();
        public HashSet<string> StrictWindows { get; set; } = new HashSet<string>();            // "startMin-endMin"
        public HashSet<string> PendingStrictWindowsRemove { get; set; } = new HashSet<string>();
        public Dictionary<string, AppInfo> Known { get; set; } = new Dictionary<string, AppInfo>();

        // Day + progress
        public long DayStart { get; set; }
        public long ResetAt { get; set; }
        public long StudiedSec { get; set; }
        public long UnlockedUntil { get; set; }
        public long UnlockAt { get; set; }
        public bool UnlockUsed { get; set; }
        public long StrictUntil { get; set; }
        public bool StrictDue { get; set; }
        public Dictionary<string, long> DayUsage { get; set; } = new Dictionary<string, long>();      // ms since the reset
        public Dictionary<string, long> UnlockUsage { get; set; } = new Dictionary<string, long>();   // ms since Unlock was pressed

        // One-time flags
        public bool SetupDone { get; set; }
        public bool StudySetupDone { get; set; }
        public bool CombineChosen { get; set; }
        public bool RequiredChosen { get; set; }
        public bool UnlockChosen { get; set; }
        public bool LimitChosen { get; set; }
    }

    /// <summary>
    /// Single source of truth for the rules (port of the Android StudyState).
    /// Same ideas: time-based changes are worked out lazily in Rollover(); loosening changes are queued until
    /// the next reset; tightening changes apply at once. Differences from Android: apps are identified by exe name,
    /// and foreground time is measured by Blocker (sampling the foreground window) instead of Android's usage log.
    /// </summary>
    public sealed class StudyState
    {
        public const int ModeRestOfDay = 0, ModeTimed = 1;
        public const int CombineTotal = 0, CombineIndividual = 1;
        public const int LimitShared = 0, LimitPerApp = 1;
        public const int ThemeSystem = 0, ThemeLight = 1, ThemeDark = 2;
        public const int ActionMinimize = 0, ActionClose = 1, ActionTerminate = 2;

        private static readonly string[] SettingKeys =
            { "required_min", "mode", "timed_min", "rollover_hour", "combine", "limit_style", "block_action", "bg_action" };

        public static StudyState Instance { get; } = new StudyState();

        private readonly StateData d;
        private bool dirty;
        private long lastSaveTick;

        private StudyState() { d = Load(); }

        // ---------------- persistence ----------------
        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Distraint", "state.json");

        private static StateData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<StateData>(File.ReadAllText(FilePath)) ?? new StateData();
            }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }   // keep a corrupt file for inspection
            }
            return new StateData();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
                dirty = false;
                lastSaveTick = Environment.TickCount64;
            }
            catch { /* disk full or locked: try again on the next change */ }
        }

        /// <summary>Frequent small updates (usage ticks) only set a flag; this writes them at most every 5 s.</summary>
        public void FlushIfDirty(bool force = false)
        {
            if (!dirty) return;
            if (!force && Environment.TickCount64 - lastSaveTick < 5000) return;
            Save();
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ---------------- app registry (names, paths) ----------------
        public void RegisterApp(string exe, string name, string path)
        {
            if (string.IsNullOrEmpty(exe)) return;
            d.Known[exe] = new AppInfo { Name = name, Path = path };
        }
        public string LabelOf(string exe) =>
            d.Known.TryGetValue(exe, out var a) && !string.IsNullOrWhiteSpace(a.Name) ? a.Name : exe;
        public string PathOf(string exe) => d.Known.TryGetValue(exe, out var a) ? a.Path : null;

        // ---------------- daily reset ----------------
        public int RolloverHour => d.RolloverHour;
        public long ResetAt => d.ResetAt;
        public bool ProtectEnabled => d.ProtectOn;
        public bool WatchdogEnabled => d.WatchdogOn;
        public void SetWatchdogEnabled(bool on) { d.WatchdogOn = on; Save(); }
        public bool RunAsAdmin => d.RunAsAdmin;
        public void SetRunAsAdmin(bool on) { d.RunAsAdmin = on; Save(); }
        public void SetProtectEnabled(bool on) { d.ProtectOn = on; Save(); }
        public bool SetupDone { get => d.SetupDone; set { d.SetupDone = value; Save(); } }
        public int ThemeMode => d.Theme;
        public void SetThemeMode(int m) { d.Theme = m; Save(); }
        public double WindowW => d.WindowW;
        public double WindowH => d.WindowH;
        public void SetWindowSize(double w, double h) { d.WindowW = w; d.WindowH = h; Save(); }

        /// <summary>First moment strictly after `from` when the local clock reads hour:00.</summary>
        private static long NextBoundary(long fromMs, int hour)
        {
            DateTime from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime;
            var t = new DateTime(from.Year, from.Month, from.Day, hour, 0, 0, DateTimeKind.Local);
            if (new DateTimeOffset(t).ToUnixTimeMilliseconds() <= fromMs) t = t.AddDays(1);
            return new DateTimeOffset(t).ToUnixTimeMilliseconds();
        }

        private static long PrevBoundary(long fromMs, int hour)
        {
            DateTime next = DateTimeOffset.FromUnixTimeMilliseconds(NextBoundary(fromMs, hour)).LocalDateTime;
            return new DateTimeOffset(next.AddDays(-1)).ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Performs the daily reset if it is due: promotes queued settings, applies queued list changes,
        /// clears progress and unlock. No alarm needed: if the PC was off, it happens on the next call.
        /// </summary>
        public void Rollover()
        {
            ApplyStrictPending();
            long now = Now();
            if (now < d.ResetAt) return;

            int oldHour = d.RolloverHour, newHour = oldHour;
            foreach (string k in SettingKeys)
            {
                if (!d.Pending.TryGetValue(k, out int v)) continue;
                SetLive(k, v);
                d.Pending.Remove(k);
                if (k == "rollover_hour") newHour = v;
            }

            d.Blocked.ExceptWith(d.PendingRemove);
            d.PendingRemove.Clear();
            d.StudyApps.UnionWith(d.PendingStudyAdd);
            d.PendingStudyAdd.Clear();

            d.DayStart = PrevBoundary(now, oldHour);
            d.ResetAt = NextBoundary(now, newHour);
            d.StudiedSec = 0;
            d.UnlockedUntil = 0;
            d.UnlockAt = 0;
            d.UnlockUsed = false;
            d.DayUsage.Clear();
            d.UnlockUsage.Clear();

            if (d.PendingStrictAppsRemove.Count > 0 || d.PendingStrictWindowsRemove.Count > 0) d.StrictDue = true;
            ApplyStrictPending();
            Save();
        }

        // ---------------- settings (changes wait for the next reset) ----------------
        public int RequiredMin => d.RequiredMin;
        public int Mode => d.Mode;

        public int TimedMin => d.TimedMin;
        public int CombineMode => d.Combine;
        public int LimitStyle => d.LimitStyle;
        public int BlockAction => d.BlockAction;
        public int BgAction => d.BgAction;

        private int Live(string key)
        {
            switch (key)
            {
                case "required_min": return d.RequiredMin;
                case "mode": return d.Mode;
                case "timed_min": return d.TimedMin;
                case "combine": return d.Combine;
                case "limit_style": return d.LimitStyle;
                case "block_action": return d.BlockAction;
                case "bg_action": return d.BgAction;
                default: return d.RolloverHour;   // "rollover_hour"
            }
        }

        private void SetLive(string key, int v)
        {
            switch (key)
            {
                case "required_min": d.RequiredMin = v; break;
                case "mode": d.Mode = v; break;
                case "timed_min": d.TimedMin = v; break;
                case "combine": d.Combine = v; break;
                case "limit_style": d.LimitStyle = v; break;
                case "block_action": d.BlockAction = v; break;
                case "rollover_hour": d.RolloverHour = v; break;
                case "bg_action": d.BgAction = v; break;
            }
        }

        /// <summary>Queues a change for the next reset. The very first choice of combine / required_min / limit_style applies at once.</summary>
        public void SetPending(string key, int value)
        {
            if (key == "bg_action" && value > d.BgAction) { d.BgAction = value; d.Pending.Remove(key); Save(); return; }   // stricter applies at once
            if (key == "combine" && !d.CombineChosen && value != d.Combine) { ChooseCombineFirstTime(value); return; }
            if (key == "required_min" && !d.RequiredChosen && value != d.RequiredMin) { ChooseRequiredFirstTime(value); return; }
            if (key == "limit_style" && !d.LimitChosen && value != d.LimitStyle) { ChooseLimitFirstTime(value); return; }
            if (value == Live(key)) d.Pending.Remove(key);     // same as today: cancels a queued change
            else d.Pending[key] = value;
            Save();
        }

        /// <summary>What the settings screen shows: the queued value if there is one, else the live one.</summary>
        public int Shown(string key) => d.Pending.TryGetValue(key, out int v) ? v : Live(key);

        public bool PerAppActive => d.LimitStyle == LimitPerApp && d.Mode == ModeTimed;

        private long UnlockUsedMs(string exe) =>
            d.UnlockAt == 0 ? 0 : (d.UnlockUsage.TryGetValue(exe, out long v) ? v : 0);

        /// <summary>Milliseconds this locked app may still be used right now (0 or less = locked).</summary>
        public long MsLeftFor(string exe)
        {
            long window = d.UnlockedUntil - Now();
            if (window <= 0) return 0;
            if (!PerAppActive) return window;
            long left = d.TimedMin * 60_000L - UnlockUsedMs(exe);
            return Math.Min(window, left);
        }

        // ---------------- locked apps: additions now, removals at the reset ----------------
        public bool IsBlocked(string exe) => d.Blocked.Contains(exe);
        public bool IsStudyApp(string exe) => d.StudyApps.Contains(exe);
        public bool IsStrictApp(string exe) => d.StrictApps.Contains(exe);
        public bool IsTracked(string exe) => d.Blocked.Contains(exe) || d.StudyApps.Contains(exe) || d.StrictApps.Contains(exe);

        public HashSet<string> Blocked() => new HashSet<string>(d.Blocked);
        public HashSet<string> PendingRemovals() => new HashSet<string>(d.PendingRemove);

        public void SaveBlocked(ISet<string> chosen)
        {
            var live = Blocked();
            live.UnionWith(chosen);                               // newly ticked apps lock right now
            var toRemove = new HashSet<string>(live);
            toRemove.ExceptWith(chosen);                          // unticked apps leave at the next reset
            d.Blocked = live;
            d.PendingRemove = toRemove;
            Save();
        }

        // ---------------- study apps: removals now, additions at the reset (first list applies at once) ----------------
        public HashSet<string> StudyApps() => new HashSet<string>(d.StudyApps);
        public HashSet<string> PendingStudyAdds() => new HashSet<string>(d.PendingStudyAdd);
        public bool StudySetupDone => d.StudySetupDone;

        public void SaveStudyApps(ISet<string> chosen)
        {
            if (!d.StudySetupDone && chosen.Count > 0)
            {
                d.StudyApps = new HashSet<string>(chosen);
                d.PendingStudyAdd.Clear();
                d.StudySetupDone = true;
                Save();
                return;
            }
            var live = StudyApps();
            live.IntersectWith(chosen);                           // unticked apps stop counting now
            var toAdd = new HashSet<string>(chosen);
            toAdd.ExceptWith(live);                               // later additions wait for the reset
            d.StudyApps = live;
            d.PendingStudyAdd = toAdd;
            Save();
        }

        // ---------------- study progress ----------------
        public long StudiedSec => d.StudiedSec;
        public void AddStudied(long sec) { d.StudiedSec += sec; dirty = true; }

        /// <summary>Called by Blocker: adds foreground time for an app (today, and since Unlock for locked apps).</summary>
        public void AddUsage(string exe, long ms)
        {
            d.DayUsage[exe] = (d.DayUsage.TryGetValue(exe, out long a) ? a : 0) + ms;
            if (d.UnlockAt != 0 && d.Blocked.Contains(exe))
                d.UnlockUsage[exe] = (d.UnlockUsage.TryGetValue(exe, out long b) ? b : 0) + ms;
            dirty = true;
        }

        /// <summary>Seconds used today per tracked app (study, locked, strict).</summary>
        public Dictionary<string, long> AppSeconds() =>
            d.DayUsage.ToDictionary(kv => kv.Key, kv => kv.Value / 1000);

        private long AppSec(string exe) => d.DayUsage.TryGetValue(exe, out long v) ? v / 1000 : 0;

        /// <summary>Total seconds across the STUDY apps only (locked apps are excluded on purpose).</summary>
        public long AppStudySec() => d.StudyApps.Sum(AppSec);

        public bool IndividualActive => d.Combine == CombineIndividual && d.StudyApps.Count > 0;

        public long GoalSec()
        {
            long perApp = d.RequiredMin * 60L;
            return IndividualActive ? perApp * d.StudyApps.Count : perApp;
        }

        public long LocalStudiedSec()
        {
            if (IndividualActive)
            {
                long cap = d.RequiredMin * 60L;
                long sum = d.StudiedSec;                          // the in-app timer is never capped
                foreach (string exe in d.StudyApps) sum += Math.Min(cap, AppSec(exe));
                return sum;
            }
            return d.StudiedSec + AppStudySec();
        }

        public long TotalStudiedSec() => LocalStudiedSec() + PeerSecTotal();
        public bool RequirementMet() => TotalStudiedSec() >= GoalSec();

        // ---------------- unlocking ----------------
        public bool CanUnlock() => RequirementMet() && !d.UnlockUsed;

        public void Unlock()
        {
            long now = Now();
            bool sharedWindow = d.Mode == ModeTimed && d.LimitStyle == LimitShared;
            d.UnlockedUntil = sharedWindow ? now + d.TimedMin * 60_000L : d.ResetAt;
            d.UnlockAt = now;
            d.UnlockUsed = true;
            d.UnlockUsage.Clear();
            Save();
        }

        public bool IsUnlocked() => Now() < d.UnlockedUntil;
        public long UnlockedUntil => d.UnlockedUntil;
        public long UnlockAt => d.UnlockAt;

        // ---- first-time choices (apply at once, later changes are queued) ----
        public bool CombineChosen => d.CombineChosen;
        public void ChooseCombineFirstTime(int mode)
        { d.Combine = mode; d.Pending.Remove("combine"); d.CombineChosen = true; Save(); }

        public bool RequiredChosen => d.RequiredChosen;
        public void ChooseRequiredFirstTime(int minutes)
        { d.RequiredMin = minutes; d.Pending.Remove("required_min"); d.RequiredChosen = true; Save(); }

        public bool LimitChosen => d.LimitChosen;
        public void ChooseLimitFirstTime(int style)
        { d.LimitStyle = style; d.Pending.Remove("limit_style"); d.LimitChosen = true; Save(); }

        public bool UnlockChosen => d.UnlockChosen;
        /// <summary>Unlock length as live: 0 = until the next reset.</summary>
        public int UnlockMinutes() => d.Mode == ModeRestOfDay ? 0 : d.TimedMin;

        public void ChooseUnlockFirstTime(int minutes)
        {
            d.Mode = minutes == 0 ? ModeRestOfDay : ModeTimed;
            d.Pending.Remove("mode");
            d.Pending.Remove("timed_min");
            d.UnlockChosen = true;
            if (minutes > 0) d.TimedMin = minutes;
            Save();
        }

        public void SetUnlockMinutes(int minutes)
        {
            if (!d.UnlockChosen && minutes != UnlockMinutes()) { ChooseUnlockFirstTime(minutes); return; }
            SetPending("mode", minutes == 0 ? ModeRestOfDay : ModeTimed);
            if (minutes > 0) SetPending("timed_min", minutes);
        }
        // ---------------- sync between devices ----------------
        public bool SyncEnabled => d.SyncEnabled;
        public void SetSyncEnabled(bool on) { d.SyncEnabled = on; Save(); }
        public string DeviceId
        {
            get
            {
                if (string.IsNullOrEmpty(d.DeviceId)) { d.DeviceId = Guid.NewGuid().ToString("N"); Save(); }
                return d.DeviceId;
            }
        }
        public string DeviceName => string.IsNullOrWhiteSpace(d.DeviceName) ? Environment.MachineName : d.DeviceName;
        public string Secret => d.Secret;

        public string EnsureSecret()
        {
            if (string.IsNullOrEmpty(d.Secret))
            {
                d.Secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                Save();
            }
            return d.Secret;
        }

        public string CurrentDayKey()
        {
            long ms = d.DayStart > 0 ? d.DayStart : Now();
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        public long PeerSecTotal()
        {
            string day = CurrentDayKey();
            return d.Peers.Values.Where(p => p.Day == day).Sum(p => p.Seconds);
        }

        public void AddPeer(string id, string name)
        {
            if (!d.Peers.TryGetValue(id, out PeerInfo p)) { p = new PeerInfo(); d.Peers[id] = p; }
            p.Name = name;
            p.LastSyncMs = Now();
            Save();
        }

        public void MergePeer(string id, string name, string day, long seconds, int resetHour)
        {
            if (!d.Peers.TryGetValue(id, out PeerInfo p)) { p = new PeerInfo(); d.Peers[id] = p; }
            p.Name = name;
            p.ResetHour = resetHour;
            p.LastSyncMs = Now();
            if (p.Day == day) p.Seconds = Math.Max(p.Seconds, seconds);
            else { p.Day = day; p.Seconds = seconds; }
            Save();
        }

        public List<PeerInfo> PeerList() => d.Peers.Values.ToList();
        public void ForgetPeers() { d.Peers.Clear(); d.Secret = null; Save(); }

        // ---------------- strict lock ----------------
        public HashSet<string> StrictApps() => new HashSet<string>(d.StrictApps);
        public HashSet<string> PendingStrictAppRemovals() => new HashSet<string>(d.PendingStrictAppsRemove);
        public HashSet<string> StrictWindows() => new HashSet<string>(d.StrictWindows);
        public HashSet<string> PendingStrictWindowRemovals() => new HashSet<string>(d.PendingStrictWindowsRemove);
        public long StrictUntil => d.StrictUntil;

        public void SaveStrictApps(ISet<string> chosen)
        {
            var live = StrictApps();
            live.UnionWith(chosen);
            d.StrictApps = live;
            if (!StrictActiveNow())
            {
                var toRemove = new HashSet<string>(live);
                toRemove.ExceptWith(chosen);
                d.PendingStrictAppsRemove = toRemove;
            }   // while locked, the old queue is left exactly as it was
            Save();
        }

        public void AddStrictWindow(int startMin, int endMin)
        {
            if (startMin == endMin) return;
            string w = startMin + "-" + endMin;
            d.StrictWindows.Add(w);
            d.PendingStrictWindowsRemove.Remove(w);               // re-adding cancels a queued removal
            Save();
        }

        /// <summary>Returns false (and does nothing) while a strict lock is active; otherwise queues the removal.</summary>
        public bool RequestRemoveStrictWindow(string w)
        {
            if (StrictActiveNow()) return false;
            d.PendingStrictWindowsRemove.Add(w);
            Save();
            return true;
        }

        /// <summary>Starts or extends the timer. It can only get longer.</summary>
        public void StartStrictTimer(long minutes)
        {
            long until = Now() + minutes * 60_000L;
            if (until > d.StrictUntil) { d.StrictUntil = until; Save(); }
        }

        private static bool InWindow(string w, int nowMin)
        {
            string[] a = w.Split('-');
            int s = int.Parse(a[0]), e = int.Parse(a[1]);
            return s < e ? (nowMin >= s && nowMin < e)            // same-day window
                         : (nowMin >= s || nowMin < e);           // crosses midnight
        }

        public bool StrictActiveNow()
        {
            if (Now() < d.StrictUntil) return true;
            DateTime t = DateTime.Now;
            int nowMin = t.Hour * 60 + t.Minute;
            foreach (string w in d.StrictWindows) if (InWindow(w, nowMin)) return true;
            return false;
        }

        public bool IsStrictLocked(string exe) => d.StrictApps.Contains(exe) && StrictActiveNow();

        /// <summary>Queued strict removals apply only once no lock is active (a reset inside a night window must not open a loophole).</summary>
        private void ApplyStrictPending()
        {
            if (!d.StrictDue || StrictActiveNow()) return;
            d.StrictApps.ExceptWith(d.PendingStrictAppsRemove);
            d.StrictWindows.ExceptWith(d.PendingStrictWindowsRemove);
            d.PendingStrictAppsRemove.Clear();
            d.PendingStrictWindowsRemove.Clear();
            d.StrictDue = false;
            Save();
        }

        /// <summary>Milliseconds until the next daily window starts, or -1 if there are none.</summary>
        public long MsUntilNextStrictStart()
        {
            DateTime now = DateTime.Now;
            long best = -1;
            foreach (string w in d.StrictWindows)
            {
                int s = int.Parse(w.Split('-')[0]);
                DateTime t = new DateTime(now.Year, now.Month, now.Day, s / 60, s % 60, 0, DateTimeKind.Local);
                if (t <= now) t = t.AddDays(1);
                long ms = (long)(t - now).TotalMilliseconds;
                if (best < 0 || ms < best) best = ms;
            }
            return best;
        }

        /// <summary>When the strict lock that is active right now ends (epoch ms); 0 if none.</summary>
        public long StrictEndsAt()
        {
            long end = Now() < d.StrictUntil ? d.StrictUntil : 0;
            DateTime now = DateTime.Now;
            int nowMin = now.Hour * 60 + now.Minute;
            foreach (string w in d.StrictWindows)
            {
                if (!InWindow(w, nowMin)) continue;
                int e = int.Parse(w.Split('-')[1]);
                DateTime t = new DateTime(now.Year, now.Month, now.Day, e / 60, e % 60, 0, DateTimeKind.Local);
                if (t <= now) t = t.AddDays(1);
                end = Math.Max(end, new DateTimeOffset(t).ToUnixTimeMilliseconds());
            }
            return end;
        }
    }
}
