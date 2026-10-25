using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Distraint.Core;

namespace Distraint
{
    /// <summary>One row of an app list (what AppAdapter displayed on Android).</summary>
    public sealed class AppRow
    {
        public ImageSource Icon { get; set; }
        public string Name { get; set; }
        public string Sub { get; set; }
        public double Progress { get; set; }
        public Visibility SubVis => string.IsNullOrEmpty(Sub) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BarVis { get; set; } = Visibility.Collapsed;
    }

    /// <summary>
    /// Home screen: progress ring, Start/Pause and Unlock, and the three app lists (port of MainActivity).
    /// The window is freely resizable. It starts at 80% of the screen's work area at most, never larger than the
    /// screen, remembers its size, and switches to a stacked layout when it gets narrow.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly StudyState state = StudyState.Instance;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool studying;
        private long lastTick;                  // clock value when the last whole second was counted
        private long lastList = -5000;          // clock value of the last list rebuild
        private bool lastMet;

        public MainWindow()
        {
            WindowPaint.Prep(this);
            InitializeComponent();

            // Fit the screen: remembered size (or a default), but never bigger than the usable desktop area.
            Rect wa = SystemParameters.WorkArea;
            MaxWidth = wa.Width;
            MaxHeight = wa.Height;
            Width = Math.Min(state.WindowW > 0 ? state.WindowW : 1100, wa.Width);
            Height = Math.Min(state.WindowH > 0 ? state.WindowH : 720, wa.Height);

            timer.Tick += OnTimer;
            SizeChanged += (s, e) => AdaptLayout();
            // Leaving the window pauses the study timer, so you have to stay here to earn credit (as on Android).
            Deactivated += (s, e) => StopStudying();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { state.Rollover(); lastList = -5000; Refresh(); timer.Start(); }
                else { timer.Stop(); StopStudying(); }
            };
            Closing += OnClosing;
            Loaded += (s, e) => AdaptLayout();
        }

        /// <summary>Closing the window hides it; the Blocker keeps running from the tray. Exit lives in the tray menu.</summary>
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (WindowState == WindowState.Normal) state.SetWindowSize(Width, Height);
            if (!((App)Application.Current).IsExiting)
            {
                e.Cancel = true;
                Hide();
            }
        }

        /// <summary>Wide: ring on the left, lists on the right. Narrow: ring on top, lists below.</summary>
        private void AdaptLayout()
        {
            bool narrow = ActualWidth < 820;
            if (narrow)
            {
                Grid.SetRow(LeftCard, 0); Grid.SetRowSpan(LeftCard, 1); Grid.SetColumn(LeftCard, 0); Grid.SetColumnSpan(LeftCard, 2);
                LeftCard.Margin = new Thickness(0, 0, 0, 10);
                LeftCard.Height = Math.Max(300, ActualHeight * 0.5);
                Grid.SetRow(RightPanel, 1); Grid.SetRowSpan(RightPanel, 1); Grid.SetColumn(RightPanel, 0); Grid.SetColumnSpan(RightPanel, 2);
                RightPanel.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                Grid.SetRow(LeftCard, 0); Grid.SetRowSpan(LeftCard, 2); Grid.SetColumn(LeftCard, 0); Grid.SetColumnSpan(LeftCard, 1);
                LeftCard.Margin = new Thickness(0, 0, 10, 0);
                LeftCard.Height = double.NaN;
                Grid.SetRow(RightPanel, 0); Grid.SetRowSpan(RightPanel, 2); Grid.SetColumn(RightPanel, 1); Grid.SetColumnSpan(RightPanel, 1);
                RightPanel.Margin = new Thickness(10, 0, 0, 0);
            }
        }

        // ---------------- study timer ----------------
        /// <summary>Runs every 200 ms while the window is visible: counts whole seconds (monotonic clock, so changing
        /// the PC's time can't fast-forward studying), redraws, and refreshes the lists once a second.</summary>
        private void OnTimer(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            if (studying)
            {
                long secs = (now - lastTick) / 1000;
                if (secs >= 1) { state.AddStudied(secs); lastTick += secs * 1000; }
            }
            Refresh();
            if (now - lastList >= 1000) { LoadLists(); lastList = now; }
        }

        private void StartStudying()
        {
            studying = true;
            lastTick = clock.ElapsedMilliseconds;
            Native.KeepAwake(true);
            StudyButton.Content = "Pause";
        }

        private void StopStudying()
        {
            if (!studying && (string)StudyButton.Content == "Start studying") return;
            studying = false;
            Native.KeepAwake(false);
            StudyButton.Content = "Start studying";
        }

        private void StudyButton_Click(object sender, RoutedEventArgs e)
        {
            if (studying) StopStudying(); else StartStudying();
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            state.Unlock();
            Refresh();
            LoadLists();
        }

        // ---------------- menus and other windows ----------------
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Bottom };
            menu.Items.Add(MenuEntry("Apps to lock", () => OpenPicker(PickMode.Locked)));
            menu.Items.Add(MenuEntry("Study apps", () => OpenPicker(PickMode.Study)));
            menu.Items.Add(MenuEntry("Strict lock", OpenStrict));
            menu.IsOpen = true;
        }

        private static MenuItem MenuEntry(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (s, e) => action();
            return item;
        }

        private void OpenPicker(PickMode mode)
        {
            new PickerWindow(mode) { Owner = this }.ShowDialog();
            LoadLists();
            Refresh();
        }

        private void OpenStrict()
        {
            new StrictWindow { Owner = this }.ShowDialog();
            LoadLists();
        }

        private void StrictCard_Click(object sender, MouseButtonEventArgs e) => OpenStrict();

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.ShowDialog();
            state.Rollover();
            Refresh();
            LoadLists();
        }

        // ---------------- drawing ----------------
        private static string ClockText(long s) =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", s / 60, s % 60);

        private static string TimeOfDay(long epochMs)
        {
            DateTime t = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime;
            return t.ToString(t.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);
        }

        private void SetRing(double fraction)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            if (fraction <= 0) { RingArc.Data = Geometry.Empty; return; }
            if (fraction >= 0.9999) fraction = 0.9999;
            const double r = 98, c = 110;
            double a = fraction * 2 * Math.PI;
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            RingArc.Data = new PathGeometry(new[] { fig });
        }

        /// <summary>Redraws the ring, clock, status text and Unlock button from StudyState (cheap, runs every 200 ms).</summary>
        private void Refresh()
        {
            state.Rollover();
            long total = state.TotalStudiedSec();
            long goal = Math.Max(1, state.GoalSec());
            TimerText.Text = ClockText(total);
            GoalText.Text = "of " + ClockText(goal);
            SetRing((double)Math.Min(total, goal) / goal);

            string status;
            if (state.IsUnlocked()) status = "Apps are unlocked.";
            else if (state.CanUnlock()) status = "Goal reached. You can unlock your apps.";
            else if (state.IndividualActive)
            {
                int n = state.StudyApps().Count;
                status = string.Format(CultureInfo.InvariantCulture,
                    "Reach {0} min: each of your {1} study apps counts up to {2} min, and the Start studying timer has no limit.",
                    state.RequiredMin * n, n, state.RequiredMin);
            }
            else status = string.Format(CultureInfo.InvariantCulture, "Study {0} min today to unlock your apps.", state.RequiredMin);
            long peer = state.PeerSecTotal();
            if (peer > 0) status += "  (+" + ClockText(peer) + " from other devices)";
            StatusText.Text = status;

            UnlockButton.IsEnabled = state.CanUnlock();
            UnlockButton.Content = state.IsUnlocked() ? "Unlocked" : "Unlock";

            bool met = state.RequirementMet();
            if (met && !lastMet && studying) System.Media.SystemSounds.Asterisk.Play();   // goal reached while studying
            lastMet = met;
        }

        /// <summary>Fills the three lists. This decides what each row says; the DataTemplate only displays it.</summary>
        private void LoadLists()
        {
            Dictionary<string, long> usage = state.AppSeconds();

            // ---- locked apps ----
            List<string> locked = state.Blocked().OrderBy(x => x).ToList();
            Set pendingRemoval = new Set(state.PendingRemovals());
            long windowLeftSec = Math.Max(0, (state.UnlockedUntil - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000);
            long budgetSec = state.TimedMin * 60L;
            var lockedRows = new List<AppRow>();
            foreach (string exe in locked)
            {
                long used = usage.TryGetValue(exe, out long u) ? u : 0;
                var row = NewRow(exe, pendingRemoval.Has(exe) ? " · unlocks at next reset" : "");
                if (!state.IsUnlocked()) row.Sub = "Locked · used " + ClockText(used);
                else if (state.PerAppActive)
                {
                    long left = Math.Max(0, state.MsLeftFor(exe) / 1000);
                    row.Sub = ClockText(left) + " left";
                    SetBar(row, 1000 * left / Math.Max(1, budgetSec));
                }
                else if (state.Mode == StudyState.ModeTimed)
                {
                    row.Sub = ClockText(windowLeftSec) + " left";
                    SetBar(row, 1000 * windowLeftSec / Math.Max(1, budgetSec));
                }
                else row.Sub = "Unlocked · used " + ClockText(used);
                lockedRows.Add(row);
            }
            LockedList.ItemsSource = lockedRows;
            LockedCount.Text = locked.Count.ToString(CultureInfo.InvariantCulture);
            LockedEmpty.Visibility = locked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ---- study apps ----
            Set pendingAdds = new Set(state.PendingStudyAdds());
            HashSet<string> studySet = state.StudyApps();
            studySet.UnionWith(pendingAdds.Items);
            long cap = state.RequiredMin * 60L;
            bool each = state.IndividualActive;
            var studyRows = new List<AppRow>();
            foreach (string exe in studySet.OrderBy(x => x))
            {
                var row = NewRow(exe, pendingAdds.Has(exe) ? " · from next reset" : "");
                if (!pendingAdds.Has(exe))                        // queued apps aren't measured yet
                {
                    long s = usage.TryGetValue(exe, out long u) ? u : 0;
                    if (each)
                    {
                        long shown = Math.Min(s, cap);
                        row.Sub = ClockText(shown) + " / " + ClockText(cap);
                        SetBar(row, 1000 * shown / Math.Max(1, cap));
                    }
                    else row.Sub = ClockText(s);
                }
                studyRows.Add(row);
            }
            StudyList.ItemsSource = studyRows;
            StudyCount.Text = studySet.Count.ToString(CultureInfo.InvariantCulture);
            StudyEmpty.Visibility = studySet.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ---- strict apps ----
            List<string> strict = state.StrictApps().OrderBy(x => x).ToList();
            Set strictRemoval = new Set(state.PendingStrictAppRemovals());
            string strictLine;
            if (state.StrictActiveNow()) strictLine = "Locked until " + TimeOfDay(state.StrictEndsAt());
            else
            {
                long next = state.MsUntilNextStrictStart();
                strictLine = next >= 0 ? "Locks at " + TimeOfDay(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + next)
                                       : "No window or timer set";
            }
            var strictRows = new List<AppRow>();
            foreach (string exe in strict)
            {
                var row = NewRow(exe, strictRemoval.Has(exe) ? " · removed at next reset" : "");
                row.Sub = strictLine;
                strictRows.Add(row);
            }
            StrictList.ItemsSource = strictRows;
            StrictCount.Text = strict.Count.ToString(CultureInfo.InvariantCulture);
            StrictEmpty.Visibility = strict.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private AppRow NewRow(string exe, string note) => new AppRow
        {
            Icon = IconCache.Get(state.PathOf(exe)),
            Name = state.LabelOf(exe) + note
        };

        private static void SetBar(AppRow row, long value)
        {
            row.Progress = value;
            row.BarVis = Visibility.Visible;
        }

        /// <summary>Tiny wrapper so "contains" reads well in LoadLists.</summary>
        private sealed class Set
        {
            public readonly HashSet<string> Items;
            public Set(HashSet<string> items) { Items = items; }
            public bool Has(string s) => Items.Contains(s);
        }
    }
}
