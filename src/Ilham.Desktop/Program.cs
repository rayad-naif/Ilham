using Avalonia;

namespace Ilham.Desktop;

/// <summary>Entry point for the native Ilham desktop app (Avalonia — Windows now, cross-platform ready).</summary>
public static class Program
{
    /// <summary>Main. Avalonia requires this to run before any UI type is touched.</summary>
    /// <param name="args">Command-line args.</param>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the Avalonia app (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
