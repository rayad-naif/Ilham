using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Ilham.Rendering;
using SkiaSharp;

namespace Ilham.Desktop.Controls;

/// <summary>
/// An Avalonia control that draws Ilham's animated face with SkiaSharp — no web view. It owns an
/// <see cref="AvatarState"/>, ticks it ~60 fps on the UI thread, and paints via the renderer shared with the
/// tests. Call <see cref="SetMood"/> and <see cref="SetAmplitude"/> to drive her.
/// </summary>
public sealed class AvatarControl : Control
{
    private readonly AvatarState _state = new();
    private readonly DispatcherTimer _timer;
    private DateTime _last = DateTime.UtcNow;

    /// <summary>Starts the animation loop.</summary>
    public AvatarControl()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            _state.Step((now - _last).TotalSeconds);
            _last = now;
            InvalidateVisual();
        };
        _timer.Start();
    }

    /// <summary>Switches her expression.</summary>
    /// <param name="mood">Mood name.</param>
    public void SetMood(string mood) => _state.SetMood(mood);

    /// <summary>Feeds live speech loudness (0..1) for lip-sync.</summary>
    /// <param name="amp">Loudness.</param>
    public void SetAmplitude(double amp) => _state.Amplitude = amp;

    /// <summary>Points her gaze (−1..1 on each axis).</summary>
    /// <param name="x">Horizontal.</param>
    /// <param name="y">Vertical.</param>
    public void LookAt(double x, double y) => _state.LookAt(x, y);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double side = Math.Min(Bounds.Width, Bounds.Height);
        if (side < 1) return;
        context.Custom(new FaceDraw(new Rect(0, 0, Bounds.Width, Bounds.Height), _state, side));
    }

    /// <summary>A Skia custom-draw operation that hands the renderer a real SKCanvas.</summary>
    private sealed class FaceDraw(Rect bounds, AvatarState state, double side) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false; // always repaint (animated)
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return; // non-Skia backend: skip rather than crash
            using var l = lease.Lease();
            SKCanvas canvas = l.SkCanvas;
            canvas.Save();
            // Centre the square face within the control.
            canvas.Translate((float)((bounds.Width - side) / 2), (float)((bounds.Height - side) / 2));
            AvatarRenderer.Draw(canvas, (float)side, state, background: null);
            canvas.Restore();
        }
    }
}
