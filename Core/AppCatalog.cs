using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Distraint.Core
{
    /// <summary>Loads (and caches) a program's icon as a frozen image that any thread can use.</summary>
    public static class IconCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Cache =
            new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Cache.GetOrAdd(path, p =>
            {
                try
                {
                    using (System.Drawing.Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(p))
                    {
                        if (ic == null) return null;
                        BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(
                            ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        return bs;
                    }
                }
                catch { return null; }
            });
        }
    }

    public sealed class AppEntry
    {
        public string Exe;      // lower-case file name, the identity used everywhere
        public string Name;
        public string Path;
        public ImageSource Icon;
    }

    /// <summary>
    /// What the picker offers: programs from the Start Menu plus anything with a window open right now.
    /// (Android's equivalent is PackageManager.queryIntentActivities for launcher apps.)
    /// Slow (it resolves shortcuts), so call it from a background thread.
    /// </summary>
    public static class AppCatalog
    {
        public static List<AppEntry> Load()
        {
            var map = new Dictionary<string, AppEntry>();
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string self = Process.GetCurrentProcess().MainModule?.FileName;
            string selfExe = self == null ? "distraint.exe" : System.IO.Path.GetFileName(self).ToLowerInvariant();

            void Add(string path, string name)
            {
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                string exe = System.IO.Path.GetFileName(path).ToLowerInvariant();
                if (exe == selfExe || map.ContainsKey(exe)) return;
                map[exe] = new AppEntry { Exe = exe, Name = name, Path = path, Icon = IconCache.Get(path) };
            }

            // 1. Start Menu shortcuts (all users + this user)
            object shell = null;
            try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); } catch { }
            if (shell != null)
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
                {
                    string root = System.IO.Path.Combine(Environment.GetFolderPath(sf), "Programs");
                    if (!Directory.Exists(root)) continue;
                    foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", opts))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                        if (name.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        string target = Resolve(shell, lnk);
                        if (target != null && File.Exists(target) &&
                            !target.StartsWith(windir, StringComparison.OrdinalIgnoreCase))
                            Add(target, name);
                    }
                }
            }

            // 2. Programs that have a window open right now (covers portable apps and games without a shortcut)
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    using (p)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        string path = Native.ProcessPath((uint)p.Id);
                        if (path == null || path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;
                        string desc = null;
                        try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
                        Add(path, string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim());
                    }
                }
                catch { }
            }

            return map.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string Resolve(object shell, string lnk)
        {
            try
            {
                dynamic sh = shell;
                dynamic link = sh.CreateShortcut(lnk);
                string t = link.TargetPath;
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch { return null; }
        }
    }
}
