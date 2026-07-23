using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ToPlay.Host.Display;

namespace ToPlay.Host.Input;

/// <summary>
/// Drives the Windows mouse cursor from normalized phone coordinates using
/// <c>SendInput</c>.
///
/// Why mouse and not raw touch injection? Touch injection (a) has to be
/// initialized and can silently fail on some machines, and (b) does not move
/// the *visible* system cursor — so on a streamed desktop (captured with
/// -draw_mouse) a finger drag produces no feedback and taps feel dead. Moving
/// the real cursor means a finger drag looks like the mouse moving and a tap is
/// a left click: the intuitive "control my PC" model that works across almost
/// every desktop app and game that reads the system cursor.
///
/// Coordinates arrive as normalized [0..1] values relative to the streamed
/// video image and are mapped onto the target monitor, then expressed as an
/// absolute position over the whole virtual desktop (0..65535) for SendInput.
/// The mapping is purely fractional, so it is correct regardless of the
/// display's DPI scaling.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MouseInjector : IPointerSink
{
    private readonly object _gate = new();
    private MonitorInfo _monitor;

    // Only the first finger down drives the mouse; extra fingers are ignored
    // so a stray second touch doesn't fight the primary pointer.
    private long _activeId = long.MinValue;
    private bool _down;

    public MouseInjector(MonitorInfo monitor) => _monitor = monitor;

    /// <summary>Nothing to set up for SendInput; always available.</summary>
    public bool Initialize() => true;

    public void SetMonitor(MonitorInfo monitor)
    {
        lock (_gate) _monitor = monitor;
    }

    public void Down(long id, double nx, double ny)
    {
        lock (_gate)
        {
            if (_down && id != _activeId) return; // another finger already owns the cursor
            _activeId = id;
            var (ax, ay) = ToAbsolute(nx, ny);
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTDOWN, ax, ay);
            _down = true;
        }
    }

    public void Move(long id, double nx, double ny)
    {
        lock (_gate)
        {
            if (!_down || id != _activeId) return;
            var (ax, ay) = ToAbsolute(nx, ny);
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay);
        }
    }

    public void Up(long id)
    {
        lock (_gate)
        {
            if (!_down || id != _activeId) return;
            SendMouse(MOUSEEVENTF_LEFTUP, 0, 0);
            _down = false;
            _activeId = long.MinValue;
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            if (_down) SendMouse(MOUSEEVENTF_LEFTUP, 0, 0);
            _down = false;
            _activeId = long.MinValue;
        }
    }

    public void Dispose() => CancelAll();

    // ---- mapping -----------------------------------------------------------

    private (int ax, int ay) ToAbsolute(double nx, double ny)
    {
        nx = Math.Clamp(nx, 0.0, 1.0);
        ny = Math.Clamp(ny, 0.0, 1.0);

        // Target pixel inside the selected monitor, in virtual-desktop space.
        double targetX = _monitor.X + nx * (_monitor.Width - 1);
        double targetY = _monitor.Y + ny * (_monitor.Height - 1);

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        int ax = (int)Math.Round((targetX - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ay = (int)Math.Round((targetY - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (Math.Clamp(ax, 0, 65535), Math.Clamp(ay, 0, 65535));
    }

    private static void SendMouse(uint flags, int ax, int ay)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 0)
            Console.WriteLine($"[mouse] SendInput failed (err={Marshal.GetLastWin32Error()}).");
    }

    // ---- P/Invoke ----------------------------------------------------------

    private const uint INPUT_MOUSE = 0;

    private const uint MOUSEEVENTF_MOVE       = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT is a union in Win32; MOUSEINPUT is the largest member, so a
    // sequential struct with just the mouse payload marshals to the right size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
