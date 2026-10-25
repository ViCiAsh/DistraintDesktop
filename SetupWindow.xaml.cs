using System;
using System.Windows;
using System.Windows.Controls;
using Distraint.Core;

namespace Distraint
{
    /// <summary>
    /// First-run questions (port of MainActivity.runOnboarding, steps 5 to 8, plus protection and startup).
    /// Every answer is the one-time "first choice" that applies immediately. Closing the window counts as confirming the defaults.
    /// </summary>
    public partial class SetupWindow : Window
    {
        private bool applied;

        public SetupWindow()
        {
            WindowPaint.Prep(this);
            InitializeComponent();
            Closing += (s, e) => Apply();
        }

        private static int Read(TextBox box, int fallback, int min, int max)
        {
            if (!int.TryParse(box.Text.Trim(), out int v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        private void Apply()
        {
            if (applied) return;
            applied = true;
            var state = StudyState.Instance;
            state.ChooseCombineFirstTime(CombineEach.IsChecked == true ? StudyState.CombineIndividual : StudyState.CombineTotal);
            state.ChooseRequiredFirstTime(Read(RequiredBox, 30, 1, 600));
            state.ChooseLimitFirstTime(LimitEach.IsChecked == true ? StudyState.LimitPerApp : StudyState.LimitShared);
            state.ChooseUnlockFirstTime(Read(UnlockBox, 60, 0, 1440));
            state.SetProtectEnabled(ProtectCheck.IsChecked == true);
            Startup.Set(StartupCheck.IsChecked == true);
            state.SetupDone = true;
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();
    }
}
