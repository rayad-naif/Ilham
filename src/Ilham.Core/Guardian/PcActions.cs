using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ilham.Core.Guardian;

/// <summary>What Ilham can physically do to the PC.</summary>
public interface IPcActions
{
    /// <summary>Time since the last keyboard or mouse input.</summary>
    TimeSpan Idle();

    /// <summary>Locks the screen (Windows: like Win+L).</summary>
    void Lock();

    /// <summary>Schedules a shutdown with a visible countdown.</summary>
    void ScheduleShutdown(int seconds, string message);

    /// <summary>Cancels a scheduled shutdown.</summary>
    void CancelShutdown();

    /// <summary>Human-readable log of what was done (for the UI and tests).</summary>
    IReadOnlyList<string> Log { get; }
}

/// <summary>Picks the right implementation for this OS.</summary>
public static class PcActions
{
    /// <summary>Windows → real actions; anything else → simulated (logged only).</summary>
    public static IPcActions ForCurrentOs() => OperatingSystem.IsWindows() ? new WindowsPcActions() : new SimulatedPcActions();
}

/// <summary>Real Windows implementation via user32.dll and shutdown.exe.</summary>
public sealed partial class WindowsPcActions : IPcActions
{
    private readonly List<string> _log = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Log => _log;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    /// <inheritdoc />
    public TimeSpan Idle()
    {
        if (!OperatingSystem.IsWindows()) return TimeSpan.Zero;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    /// <inheritdoc />
    public void Lock() { _log.Add("lock"); if (OperatingSystem.IsWindows()) LockWorkStation(); }

    /// <inheritdoc />
    public void ScheduleShutdown(int seconds, string message)
    {
        _log.Add($"shutdown in {seconds}s");
        string safe = message.Replace("\"", "'");
        Process.Start(new ProcessStartInfo("shutdown", $"/s /t {seconds} /c \"{safe}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    /// <inheritdoc />
    public void CancelShutdown()
    {
        _log.Add("cancel shutdown");
        Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false });
    }
}

/// <summary>Logs actions instead of performing them (non-Windows development and tests).</summary>
public sealed class SimulatedPcActions : IPcActions
{
    private readonly List<string> _log = [];

    /// <summary>Idle time to report (tests set this).</summary>
    public TimeSpan IdleValue { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<string> Log => _log;
    /// <inheritdoc />
    public TimeSpan Idle() => IdleValue;
    /// <inheritdoc />
    public void Lock() => _log.Add("lock (simulated)");
    /// <inheritdoc />
    public void ScheduleShutdown(int seconds, string message) => _log.Add($"shutdown in {seconds}s (simulated)");
    /// <inheritdoc />
    public void CancelShutdown() => _log.Add("cancel shutdown (simulated)");
}
