using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ToPlay.Host.Services;

/// <summary>
/// Registers a system-wide hotkey (default <c>Ctrl + Alt + Shift + Q</c>) that
/// shuts the host down from anywhere — handy while a game is running fullscreen
/// on another monitor and the console window isn't focused.
///
/// A global hotkey needs a Win32 message loop, so this runs its own background
/// thread: it calls <c>RegisterHotKey</c> with a NULL window (thread-scoped) and
/// pumps messages until the hotkey fires or <see cref="Dispose"/> is called.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyService : IDisposable
{
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
    private const int HOTKEY_ID = 0xB0B;

    private readonly Action _onPressed;
    private readonly uint _modifiers;
    private readonly uint _vk;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _disposed;

    /// <summary>Human-readable shortcut, for the console banner.</summary>
    public string Description { get; }

    /// <param name="onPressed">Invoked once when the hotkey is pressed.</param>
    public HotkeyService(Action onPressed)
    {
        _onPressed = onPressed;
        _modifiers = MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT;
        _vk = 0x51; // 'Q'
        Description = "Ctrl + Alt + Shift + Q";
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ToPlay-Hotkey" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        if (!RegisterHotKey(IntPtr.Zero, HOTKEY_ID, _modifiers, _vk))
        {
            Console.WriteLine($"[hotkey] Could not register {Description} " +
                              $"(err={Marshal.GetLastWin32Error()}). Use Ctrl+C in the console to quit.");
            return;
        }

        try
        {
            while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HOTKEY_ID)
                {
                    try { _onPressed(); } catch { /* ignore */ }
                    break;
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0)
        {
            // Nudge the blocked GetMessage loop so the thread can exit cleanly.
            try { PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }

    // ---- P/Invoke ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
