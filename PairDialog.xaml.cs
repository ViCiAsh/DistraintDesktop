using System;
using System.Windows;
using System.Windows.Threading;
using Distraint.Core;

namespace Distraint
{
    /// <summary>Shows the 6-digit pairing code while the pairing window is open. Closes itself when a device pairs.</summary>
    public partial class PairDialog : Window
    {
        private readonly SyncService sync;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        public PairDialog(SyncService sync)
        {
            InitializeComponent();
            this.sync = sync;

            string code = sync.StartPairing();
            CodeText.Text = code.Substring(0, 3) + " " + code.Substring(3);
            var addrs = SyncService.LocalAddresses();
            AddressText.Text = addrs.Count == 0 ? "(no network found)" : string.Join("\n", addrs.ConvertAll(a => a + "   port " + SyncService.Port));

            sync.Paired += OnPaired;
            timer.Tick += (s, e) =>
            {
                int left = sync.PairSecondsLeft();
                TimeText.Text = left > 0 ? "Code valid for " + left + " s" : "Code expired";
                if (left <= 0) Close();
            };
            timer.Start();
            TimeText.Text = "Code valid for 120 s";
            Closed += (s, e) => { timer.Stop(); sync.Paired -= OnPaired; sync.CancelPairing(); };
        }

        private void OnPaired() { DialogResult = true; }   // raised on the UI thread

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
