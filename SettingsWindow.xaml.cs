using System;
using System.Windows;
using System.Windows.Controls;
using Distraint.Core;

namespace Distraint
{
    /// <summary>
    /// Settings (port of SettingsActivity). Values come from StudyState.Shown() (queued value if one exists, else the live one).
    /// Saving happens when the window closes: the first-ever choice of a setting applies at once, later changes are queued
    /// until the next daily reset. Only the theme applies immediately.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly StudyState state = StudyState.Instance;
        private bool loading = true;      // true while the controls are being filled, so their change events are ignored

        public SettingsWindow()
        {
            WindowPaint.Prep(this);
            InitializeComponent();
            state.Rollover();

            RequiredBox.Text = state.Shown("required_min").ToString();
            UnlockBox.Text = ShownUnlockMin().ToString();
            ResetBox.Text = state.Shown("rollover_hour").ToString();
            (state.Shown("combine") == StudyState.CombineIndividual ? CombineEach : CombineTotal).IsChecked = true;
            (state.Shown("limit_style") == StudyState.LimitPerApp ? LimitEach : LimitShared).IsChecked = true;
            int act = state.Shown("block_action");
            (act == StudyState.ActionTerminate ? ActKill : act == StudyState.ActionClose ? ActClose : ActMin).IsChecked = true;
            int theme = state.ThemeMode;
            (theme == StudyState.ThemeLight ? ThemeLight : theme == StudyState.ThemeDark ? ThemeDark : ThemeSystem).IsChecked = true;
            StartupCheck.IsChecked = Startup.IsEnabled();
            AdminCheck.IsChecked = state.RunAsAdmin;
            RefreshProtection();

            loading = false;
            Closing += (s, e) => SaveAll();
        }

        /// <summary>Unlock length as the screen should show it: 0 = until the next reset, otherwise minutes (queued value if any).</summary>
        private int ShownUnlockMin() =>
            state.Shown("mode") == StudyState.ModeRestOfDay ? 0 : state.Shown("timed_min");

        /// <summary>Reads a number from a box, clamped to min..max. Empty or invalid text returns `fallback`.</summary>
        private static int Read(TextBox box, int fallback, int min, int max)
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        private void SaveAll()
        {
            state.SetPending("required_min", Read(RequiredBox, state.Shown("required_min"), 1, 600));
            state.SetUnlockMinutes(Read(UnlockBox, ShownUnlockMin(), 0, 1440));
            state.SetPending("combine", CombineEach.IsChecked == true ? StudyState.CombineIndividual : StudyState.CombineTotal);
            state.SetPending("rollover_hour", Read(ResetBox, state.Shown("rollover_hour"), 0, 23));
            state.SetPending("limit_style", LimitEach.IsChecked == true ? StudyState.LimitPerApp : StudyState.LimitShared);
            state.SetPending("block_action",
                ActKill.IsChecked == true ? StudyState.ActionTerminate :
                ActClose.IsChecked == true ? StudyState.ActionClose : StudyState.ActionMinimize);
        }

        // ---- theme: the one setting that applies at once ----
        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            int m = ThemeLight.IsChecked == true ? StudyState.ThemeLight
                  : ThemeDark.IsChecked == true ? StudyState.ThemeDark : StudyState.ThemeSystem;
            state.SetThemeMode(m);
            ((App)Application.Current).ApplyTheme();
        }

        // ---- protection ----
        private void RefreshProtection()
        {
            if (state.ProtectEnabled)
            {
                ProtectStatus.Text = "Protection is on. Exiting Distraint needs a typed code, and can't be done during a strict lock.";
                ProtectButton.Content = "Turn off protection…";
            }
            else
            {
                ProtectStatus.Text = "Protection is off. Distraint can be exited from the tray at any time.";
                ProtectButton.Content = "Turn on protection";
            }
        }

        private void Protect_Click(object sender, RoutedEventArgs e)
        {
            if (!state.ProtectEnabled) { state.SetProtectEnabled(true); RefreshProtection(); return; }

            if (state.StrictActiveNow())      // "no matter what": no way out in the middle of a strict lock
            {
                MessageBox.Show(this, "Can't turn off protection during a strict lock.", "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new CodeDialog("Turn off protection?",
                "This makes it easy to exit Distraint and stop all locking.") { Owner = this, Topmost = false };
            if (dlg.ShowDialog() == true) { state.SetProtectEnabled(false); RefreshProtection(); }
        }

        // ---- start with Windows ----
        private void Startup_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            bool want = StartupCheck.IsChecked == true;
            if (!want && state.ProtectEnabled)     // switching it off is a way to dodge the blocker, so protection guards it
            {
                MessageBox.Show(this, "Turn off protection first to stop Distraint starting with Windows.", "Distraint",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                loading = true; StartupCheck.IsChecked = true; loading = false;
                return;
            }
            Startup.Set(want, state.RunAsAdmin);
        }

        private void Admin_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            bool admin = AdminCheck.IsChecked == true;
            state.SetRunAsAdmin(admin);

            string msg = admin
                ? "Distraint will ask for administrator permission the next time it starts. Exit it from the tray and open it again to apply."
                : "Distraint will start normally from the next launch.";
            if (StartupCheck.IsChecked == true && !Startup.Set(true, admin))
                msg += "\n\nStart with Windows couldn't be set up for administrator mode (that needs Distraint to be running as administrator right now). It will ask for permission at each login instead. To avoid that, open Distraint once as administrator and toggle this option again.";
            MessageBox.Show(this, msg, "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
