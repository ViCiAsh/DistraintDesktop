using System;
using System.Collections.Generic;
using System.Linq;

namespace Distraint.Core
{
    /// <summary>
    /// Finds locked apps that are running but not in front, and closes them (Settings > Locked apps in the background).
    /// Blocker only reacts to the window in front; this covers a locked game left running behind another window.
    ///   - a process counts as locked if its exe is locked OR any process that started it (up to 8 levels) is locked;
    ///   - an app must stay locked and running for GraceMs before anything is done to it (so an unlock, or an app that is
    ///     just being handled in front, is never caught half-way);
    ///   - Windows' own processes and Distraint itself are never touched, whatever the lock list says.
    /// Everything it needs is passed in as functions, so the logic can be tested without Windows.
    /// </summary>
    public sealed class BackgroundSweeper
    {
        public const long GraceMs = 5000;
        public const int ModeOff = 0, ModeClose = 1, ModeTerminate = 2;

        /// <summary>Never acted on, even if someone adds one to the locked list.</summary>
        public static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "dwm.exe", "winlogon.exe", "csrss.exe", "services.exe", "svchost.exe", "lsass.exe",
            "smss.exe", "wininit.exe", "system", "registry", "sihost.exe", "ctfmon.exe", "fontdrvhost.exe",
            "conhost.exe", "taskmgr.exe", "searchhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
            "runtimebroker.exe", "applicationframehost.exe"
        };

        private readonly Func<int> mode;
        private readonly Func<HashSet<string>> lockedNow;
        private readonly Func<Dictionary<uint, (uint parent, string exe)>> snapshot;
        private readonly Action<int, uint> act;                 // (mode, pid)
        private readonly Func<long> nowMs;
        private readonly uint selfPid;
        private readonly ISet<string> selfExes;
        private readonly Dictionary<string, long> seen = new Dictionary<string, long>();

        public BackgroundSweeper(Func<int> mode, Func<HashSet<string>> lockedNow,
                                 Func<Dictionary<uint, (uint parent, string exe)>> snapshot, Action<int, uint> act,
                                 Func<long> nowMs, uint selfPid, ISet<string> selfExes)
        {
            this.mode = mode; this.lockedNow = lockedNow; this.snapshot = snapshot; this.act = act;
            this.nowMs = nowMs; this.selfPid = selfPid; this.selfExes = selfExes;
        }

        /// <summary>One pass. `skip` = locked exes that the foreground logic is handling right now (leave them alone).</summary>
        public void Sweep(ISet<string> skip)
        {
            int m = mode();
            if (m == ModeOff) { seen.Clear(); return; }

            HashSet<string> locked = lockedNow();
            locked.ExceptWith(Protected);
            if (locked.Count == 0) { seen.Clear(); return; }

            var targets = FindTargets(snapshot(), locked, skip, selfPid, selfExes);
            foreach (string gone in seen.Keys.Where(k => !targets.ContainsKey(k)).ToList()) seen.Remove(gone);

            long t = nowMs();
            foreach (var kv in targets)
            {
                if (!seen.TryGetValue(kv.Key, out long first)) { seen[kv.Key] = t; continue; }   // first sighting: start the grace
                if (t - first < GraceMs) continue;
                foreach (uint pid in kv.Value) act(m, pid);
                seen[kv.Key] = t;                                                              // if it lingers, try again after another grace
            }
        }

        /// <summary>Locked exe -> pids to act on (the exe's own processes and everything it started).</summary>
        public static Dictionary<string, List<uint>> FindTargets(Dictionary<uint, (uint parent, string exe)> procs,
            HashSet<string> locked, ISet<string> skip, uint selfPid, ISet<string> selfExes)
        {
            var result = new Dictionary<string, List<uint>>();
            foreach (var kv in procs)
            {
                uint pid = kv.Key;
                string own = kv.Value.exe;
                if (pid == selfPid || selfExes.Contains(own) || Protected.Contains(own)) continue;

                string owner = LockedOwner(procs, pid, locked);
                if (owner == null || (skip != null && skip.Contains(owner))) continue;

                if (!result.TryGetValue(owner, out List<uint> list)) result[owner] = list = new List<uint>();
                list.Add(pid);
            }
            return result;
        }

        /// <summary>The locked exe this process is, or was started by (walks up at most 8 parents), or null.</summary>
        private static string LockedOwner(Dictionary<uint, (uint parent, string exe)> procs, uint pid, HashSet<string> locked)
        {
            for (int i = 0; i < 9 && procs.TryGetValue(pid, out var p); i++)
            {
                if (locked.Contains(p.exe)) return p.exe;
                if (p.parent == pid) break;
                pid = p.parent;
            }
            return null;
        }
    }
}
