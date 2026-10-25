using System;
using Microsoft.Win32;

namespace Distraint.Core
{
    /// <summary>"Start with Windows" through the per-user Run key (no admin rights needed).</summary>
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Distraint";
        private const string TaskName = "Distraint";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                if (k != null && k.GetValue(ValueName) != null) return true;
            return Schtasks("/Query /TN " + TaskName) == 0;
        }

        /// <summary>
        /// Start with Windows. Normal mode uses the per-user Run key. With admin = true it uses a scheduled task set to
        /// "highest privileges", which starts elevated at logon without a UAC prompt (creating it needs Distraint to be
        /// running as administrator right now; if it isn't, this falls back to the Run key and returns false).
        /// </summary>
        public static bool Set(bool on, bool admin = false)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                k?.DeleteValue(ValueName, false);
            Schtasks("/Delete /F /TN " + TaskName);
            if (!on) return true;

            string exe = Environment.ProcessPath;
            if (admin && Schtasks("/Create /F /TN " + TaskName + " /SC ONLOGON /RL HIGHEST /TR \"\\\"" + exe + "\\\" --tray\"") == 0)
                return true;

            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                k?.SetValue(ValueName, "\"" + exe + "\" --tray");
            return !admin;
        }

        private static int Schtasks(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
                using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
            }
            catch { return -1; }
        }
    }
}
