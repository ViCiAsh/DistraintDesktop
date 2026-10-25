using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Distraint.Core
{
    /// <summary>The few Win32 calls Distraint needs.</summary>
    internal static class Native
    {
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const int SW_MINIMIZE = 6;
        public const uint WM_CLOSE = 0x0010;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        private const uint ES_CONTINUOUS = 0x80000000, ES_DISPLAY_REQUIRED = 0x00000002, ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);

        /// <summary>Full path of a process's exe, or null if it can't be read (for example an elevated process).</summary>
        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);

        /// <summary>Every running process: pid -> (parent pid, lower-case exe name).</summary>
        public static System.Collections.Generic.Dictionary<uint, (uint parent, string exe)> Snapshot()
        {
            var map = new System.Collections.Generic.Dictionary<uint, (uint, string)>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    map[e.th32ProcessID] = (e.th32ParentProcessID, (e.szExeFile ?? "").ToLowerInvariant());
            }
            finally { CloseHandle(snap); }
            return map;
        }

        /// <summary>The window in front: its handle, lower-case exe name ("chrome.exe") and full path.</summary>
        public static (IntPtr hwnd, string exe, string path, uint pid) Foreground()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return (IntPtr.Zero, null, null, 0);
            GetWindowThreadProcessId(h, out uint pid);
            string path = ProcessPath(pid);
            if (path != null) return (h, System.IO.Path.GetFileName(path).ToLowerInvariant(), path, pid);
            // Path unreadable (usually an elevated process): the snapshot still knows the exe name.
            return Snapshot().TryGetValue(pid, out var p) && p.exe.Length > 0 ? (h, p.exe, null, pid) : (h, null, null, pid);
        }

        /// <summary>Milliseconds since the last keyboard or mouse input.</summary>
        public static long IdleMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime);
        }

        /// <summary>Keeps the screen on while true (like Android's FLAG_KEEP_SCREEN_ON).</summary>
        public static void KeepAwake(bool on) =>
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

        /// <summary>Makes a window click-through, never-focused and hidden from Alt+Tab (for the small banners).</summary>
        public static void MakeClickThrough(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }
}
