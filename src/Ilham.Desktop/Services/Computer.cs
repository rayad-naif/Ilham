using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ilham.Desktop.Services;

/// <summary>Lets Ilham actually operate the PC — move the mouse, click, type, press keys, open apps.</summary>
public interface IComputer
{
    /// <summary>True if real control is available on this machine.</summary>
    bool CanControl { get; }

    /// <summary>Runs one GUI action (e.g. "type hello", "key ctrl+c", "click 640,400", "open chrome").</summary>
    /// <param name="action">The action string, verb first.</param>
    /// <returns>A short human-readable result.</returns>
    Task<string> ExecuteAsync(string action);
}

/// <summary>Chooses the right controller for this OS.</summary>
public static class Computer
{
    /// <summary>Windows → real SendInput control; other OSes → a no-op so the app still runs.</summary>
    public static IComputer Create() => OperatingSystem.IsWindows() ? new WindowsComputer() : new NullComputer();
}

/// <summary>No control (non-Windows dev/CI).</summary>
public sealed class NullComputer : IComputer
{
    /// <inheritdoc />
    public bool CanControl => false;
    /// <inheritdoc />
    public Task<string> ExecuteAsync(string action) => Task.FromResult("PC control isn't available on this system.");
}

/// <summary>
/// Real mouse/keyboard control on Windows via the Win32 <c>SendInput</c> API (and <c>Process.Start</c> for
/// launching apps). Every call is already approval-gated by the UI before it reaches here.
/// </summary>
public sealed class WindowsComputer : IComputer
{
    /// <inheritdoc />
    public bool CanControl => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string action)
    {
        if (!OperatingSystem.IsWindows()) return "PC control is Windows-only.";
        action = action.Trim();
        int sp = action.IndexOf(' ');
        string verb = (sp < 0 ? action : action[..sp]).ToLowerInvariant();
        string arg = sp < 0 ? "" : action[(sp + 1)..].Trim();
        try
        {
            switch (verb)
            {
                case "open" or "launch" or "start":
                    if (arg.Length == 0) return "open what?";
                    Process.Start(new ProcessStartInfo(arg) { UseShellExecute = true });
                    return $"Opened {arg}.";
                case "type" or "write":
                    TypeText(arg);
                    return $"Typed \"{Trunc(arg)}\".";
                case "key" or "press" or "hotkey":
                    PressCombo(arg);
                    return $"Pressed {arg}.";
                case "click":
                    (int cx, int cy) = ParsePoint(arg);
                    if (cx >= 0) SetCursorPos(cx, cy);
                    Click(LEFTDOWN, LEFTUP);
                    return cx >= 0 ? $"Clicked at {cx},{cy}." : "Clicked.";
                case "doubleclick" or "dblclick":
                    (int dx, int dy) = ParsePoint(arg);
                    if (dx >= 0) SetCursorPos(dx, dy);
                    Click(LEFTDOWN, LEFTUP); await Task.Delay(60); Click(LEFTDOWN, LEFTUP);
                    return "Double-clicked.";
                case "rightclick":
                    (int rx, int ry) = ParsePoint(arg);
                    if (rx >= 0) SetCursorPos(rx, ry);
                    Click(RIGHTDOWN, RIGHTUP);
                    return "Right-clicked.";
                case "move" or "moveto":
                    (int mx, int my) = ParsePoint(arg);
                    if (mx < 0) return "move where? use: move x,y";
                    SetCursorPos(mx, my);
                    return $"Moved to {mx},{my}.";
                case "scroll":
                    int amt = int.TryParse(arg, out var a) ? a : -3;
                    Scroll(amt);
                    return $"Scrolled {amt}.";
                case "wait" or "sleep":
                    int ms = Math.Clamp(int.TryParse(arg, out var w) ? w : 500, 0, 5000);
                    await Task.Delay(ms);
                    return $"Waited {ms} ms.";
                default:
                    return $"I don't know the action \"{verb}\".";
            }
        }
        catch (Exception ex) { return $"Couldn't do it: {ex.Message}"; }
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // (x,y) or -1,-1 if not provided/parseable.
    private static (int, int) ParsePoint(string arg)
    {
        var p = arg.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 2 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) ? (x, y) : (-1, -1);
    }

    // ---- Win32 SendInput ----------------------------------------------------------------------------------

    private static void TypeText(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n') { KeyVk(VK_RETURN); continue; }
            if (c == '\t') { KeyVk(VK_TAB); continue; }
            SendKeyUnicode(c, down: true);
            SendKeyUnicode(c, down: false);
        }
    }

    private static void PressCombo(string combo)
    {
        var parts = combo.Split(new[] { '+', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        var vks = parts.Select(VkFor).Where(v => v != 0).ToArray();
        if (vks.Length == 0) return;
        for (int i = 0; i < vks.Length; i++) SendVk(vks[i], down: true);       // press all (modifiers first as written)
        for (int i = vks.Length - 1; i >= 0; i--) SendVk(vks[i], down: false);  // release in reverse
    }

    private static void KeyVk(ushort vk) { SendVk(vk, true); SendVk(vk, false); }

    private static ushort VkFor(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" => VK_CONTROL,
        "alt" => VK_MENU,
        "shift" => VK_SHIFT,
        "win" or "super" or "meta" => VK_LWIN,
        "enter" or "return" => VK_RETURN,
        "tab" => VK_TAB,
        "esc" or "escape" => VK_ESCAPE,
        "space" => 0x20,
        "backspace" or "bksp" => 0x08,
        "delete" or "del" => 0x2E,
        "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
        "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74, "f6" => 0x75,
        "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        { Length: 1 } one => (ushort)char.ToUpperInvariant(one[0]), // letters/digits
        _ => 0,
    };

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B,
                         VK_RETURN = 0x0D, VK_TAB = 0x09, VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010, WHEEL = 0x0800;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;

    private static void SendVk(ushort vk, bool down)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.U.ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyUnicode(char c, bool down)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.U.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (down ? 0 : KEYEVENTF_KEYUP) };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    private static void Click(uint down, uint up)
    {
        Mouse(down); Mouse(up);
    }

    private static void Mouse(uint flags)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi = new MOUSEINPUT { dwFlags = flags };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    private static void Scroll(int notches)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.U.mi = new MOUSEINPUT { mouseData = (uint)(notches * 120), dwFlags = WHEEL };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
}
