using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ToPlay.Host.Services;

/// <summary>
/// Process-wide tuning that trades a little power efficiency for consistently
/// low latency. ToPlay is used to play competitive games, where a single late
/// frame or a 15 ms scheduling hiccup is felt immediately, so the host asks
/// Windows for tighter timing than the power-saving defaults give it.
///
/// Everything here is best-effort: if a call fails the stream still works
/// exactly as before, just with the platform defaults.
/// </summary>
public static class LatencyTuning
{
    // Windows' default timer granularity is ~15.6 ms. Every sleep, timer and
    // wait in the process (ours, Kestrel's and SIPSorcery's) rounds up to that
    // tick, so a frame that is ready 1 ms after a tick can wait 15 ms for the
    // next one. Asking for 1 ms removes that whole class of jitter.
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    private const uint TimerPeriodMs = 1;
    private static bool _timerRaised;

    /// <summary>
    /// Call once at startup, before the web host starts serving.
    /// </summary>
    public static void Apply()
    {
        // 1) 1 ms timer resolution for the whole process.
        try
        {
            if (OperatingSystem.IsWindows() && TimeBeginPeriod(TimerPeriodMs) == 0)
                _timerRaised = true;
        }
        catch { /* winmm missing — keep default granularity */ }

        // 2) Keep garbage collection out of the frame path. Combined with
        //    ServerGC this avoids the blocking gen-2 collections that would
        //    otherwise show up on the phone as an occasional freeze.
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
        catch { /* not supported in this GC configuration */ }

        // 3) The host only forwards frames and touches, so it must never wait
        //    behind background work (updaters, indexers, the browser). Above
        //    normal is enough to be scheduled promptly while still leaving the
        //    game itself with all the CPU it wants.
        try
        {
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch { /* denied — normal priority is fine */ }
    }

    /// <summary>
    /// Hands the timer resolution back to Windows on shutdown. Leaving it
    /// raised would keep the whole machine on a 1 ms tick (worse battery life)
    /// until reboot.
    /// </summary>
    public static void Restore()
    {
        if (!_timerRaised) return;
        _timerRaised = false;
        try { TimeEndPeriod(TimerPeriodMs); } catch { }
    }
}
