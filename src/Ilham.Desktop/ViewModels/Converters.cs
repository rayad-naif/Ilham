using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Ilham.Desktop.ViewModels;

/// <summary>Small value converters used by the chat template (alignment and bubble colour by speaker).</summary>
public static class Converters
{
    /// <summary>Her messages hug the left; yours hug the right.</summary>
    public static readonly IValueConverter HerAlign =
        new FuncValueConverter<bool, HorizontalAlignment>(fromHer => fromHer ? HorizontalAlignment.Left : HorizontalAlignment.Right);

    /// <summary>Her bubbles are warm/pink-tinted; yours are a cool violet.</summary>
    public static readonly IValueConverter BubbleBrush =
        new FuncValueConverter<bool, IBrush>(fromHer => new SolidColorBrush(Color.Parse(fromHer ? "#2a1c2e" : "#2a2340")));
}
