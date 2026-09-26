using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameStream;

/// <summary>Uma janela ou monitor que pode ser transmitido.</summary>
/// <param name="Id">"w:&lt;hwnd&gt;" ou "m:&lt;hmonitor&gt;".</param>
public sealed record CaptureSource(string Id, string Kind, string Name, string Detail, int Width, int Height, int ProcessId);

/// <summary>Lista janelas visíveis e monitores (coordenadas em pixels físicos).</summary>
public static class CaptureSources
{
    public static List<CaptureSource> List()
    {
        var list = new List<CaptureSource>();
        list.AddRange(Windows());
        list.AddRange(Monitors());
        return list;
    }

    public static CaptureSource? Find(string id) => List().FirstOrDefault(s => s.Id == id);

    private static IEnumerable<CaptureSource> Windows()
    {
        var own = Environment.ProcessId;
        var result = new List<CaptureSource>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var title = GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) return true;

            GetClientRect(hwnd, out var rc);
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            if (w < 64 || h < 64) return true;

            string exe;
            try { exe = Process.GetProcessById((int)pid).ProcessName; } catch { exe = "?"; }
            if (exe is "explorer" && title == "Program Manager") return true;

            result.Add(new CaptureSource($"w:{hwnd.ToInt64()}", "window", title, $"{exe}.exe", w, h, (int)pid));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IEnumerable<CaptureSource> Monitors()
    {
        var result = new List<CaptureSource>();
        var n = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            n++;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hmon, ref info))
            {
                var w = info.rcMonitor.Right - info.rcMonitor.Left;
                var h = info.rcMonitor.Bottom - info.rcMonitor.Top;
                var primary = (info.dwFlags & 1) != 0;
                result.Add(new CaptureSource($"m:{hmon.ToInt64()}", "monitor",
                    $"Monitor {n}{(primary ? " (principal)" : "")}", $"{w}×{h}", w, h, 0));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------- Win32 ----------

    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
