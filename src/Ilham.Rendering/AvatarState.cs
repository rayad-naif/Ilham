namespace Ilham.Rendering;

/// <summary>
/// The animated state of Ilham's face at one instant: which mood she's easing toward, how far the eased
/// values have travelled, blink, gaze, breathing phase and live mouth openness. Pure data + a time-step, so
/// it's deterministic and testable and the renderer stays stateless.
/// </summary>
public sealed class AvatarState
{
    /// <summary>The nine expression parameters, eased over time toward the current mood's targets.</summary>
    public Expression Current = Expression.For("neutral");
    private Expression _target = Expression.For("neutral");

    /// <summary>Live mouth openness (0..1) from speech, for lip-sync.</summary>
    public double Amplitude;
    private double _ampSmooth;

    /// <summary>1 = eyes open, 0 = shut.</summary>
    public double Blink = 1;
    private double _blinkPhase = -1, _nextBlink = 2;

    /// <summary>Where she's looking, −1..1 on each axis.</summary>
    public double GazeX, GazeY;
    private double _gazeTX, _gazeTY;

    /// <summary>Seconds elapsed (drives breathing and idle sway).</summary>
    public double Time;

    /// <summary>Current mood name.</summary>
    public string Mood { get; private set; } = "neutral";

    /// <summary>Switches the mood she eases toward.</summary>
    /// <param name="mood">Mood name.</param>
    public void SetMood(string mood) { _target = Expression.For(mood); Mood = Expression.Known(mood) ? mood.ToLowerInvariant() : "neutral"; }

    /// <summary>Points her gaze toward a spot, −1..1 on each axis.</summary>
    /// <param name="x">Horizontal.</param>
    /// <param name="y">Vertical.</param>
    public void LookAt(double x, double y) { _gazeTX = Math.Clamp(x, -1, 1); _gazeTY = Math.Clamp(y, -1, 1); }

    /// <summary>The eased amplitude the renderer should draw.</summary>
    public double MouthOpen => _ampSmooth;

    /// <summary>Advances the animation by <paramref name="dt"/> seconds.</summary>
    /// <param name="dt">Delta time in seconds (clamped internally).</param>
    public void Step(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.05);
        Time += dt;
        double k = 1 - Math.Pow(0.001, dt);
        Current = Expression.Lerp(Current, _target, k);
        Amplitude = Math.Clamp(Amplitude, 0, 1);
        _ampSmooth = Amplitude > _ampSmooth ? Lerp(_ampSmooth, Amplitude, 0.6) : Lerp(_ampSmooth, Amplitude, 0.25);
        GazeX = Lerp(GazeX, _gazeTX * 0.4 + Math.Sin(Time * 0.6) * 0.12, 0.08);
        GazeY = Lerp(GazeY, _gazeTY * 0.3 + Math.Sin(Time * 0.5) * 0.08, 0.08);

        if (Time > _nextBlink) { _blinkPhase = 0; _nextBlink = Time + 2 + Random.Shared.NextDouble() * 4; }
        if (_blinkPhase >= 0)
        {
            _blinkPhase += dt * 9;
            Blink = _blinkPhase < 1 ? 1 - _blinkPhase : Math.Min(1, _blinkPhase - 1);
            if (_blinkPhase >= 2) { Blink = 1; _blinkPhase = -1; }
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

/// <summary>Expression parameters per mood (brow, eye, mouth, blush, head tilt, colour hue).</summary>
/// <param name="Brow">Eyebrow height.</param>
/// <param name="BrowAngle">Inner-brow angle (positive = worried/sad, negative = angry).</param>
/// <param name="Eye">Eye openness multiplier.</param>
/// <param name="Smile">Mouth curve, −1 frown … 1 big smile.</param>
/// <param name="Blush">Cheek blush 0..1.</param>
/// <param name="Tilt">Head tilt in radians.</param>
/// <param name="Hue">Accent hue (degrees) for hair/iris/clothes tint.</param>
public readonly record struct Expression(double Brow, double BrowAngle, double Eye, double Smile, double Blush, double Tilt, double Hue)
{
    private static readonly Dictionary<string, Expression> Table = new()
    {
        ["neutral"] = new(0.0, 0.0, 1.0, 0.1, 0.1, 0.0, 318),
        ["happy"] = new(0.1, 0.0, 0.85, 0.9, 0.4, -0.03, 330),
        ["loving"] = new(0.15, 0.1, 0.7, 0.7, 0.75, 0.06, 345),
        ["teasing"] = new(0.2, -0.25, 0.8, 0.6, 0.3, 0.1, 300),
        ["excited"] = new(0.35, 0.05, 1.15, 1.0, 0.55, -0.05, 335),
        ["worried"] = new(0.1, 0.5, 1.05, -0.3, 0.15, 0.04, 310),
        ["annoyed"] = new(-0.3, -0.5, 0.75, -0.4, 0.1, 0.0, 295),
        ["angry"] = new(-0.5, -0.7, 0.9, -0.6, 0.2, 0.0, 355),
        ["sad"] = new(0.05, 0.7, 0.7, -0.7, 0.05, 0.12, 265),
    };

    /// <summary>The expression for a mood name (neutral if unknown).</summary>
    /// <param name="mood">Mood name.</param>
    public static Expression For(string mood) => Table.TryGetValue(mood?.ToLowerInvariant() ?? "", out var e) ? e : Table["neutral"];

    /// <summary>Whether a mood name is recognised.</summary>
    /// <param name="mood">Mood name.</param>
    public static bool Known(string mood) => Table.ContainsKey(mood?.ToLowerInvariant() ?? "");

    /// <summary>Linear blend between two expressions.</summary>
    /// <param name="a">From.</param>
    /// <param name="b">To.</param>
    /// <param name="t">0..1.</param>
    public static Expression Lerp(Expression a, Expression b, double t) => new(
        L(a.Brow, b.Brow, t), L(a.BrowAngle, b.BrowAngle, t), L(a.Eye, b.Eye, t), L(a.Smile, b.Smile, t),
        L(a.Blush, b.Blush, t), L(a.Tilt, b.Tilt, t), L(a.Hue, b.Hue, t));

    private static double L(double x, double y, double t) => x + (y - x) * t;
}
