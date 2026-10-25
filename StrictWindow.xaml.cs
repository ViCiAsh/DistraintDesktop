using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Distraint.Core;

namespace Distraint
{
    /// <summary>
    /// Strict lock page (port of StrictLockActivity). Every action saves immediately through StudyState;
    /// Refresh() redraws everything from the saved state. Enforcement itself happens in Blocker.
    /// </summary>
    public partial class StrictWindow : Window
    {
        private readonly StudyState state = StudyState.Instance;

        public StrictWindow()
        {
            WindowPaint.Prep(this);
            InitializeComponent();
            Loaded += (s, e) => { state.Rollover(); Refresh(); };
        }

        private void ChooseApps_Click(object sender, RoutedEventArgs e)
        {
            new PickerWindow(PickMode.Strict) { Owner = this }.ShowDialog();
            state.Rollover();
            Refresh();
        }

        private void AddWindow_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new TimeDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            state.AddStrictWindow(dlg.StartMin, dlg.EndMin);
            Refresh();
        }

        /// <summary>The timer can't be cancelled, so ask once before starting it.</summary>
        private void StartTimer_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TimerBox.Text.Trim(), out int mins))
            {
                MessageBox.Show(this, "Enter the number of minutes.", "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int minutes = Math.Max(1, Math.Min(10080, mins));     // 1 minute to 7 days
            var answer = MessageBox.Show(this,
                "Your strict-lock apps will be locked for " + Duration(minutes) + ". This cannot be cancelled.",
                "Start strict lock?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
            state.StartStrictTimer(minutes);
            Refresh();
        }

        /// <summary>Redraws everything from StudyState.</summary>
        private void Refresh()
        {
            HashSet<string> apps = state.StrictApps();
            apps.ExceptWith(state.PendingStrictAppRemovals());    // queued removals no longer count here
            AppsSummary.Text = apps.Count == 0 ? "No apps chosen" : apps.Count + (apps.Count == 1 ? " app" : " apps");

            WindowList.Children.Clear();
            List<string> wins = state.StrictWindows().OrderBy(w => int.Parse(w.Split('-')[0])).ToList();
            HashSet<string> queued = state.PendingStrictWindowRemovals();
            if (wins.Count == 0)
                WindowList.Children.Add(new TextBlock { Text = "No daily windows yet", Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush") });
            foreach (string w in wins) WindowList.Children.Add(WindowRow(w, queued.Contains(w)));

            long until = state.StrictUntil;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < until)
            {
                DateTime t = DateTimeOffset.FromUnixTimeMilliseconds(until).LocalDateTime;
                TimerStatus.Text = "Locked until " + t.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
            }
            else TimerStatus.Text = "No timer running";
        }

        /// <summary>One row: "22:00 to 06:00" and a Remove button. Removal is refused while a lock is active.</summary>
        private UIElement WindowRow(string w, bool removalQueued)
        {
            string[] a = w.Split('-');
            string text = Clock(int.Parse(a[0])) + " to " + Clock(int.Parse(a[1])) + (removalQueued ? "  (removal queued)" : "");

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });

            var remove = new Button { Content = "Remove", Padding = new Thickness(12, 6, 12, 6) };
            remove.Click += (s, e) =>
            {
                if (state.RequestRemoveStrictWindow(w))
                    MessageBox.Show(this, "Removal queued for the next reset.", "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(this, "Can't remove while a strict lock is active.", "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
                Refresh();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            return row;
        }

        private static string Clock(int minutes) =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes / 60, minutes % 60);

        private static string Duration(int minutes)
        {
            int d = minutes / 1440, h = (minutes % 1440) / 60, m = minutes % 60;
            var parts = new List<string>();
            if (d > 0) parts.Add(d + " d");
            if (h > 0) parts.Add(h + " h");
            if (m > 0 || parts.Count == 0) parts.Add(m + " min");
            return string.Join(" ", parts);
        }
    }
}
