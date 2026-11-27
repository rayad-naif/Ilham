using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ilham.Desktop.ViewModels;
using Ilham.Desktop.Views;

namespace Ilham.Desktop;

/// <summary>The Avalonia application: builds the main window and its view model on startup.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ilham");
            var vm = new MainViewModel(dataDir);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
