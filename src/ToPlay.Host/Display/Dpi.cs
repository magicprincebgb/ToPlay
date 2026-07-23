using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ToPlay.Host.Display;

/// <summary>
/// Makes the process DPI-aware (Per-Monitor v2) so monitor sizes and the
/// coordinates we inject match the real, physical pixels of the display.
///
/// Why this matters: on a 1920x1080 panel scaled to 125%, a DPI-unaware process
/// sees a "logical" 1536x864 desktop. We hand those numbers to ffmpeg's gdigrab,
/// which captures in physical pixels, so it only grabbed the top-left 1536x864 —
/// cropping the right edge and the bottom taskbar. Being DPI-aware makes
/// GetMonitorInfo report the true 1920x1080, so the whole screen is captured and
/// touches map 1:1. Must be called once, before any monitor query.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Dpi
{
    // DPI_AWARENESS_CONTEXT sentinel handles.
    private static readonly IntPtr PerMonitorV2 = new(-4);
    private static readonly IntPtr PerMonitor   = new(-3);
    private static readonly IntPtr SystemAware  = new(-2);

    public static void EnablePerMonitorV2()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorV2) ||
                SetProcessDpiAwarenessContext(PerMonitor) ||
                SetProcessDpiAwarenessContext(SystemAware))
                return;
        }
        catch { /* SetProcessDpiAwarenessContext missing on Windows < 10 1703 */ }

        try { SetProcessDPIAware(); } catch { /* ancient Windows */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
