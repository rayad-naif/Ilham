using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ilham.Desktop.Controls;
using Ilham.Desktop.ViewModels;

namespace Ilham.Desktop.Views;

/// <summary>The main companion window: her face on the left, the conversation on the right.</summary>
public partial class MainWindow : Window
{
    /// <summary>Wires the view model's mood changes to the avatar control.</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var avatar = this.FindControl<AvatarControl>("Avatar");
                avatar?.SetOutfit(vm.Profile.Outfit);
                vm.MoodChanged += mood => avatar?.SetMood(mood);
                vm.Amplitude += amp => avatar?.SetAmplitude(amp);
                vm.OutfitChanged += outfit => avatar?.SetOutfit(outfit);
                vm.Messages.CollectionChanged += (_, _) => ScrollToEnd();
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnSend(object? sender, RoutedEventArgs e) { if (Vm is { } vm) await vm.SendAsync(); }
    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm) { e.Handled = true; await vm.SendAsync(); }
    }
    private async void OnApprove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string id } && Vm is { } vm) await vm.ApproveAsync(id);
    }
    private void OnReject(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string id } && Vm is { } vm) vm.Reject(id);
    }

    private void OnToggleFace(object? sender, RoutedEventArgs e) => Vm?.ToggleFace();

    private void ScrollToEnd() => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd();
}
