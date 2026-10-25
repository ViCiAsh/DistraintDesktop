using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Distraint.Core;

namespace Distraint
{
    /// <summary>Which of the three lists the picker edits.</summary>
    public enum PickMode { Locked, Study, Strict }

    public sealed class PickItem : INotifyPropertyChanged
    {
        public string Exe { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public ImageSource Icon { get; set; }
        public string Note { get; set; }
        public Visibility NoteVis => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;

        private bool isChecked;
        public bool IsChecked
        {
            get => isChecked;
            set { isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// One picker for three lists (port of GamePickerActivity). Apps already in the OTHER one of the locked / study lists are
    /// hidden, so an app can't be both. Strict lock is not part of that rule. Nothing is saved until the window closes;
    /// StudyState then decides what applies now and what waits for the reset.
    /// </summary>
    public partial class PickerWindow : Window
    {
        private readonly PickMode mode;
        private readonly StudyState state = StudyState.Instance;
        private readonly List<PickItem> items = new List<PickItem>();
        private ICollectionView view;
        private bool loaded;

        public PickerWindow(PickMode mode)
        {
            WindowPaint.Prep(this);
            this.mode = mode;
            InitializeComponent();

            TitleText.Text = mode == PickMode.Strict ? "Strict lock apps" : mode == PickMode.Study ? "Study apps" : "Apps to lock";
            HintText.Text = mode == PickMode.Strict
                ? "New apps are strictly locked now. Removing one waits for a reset, and isn't possible while a strict lock is active."
                : mode == PickMode.Study
                    ? (state.StudySetupDone
                        ? "Removed apps stop counting now. New apps count from the next reset."
                        : "Your first study apps count right away. Later additions wait for the next reset.")
                    : "New apps lock now. Unticked apps unlock at the next reset.";

            Loaded += async (s, e) => await LoadAsync();
            Closing += (s, e) => Save();
        }

        private async Task LoadAsync()
        {
            // The scan resolves shortcuts and reads icons, so it runs off the UI thread.
            List<AppEntry> found = await Task.Run(() => AppCatalog.Load());

            HashSet<string> hidden = HiddenApps();
            HashSet<string> dups = Duplicates();
            HashSet<string> ticked0 = InitialTicks();

            // Apps already chosen earlier must always be listed even if they aren't running or in the Start Menu right now;
            // otherwise saving would read them as unticked and queue their removal.
            var byExe = found.ToDictionary(a => a.Exe);
            foreach (string exe in ticked0.Concat(dups))
            {
                if (byExe.ContainsKey(exe)) continue;
                byExe[exe] = new AppEntry { Exe = exe, Name = state.LabelOf(exe), Path = state.PathOf(exe), Icon = IconCache.Get(state.PathOf(exe)) };
            }

            foreach (AppEntry a in byExe.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (hidden.Contains(a.Exe)) continue;             // already in the other list
                items.Add(new PickItem
                {
                    Exe = a.Exe, Name = a.Name, Path = a.Path, Icon = a.Icon,
                    Note = dups.Contains(a.Exe) ? "also in " + (mode == PickMode.Study ? "locked apps" : "study apps") : null,
                    IsChecked = ticked0.Contains(a.Exe)
                });
            }

            view = CollectionViewSource.GetDefaultView(items);
            AppList.ItemsSource = view;
            LoadingText.Visibility = Visibility.Collapsed;
            loaded = true;
        }

        /// <summary>Pre-tick the current choices. Queued study apps count as ticked; apps queued for removal count as unticked.</summary>
        private HashSet<string> InitialTicks()
        {
            HashSet<string> t;
            if (mode == PickMode.Strict) { t = state.StrictApps(); t.ExceptWith(state.PendingStrictAppRemovals()); }
            else if (mode == PickMode.Study) { t = state.StudyApps(); t.UnionWith(state.PendingStudyAdds()); }
            else { t = state.Blocked(); t.ExceptWith(state.PendingRemovals()); }
            return t;
        }

        /// <summary>Apps hidden because they are already in the other list. An app in THIS list is never hidden.</summary>
        private HashSet<string> HiddenApps()
        {
            if (mode == PickMode.Strict) return new HashSet<string>();
            HashSet<string> study = state.StudyApps();
            study.UnionWith(state.PendingStudyAdds());
            HashSet<string> locked = state.Blocked();             // includes ones queued for removal (still locked until the reset)
            HashSet<string> mine = mode == PickMode.Study ? study : locked;
            var hide = new HashSet<string>(mode == PickMode.Study ? locked : study);
            hide.ExceptWith(mine);
            return hide;
        }

        /// <summary>Apps in BOTH lists (left over from before the rule). They stay visible so they can be unticked.</summary>
        private HashSet<string> Duplicates()
        {
            if (mode == PickMode.Strict) return new HashSet<string>();
            HashSet<string> study = state.StudyApps();
            study.UnionWith(state.PendingStudyAdds());
            HashSet<string> dup = state.Blocked();
            dup.IntersectWith(study);
            return dup;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (view == null) return;
            string q = SearchBox.Text.Trim();
            view.Filter = string.IsNullOrEmpty(q) ? (Predicate<object>)null
                : o => ((PickItem)o).Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>For programs the scan can't see: pick the .exe yourself.</summary>
        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose a program" };
            if (dlg.ShowDialog(this) != true) return;

            string exe = System.IO.Path.GetFileName(dlg.FileName).ToLowerInvariant();
            PickItem existing = items.FirstOrDefault(i => i.Exe == exe);
            if (existing != null) { existing.IsChecked = true; return; }

            if (HiddenApps().Contains(exe))
            {
                MessageBox.Show(this, "That program is already in the other list (locked or study apps). Remove it there first.",
                    "Distraint", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string desc = null;
            try { desc = FileVersionInfo.GetVersionInfo(dlg.FileName).FileDescription; } catch { }
            var item = new PickItem
            {
                Exe = exe, Path = dlg.FileName, IsChecked = true,
                Name = string.IsNullOrWhiteSpace(desc) ? System.IO.Path.GetFileNameWithoutExtension(exe) : desc.Trim(),
                Icon = IconCache.Get(dlg.FileName)
            };
            items.Add(item);
            view.Refresh();
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>No Save button: the choices are saved whenever the window is closed (as on Android's onPause).</summary>
        private void Save()
        {
            if (!loaded) return;                                  // closed before the list was ready: change nothing
            var chosen = new HashSet<string>();
            foreach (PickItem i in items)
            {
                if (!i.IsChecked) continue;
                chosen.Add(i.Exe);
                state.RegisterApp(i.Exe, i.Name, i.Path);
            }
            if (mode == PickMode.Strict) state.SaveStrictApps(chosen);
            else if (mode == PickMode.Study) state.SaveStudyApps(chosen);
            else state.SaveBlocked(chosen);
        }
    }
}
