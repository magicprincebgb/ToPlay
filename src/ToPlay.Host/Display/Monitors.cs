using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ToPlay.Host.Display;

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    int X, int Y, int Width, int Height)
{
    /// <summary>Human-readable label for the settings UI.</summary>
    public string Label => $"#{Index} {(IsPrimary ? "(Primary) " : "")}{Width}x{Height} @ {X},{Y}";
}

/// <summary>
/// Enumerates physical monitors and their virtual-desktop pixel bounds.
/// Used to pick the capture source and to map phone touches to screen pixels.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var results = new List<MonitorInfo>();
        int index = 0;

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                results.Add(new MonitorInfo(
                    index++,
                    mi.szDevice ?? $"DISPLAY{index}",
                    primary,
                    mi.rcMonitor.left,
                    mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left,
                    mi.rcMonitor.bottom - mi.rcMonitor.top));
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        // Put the primary monitor first so index 0 == primary by default.
        results.Sort((a, b) => a.IsPrimary == b.IsPrimary ? a.Index.CompareTo(b.Index) : (a.IsPrimary ? -1 : 1));
        // Re-number after sort so Index is stable/contiguous.
        for (int i = 0; i < results.Count; i++)
            results[i] = results[i] with { Index = i };

        if (results.Count == 0)
        {
            // Fallback: single primary monitor via system metrics.
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            results.Add(new MonitorInfo(0, "PRIMARY", true, 0, 0, w, h));
        }

        return results;
    }

    public static MonitorInfo Get(int index)
    {
        var all = Enumerate();
        if (index < 0 || index >= all.Count) index = 0;
        return all[index];
    }

    // ---- P/Invoke ----------------------------------------------------------

    private const int MONITORINFOF_PRIMARY = 0x1;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
