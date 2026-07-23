using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ToPlay.Host.Display;

namespace ToPlay.Host.Input;

/// <summary>
/// Real multi-touch injector built on the Windows <em>synthetic pointer device</em>
/// API (<c>CreateSyntheticPointerDevice</c> / <c>InjectSyntheticPointerInput</c>,
/// Windows 10 1809+). Unlike <see cref="MouseInjector"/> this delivers genuine
/// simultaneous contacts — essential for games such as MLBB where you hold a
/// movement joystick with one thumb while tapping skills with the other.
///
/// Design notes (mirrors Sunshine's Windows input backend):
///  • Up to <see cref="MaxContacts"/> fingers, each pinned to a fixed slot so its
///    Windows pointerId stays stable for the life of the touch.
///  • Windows auto-cancels a held contact if it isn't refreshed within ~1s, so a
///    background timer re-injects all active contacts every 120ms (keeps the
///    joystick "pressed").
///  • The edge-triggered DOWN/UP/UPDATE/CANCELED flags are cleared after every
///    inject, leaving a steady INRANGE|INCONTACT state for held fingers.
///  • Windows requires the active contacts to be packed contiguously in the
///    injected array, so we build a fresh compact array on each inject.
///
/// Coordinates arrive normalized [0..1] over the streamed video image and map to
/// physical monitor pixels (the host is DPI-aware, so these are real pixels).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TouchInjector : IPointerSink
{
    private const int MaxContacts = 10;

    private readonly object _gate = new();
    private MonitorInfo _monitor;

    private IntPtr _device = IntPtr.Zero;
    private readonly POINTER_TYPE_INFO[] _slots = new POINTER_TYPE_INFO[MaxContacts];
    private readonly bool[] _used = new bool[MaxContacts];
    private readonly long[] _extId = new long[MaxContacts]; // phone pointerId per slot
    private int _primary = -1;
    private Timer? _repeat;

    // Resolved at runtime so we degrade gracefully on older Windows.
    private CreateSyntheticPointerDeviceDelegate? _create;
    private InjectSyntheticPointerInputDelegate? _inject;
    private DestroySyntheticPointerDeviceDelegate? _destroy;

    public TouchInjector(MonitorInfo monitor)
    {
        _monitor = monitor;
        for (int i = 0; i < MaxContacts; i++) _extId[i] = long.MinValue;
    }

    public void SetMonitor(MonitorInfo monitor)
    {
        lock (_gate) _monitor = monitor;
    }

    public bool Initialize()
    {
        lock (_gate)
        {
            if (_device != IntPtr.Zero) return true;

            var user32 = GetModuleHandle("user32.dll");
            if (user32 == IntPtr.Zero) user32 = LoadLibrary("user32.dll");
            if (user32 == IntPtr.Zero) return false;

            var pCreate  = GetProcAddress(user32, "CreateSyntheticPointerDevice");
            var pInject  = GetProcAddress(user32, "InjectSyntheticPointerInput");
            var pDestroy = GetProcAddress(user32, "DestroySyntheticPointerDevice");
            if (pCreate == IntPtr.Zero || pInject == IntPtr.Zero || pDestroy == IntPtr.Zero)
            {
                Console.WriteLine("[touch] Synthetic pointer API missing (needs Windows 10 1809+).");
                return false;
            }

            _create  = Marshal.GetDelegateForFunctionPointer<CreateSyntheticPointerDeviceDelegate>(pCreate);
            _inject  = Marshal.GetDelegateForFunctionPointer<InjectSyntheticPointerInputDelegate>(pInject);
            _destroy = Marshal.GetDelegateForFunctionPointer<DestroySyntheticPointerDeviceDelegate>(pDestroy);

            _device = _create(PT_TOUCH, MaxContacts, POINTER_FEEDBACK_DEFAULT);
            if (_device == IntPtr.Zero)
            {
                Console.WriteLine($"[touch] CreateSyntheticPointerDevice failed (err={Marshal.GetLastWin32Error()}).");
                return false;
            }

            _repeat = new Timer(_ => Repeat(), null, Timeout.Infinite, Timeout.Infinite);
            Console.WriteLine("[touch] Virtual multi-touch device ready (up to 10 contacts).");
            return true;
        }
    }

    public void Down(long id, double nx, double ny)
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;

            int slot = FindSlot(id);
            if (slot < 0) slot = FreeSlot();
            if (slot < 0)
            {
                // No room: something is stuck. Reset and grab slot 0.
                CancelAllLocked();
                slot = 0;
            }

            _used[slot] = true;
            _extId[slot] = id;
            if (_primary < 0) _primary = slot;

            var (px, py) = MapToPixels(nx, ny);
            SetSlot(slot, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN, px, py);
            InjectLocked();
            ClearEdge();
            _repeat?.Change(120, 120);
        }
    }

    public void Move(long id, double nx, double ny)
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;
            int slot = FindSlot(id);
            if (slot < 0) return; // move without a down: ignore

            var (px, py) = MapToPixels(nx, ny);
            SetSlot(slot, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE, px, py);
            InjectLocked();
            ClearEdge();
        }
    }

    public void Up(long id)
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return;
            int slot = FindSlot(id);
            if (slot < 0) return;

            _slots[slot].touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP;
            InjectLocked();          // slot still marked used so it's in the array
            FreeSlotAt(slot);
            if (!AnyUsed()) _repeat?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void CancelAll()
    {
        lock (_gate) CancelAllLocked();
    }

    // ---- internals ---------------------------------------------------------

    private void CancelAllLocked()
    {
        if (_device == IntPtr.Zero || !AnyUsed()) return;
        for (int i = 0; i < MaxContacts; i++)
            if (_used[i]) _slots[i].touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_CANCELED;
        InjectLocked();
        for (int i = 0; i < MaxContacts; i++) FreeSlotAt(i);
        _repeat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void Repeat()
    {
        lock (_gate)
        {
            if (_device == IntPtr.Zero || !AnyUsed()) return;
            for (int i = 0; i < MaxContacts; i++)
                if (_used[i])
                    _slots[i].touchInfo.pointerInfo.pointerFlags =
                        POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE;
            InjectLocked();
            ClearEdge();
        }
    }

    /// <summary>Packs used slots contiguously and injects. Caller holds _gate.</summary>
    private void InjectLocked()
    {
        if (_inject == null || _device == IntPtr.Zero) return;

        var buf = new POINTER_TYPE_INFO[MaxContacts];
        uint n = 0;
        for (int i = 0; i < MaxContacts; i++)
        {
            if (!_used[i]) continue;
            buf[n] = _slots[i];
            if (i == _primary)
                buf[n].touchInfo.pointerInfo.pointerFlags |= POINTER_FLAG_PRIMARY;
            n++;
        }
        if (n == 0) return;

        if (!_inject(_device, buf, n))
        {
            // Occasional transient failure: one retry, then log.
            if (!_inject(_device, buf, n))
                Console.WriteLine($"[touch] InjectSyntheticPointerInput failed (err={Marshal.GetLastWin32Error()}).");
        }
    }

    private void SetSlot(int slot, uint flags, int px, int py)
    {
        ref var s = ref _slots[slot];
        s.type = PT_TOUCH;
        s.touchInfo.pointerInfo.pointerType = PT_TOUCH;
        s.touchInfo.pointerInfo.pointerId = (uint)slot;
        s.touchInfo.pointerInfo.pointerFlags = flags;
        s.touchInfo.pointerInfo.ptPixelLocation = new POINT { x = px, y = py };
        s.touchInfo.touchMask = TOUCH_MASK_NONE;
        s.touchInfo.touchFlags = 0;
    }

    /// <summary>Clears the edge-triggered bits, leaving held contacts steady.</summary>
    private void ClearEdge()
    {
        for (int i = 0; i < MaxContacts; i++)
            if (_used[i])
                _slots[i].touchInfo.pointerInfo.pointerFlags &= ~EDGE;
    }

    private int FindSlot(long id)
    {
        for (int i = 0; i < MaxContacts; i++)
            if (_used[i] && _extId[i] == id) return i;
        return -1;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < MaxContacts; i++)
            if (!_used[i]) return i;
        return -1;
    }

    private bool AnyUsed()
    {
        for (int i = 0; i < MaxContacts; i++)
            if (_used[i]) return true;
        return false;
    }

    private void FreeSlotAt(int slot)
    {
        _used[slot] = false;
        _extId[slot] = long.MinValue;
        _slots[slot].touchInfo.pointerInfo.pointerFlags = POINTER_FLAG_NONE;
        if (_primary == slot)
            _primary = FirstUsed();
    }

    private int FirstUsed()
    {
        for (int i = 0; i < MaxContacts; i++)
            if (_used[i]) return i;
        return -1;
    }

    private (int x, int y) MapToPixels(double nx, double ny)
    {
        nx = Math.Clamp(nx, 0.0, 1.0);
        ny = Math.Clamp(ny, 0.0, 1.0);
        int x = _monitor.X + (int)Math.Round(nx * (_monitor.Width - 1));
        int y = _monitor.Y + (int)Math.Round(ny * (_monitor.Height - 1));
        return (x, y);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CancelAllLocked();
            _repeat?.Dispose();
            _repeat = null;
            if (_device != IntPtr.Zero && _destroy != null)
            {
                try { _destroy(_device); } catch { }
            }
            _device = IntPtr.Zero;
        }
    }

    // ---- constants ---------------------------------------------------------

    private const uint PT_TOUCH = 0x00000002;
    private const uint POINTER_FEEDBACK_DEFAULT = 1;

    private const uint POINTER_FLAG_NONE      = 0x00000000;
    private const uint POINTER_FLAG_INRANGE   = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_PRIMARY   = 0x00002000;
    private const uint POINTER_FLAG_CANCELED  = 0x00008000;
    private const uint POINTER_FLAG_DOWN      = 0x00010000;
    private const uint POINTER_FLAG_UPDATE    = 0x00020000;
    private const uint POINTER_FLAG_UP        = 0x00040000;
    private const uint EDGE = POINTER_FLAG_DOWN | POINTER_FLAG_UP | POINTER_FLAG_UPDATE | POINTER_FLAG_CANCELED;

    private const uint TOUCH_MASK_NONE = 0x00000000;

    // ---- P/Invoke ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    // Native POINTER_TYPE_INFO is a union; POINTER_TOUCH_INFO is the largest
    // member, so a sequential struct carrying it marshals to the right size.
    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TYPE_INFO
    {
        public uint type;
        public POINTER_TOUCH_INFO touchInfo;
    }

    private delegate IntPtr CreateSyntheticPointerDeviceDelegate(uint pointerType, uint maxCount, uint mode);
    private delegate bool InjectSyntheticPointerInputDelegate(IntPtr device, POINTER_TYPE_INFO[] pointerInfo, uint count);
    private delegate void DestroySyntheticPointerDeviceDelegate(IntPtr device);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
