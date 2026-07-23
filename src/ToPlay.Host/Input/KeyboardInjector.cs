using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ToPlay.Host.Input;

/// <summary>
/// Sends a few whole-system key presses via the Win32 <c>SendInput</c> API so
/// the phone's on-screen buttons can act on whatever window is focused on the
/// PC — no matter which folder, game or program is in the foreground.
///
/// Named keys understood by <see cref="Send(string?)"/>:
///   "back" / "esc" → Escape   — universal "go back / cancel". Inside an Android
///                                emulator (LDPlayer's default keymap) Escape is
///                                the Android Back button, so this backs out of
///                                menus in MLBB too.
///   "exit"         → Alt+F4    — closes the focused window/program.
///
/// Nothing here needs a device handle or monitor mapping, so it's a stateless
/// static helper (unlike the pointer injectors).
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyboardInjector
{
    public static void Send(string? key)
    {
        switch (key?.ToLowerInvariant())
        {
            case "back":
            case "esc":
            case "escape":
                Tap(VK_ESCAPE);
                break;

            case "exit":
            case "close":
                Combo(VK_MENU, VK_F4); // Alt+F4
                break;

            default:
                // Unknown key name: ignore rather than injecting something surprising.
                Console.WriteLine($"[key] Ignoring unknown key '{key}'.");
                break;
        }
    }

    /// <summary>Press then release a single virtual key.</summary>
    private static void Tap(ushort vk)
    {
        var inputs = new[]
        {
            KeyInput(vk, keyUp: false),
            KeyInput(vk, keyUp: true),
        };
        SendAll(inputs);
    }

    /// <summary>Press modifier + key, then release both (modifier last).</summary>
    private static void Combo(ushort modifier, ushort vk)
    {
        var inputs = new[]
        {
            KeyInput(modifier, keyUp: false),
            KeyInput(vk, keyUp: false),
            KeyInput(vk, keyUp: true),
            KeyInput(modifier, keyUp: true),
        };
        SendAll(inputs);
    }

    private static void SendAll(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Console.WriteLine($"[key] SendInput sent {sent}/{inputs.Length} (err={Marshal.GetLastWin32Error()}).");
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    // ---- constants ---------------------------------------------------------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_MENU   = 0x12; // ALT
    private const ushort VK_F4     = 0x73;

    // ---- P/Invoke ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // Union of the three input payloads. MOUSEINPUT is the largest member, so
    // including it keeps sizeof(INPUT) matching the native definition — which
    // SendInput validates via the cbSize argument.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
