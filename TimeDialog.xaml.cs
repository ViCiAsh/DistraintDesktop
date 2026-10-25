using System;
using System.Globalization;
using System.Windows;

namespace Distraint
{
    /// <summary>Asks for the start and end of a daily strict-lock window. Results are minutes after midnight.</summary>
    public partial class TimeDialog : Window
    {
        public int StartMin { get; private set; }
        public int EndMin { get; private set; }

        public TimeDialog() {
            WindowPaint.Prep(this);
            InitializeComponent(); }

        private static bool TryParse(string text, out int minutes)
        {
            minutes = 0;
            if (!TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out TimeSpan t)) return false;
            if (t.TotalHours >= 24) return false;
            minutes = (int)t.TotalMinutes;
            return true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(StartBox.Text, out int s) || !TryParse(EndBox.Text, out int en))
            { ErrorText.Text = "Type times like 22:00 or 6:30."; return; }
            if (s == en) { ErrorText.Text = "Start and end can't be the same."; return; }
            StartMin = s; EndMin = en;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
