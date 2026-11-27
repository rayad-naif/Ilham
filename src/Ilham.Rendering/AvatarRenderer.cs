using SkiaSharp;

namespace Ilham.Rendering;

/// <summary>
/// Draws Ilham's face onto a Skia canvas from an <see cref="AvatarState"/>. Stateless and cross-platform:
/// the same code renders inside the Avalonia desktop app (Windows/Linux/macOS) and into a PNG in tests.
/// It's a native port of the browser canvas face — blinking, breathing, lip-sync and nine moods — so there
/// is no web view anywhere.
/// </summary>
public static class AvatarRenderer
{
    /// <summary>Renders the face, centred, filling a square of side <paramref name="size"/>.</summary>
    /// <param name="canvas">Target canvas.</param>
    /// <param name="size">Side length in pixels.</param>
    /// <param name="s">Animation state.</param>
    /// <param name="background">Optional background fill (null = transparent, for the floating pet).</param>
    public static void Draw(SKCanvas canvas, float size, AvatarState s, SKColor? background = null)
    {
        if (background is { } bg) canvas.Clear(bg); else canvas.Clear(SKColors.Transparent);
        var e = s.Current;
        float cx = size / 2, cy = size / 2;
        float breathe = (float)(Math.Sin(s.Time * 1.1) * size * 0.004);
        float tilt = (float)(e.Tilt + Math.Sin(s.Time * 0.4) * 0.015);

        canvas.Save();
        canvas.Translate(cx, cy + breathe);
        canvas.RotateRadians(tilt);

        // Warm medium skin tone, long dark hair, mustard kameez + cream dupatta — styled after the reference.
        var skin = new SKColor(0xe7, 0xb2, 0x8c);
        var skinShade = new SKColor(0xd6, 0x9d, 0x75);
        var hair = Hsl(272, 0.28, 0.12);
        var hairLit = Hsl(280, 0.26, 0.20);
        var kameez = new SKColor(0xe0, 0xa5, 0x33);       // mustard/gold
        var kameezShade = new SKColor(0xc6, 0x8c, 0x22);
        var dupatta = new SKColor(0xf0, 0xe6, 0xcf);       // cream
        float fw = size * 0.30f, fh = size * 0.38f;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

        // Long hair falling well past the shoulders, behind everything.
        fill.Color = hair;
        canvas.DrawOval(0, fh * 0.45f, fw * 1.25f, fh * 1.55f, fill); // long fall down the back/sides
        canvas.DrawOval(0, -size * 0.02f, fw * 1.42f, fh * 1.4f, fill); // crown

        // Neck.
        fill.Color = skinShade;
        canvas.DrawRect(-fw * 0.42f, fh * 0.55f, fw * 0.84f, fh * 0.7f, fill);

        // Shoulders / kameez (mustard), with a cream dupatta draped across.
        using (var body = new SKPath())
        {
            body.MoveTo(-fw * 1.7f, fh * 2.3f);
            body.CubicTo(-fw * 1.5f, fh * 1.15f, -fw * 0.5f, fh * 0.95f, 0, fh * 0.98f);
            body.CubicTo(fw * 0.5f, fh * 0.95f, fw * 1.5f, fh * 1.15f, fw * 1.7f, fh * 2.3f);
            body.Close();
            using var kg = SKShader.CreateLinearGradient(new SKPoint(0, fh), new SKPoint(0, fh * 2.3f), new SKColor[] { kameez, kameezShade }, (float[])null, SKShaderTileMode.Clamp);
            fill.Shader = kg; canvas.DrawPath(body, fill); fill.Shader = null;
        }
        // Embroidered neckline hint.
        stroke.Color = new SKColor(0xf2, 0xd9, 0x9a); stroke.StrokeWidth = size * 0.010f;
        using (var neckline = new SKPath()) { neckline.MoveTo(-fw * 0.34f, fh * 1.08f); neckline.QuadTo(0, fh * 1.42f, fw * 0.34f, fh * 1.08f); canvas.DrawPath(neckline, stroke); }
        // Cream dupatta draped over her left shoulder, across the chest.
        fill.Color = dupatta;
        using (var dup = new SKPath())
        {
            dup.MoveTo(-fw * 1.55f, fh * 2.3f);
            dup.CubicTo(-fw * 1.2f, fh * 1.2f, -fw * 0.3f, fh * 1.05f, fw * 0.6f, fh * 1.35f);
            dup.CubicTo(fw * 0.2f, fh * 1.5f, -fw * 0.7f, fh * 1.5f, -fw * 1.1f, fh * 2.3f);
            dup.Close(); canvas.DrawPath(dup, fill);
        }

        // Face with a soft vertical gradient
        using (var face = new SKPath())
        {
            face.MoveTo(0, -fh);
            face.CubicTo(fw, -fh, fw * 1.05f, fh * 0.35f, fw * 0.5f, fh * 0.9f);
            face.CubicTo(fw * 0.2f, fh * 1.12f, -fw * 0.2f, fh * 1.12f, -fw * 0.5f, fh * 0.9f);
            face.CubicTo(-fw * 1.05f, fh * 0.35f, -fw, -fh, 0, -fh);
            face.Close();
            using var grad = SKShader.CreateLinearGradient(new SKPoint(0, -fh), new SKPoint(0, fh), new SKColor[] { skin, skinShade }, (float[])null, SKShaderTileMode.Clamp);
            fill.Shader = grad; canvas.DrawPath(face, fill); fill.Shader = null;
        }

        // Blush
        if (e.Blush > 0.05)
        {
            fill.Color = new SKColor(255, 120, 150, (byte)(e.Blush * 0.5 * 255));
            foreach (int sgn in new[] { -1, 1 }) canvas.DrawOval(sgn * fw * 0.55f, fh * 0.28f, fw * 0.22f, fh * 0.12f, fill);
        }

        float eyeY = -fh * 0.02f, eyeDx = fw * 0.46f, eyeW = fw * 0.3f;
        float eyeH = (float)(fh * 0.17f * e.Eye * s.Blink);

        // Eyebrows
        stroke.Color = hairLit; stroke.StrokeWidth = size * 0.012f;
        foreach (int sgn in new[] { -1, 1 })
        {
            float by = (float)(eyeY - fh * 0.26f - e.Brow * fh * 0.1);
            using var brow = new SKPath();
            brow.MoveTo(sgn * (eyeDx - eyeW * 0.8f), (float)(by + sgn * e.BrowAngle * fh * 0.06));
            brow.QuadTo(sgn * eyeDx, (float)(by - fh * 0.03f - sgn * e.BrowAngle * fh * 0.04), sgn * (eyeDx + eyeW * 0.8f), (float)(by - sgn * e.BrowAngle * fh * 0.02));
            canvas.DrawPath(brow, stroke);
        }

        // Eyes
        foreach (int sgn in new[] { -1, 1 })
        {
            float ex = sgn * eyeDx;
            canvas.Save(); canvas.Translate(ex, eyeY);
            fill.Color = new SKColor(0xfb, 0xf7, 0xf5);
            canvas.DrawOval(0, 0, eyeW, Math.Max(0.5f, eyeH), fill);
            if (eyeH > eyeW * 0.25f)
            {
                float ix = (float)(s.GazeX * eyeW * 0.35), iy = (float)(s.GazeY * eyeH * 0.4);
                canvas.Save();
                using (var clip = new SKPath()) { clip.AddOval(new SKRect(-eyeW, -eyeH, eyeW, eyeH)); canvas.ClipPath(clip, antialias: true); }
                fill.Color = Hsl(e.Hue - 10, 0.45, 0.32); canvas.DrawCircle(ix, iy, eyeW * 0.62f, fill);
                fill.Color = new SKColor(0x1a, 0x10, 0x20); canvas.DrawCircle(ix, iy, eyeW * 0.32f, fill);
                fill.Color = new SKColor(255, 255, 255, 200); canvas.DrawCircle(ix - eyeW * 0.2f, iy - eyeH * 0.3f, eyeW * 0.12f, fill);
                canvas.Restore();
            }
            stroke.Color = new SKColor(0x2a, 0x18, 0x30); stroke.StrokeWidth = size * 0.007f;
            using (var lash = new SKPath()) { lash.AddArc(new SKRect(-eyeW, -Math.Max(0.5f, eyeH), eyeW, Math.Max(0.5f, eyeH)), 180, 180); canvas.DrawPath(lash, stroke); }
            canvas.Restore();
        }

        // Nose
        stroke.Color = skinShade; stroke.StrokeWidth = size * 0.006f;
        using (var nose = new SKPath()) { nose.MoveTo(-fw * 0.03f, fh * 0.12f); nose.QuadTo(fw * 0.06f, fh * 0.26f, -fw * 0.04f, fh * 0.3f); canvas.DrawPath(nose, stroke); }

        // Mouth
        float my = fh * 0.56f, mw = fw * 0.5f;
        float open = (float)(Math.Max(0, s.MouthOpen) * fh * 0.22 + Math.Max(0, -e.Smile) * fh * 0.02);
        float curve = (float)(e.Smile * fh * 0.14);
        fill.Color = new SKColor(0x9e, 0x3b, 0x54);
        using (var mouth = new SKPath())
        {
            mouth.MoveTo(-mw, my);
            mouth.QuadTo(0, my + curve, mw, my);
            mouth.QuadTo(0, my + curve + open + Math.Abs(curve) * 0.4f, -mw, my);
            mouth.Close(); canvas.DrawPath(mouth, fill);
        }
        if (open > fh * 0.03f)
        {
            fill.Color = new SKColor(0xff, 0xf4, 0xf6);
            using var teeth = new SKPath();
            teeth.MoveTo(-mw * 0.7f, my); teeth.QuadTo(0, my + curve * 0.6f, mw * 0.7f, my);
            teeth.QuadTo(0, my + curve * 0.6f + open * 0.3f, -mw * 0.7f, my); teeth.Close(); canvas.DrawPath(teeth, fill);
        }

        // Long front locks: centre-parted fringe that continues into hair falling over the shoulders and chest.
        using (var hg = SKShader.CreateLinearGradient(new SKPoint(0, -fh * 1.3f), new SKPoint(0, fh * 1.8f), new SKColor[] { hairLit, hair }, (float[])null, SKShaderTileMode.Clamp))
        {
            fill.Shader = hg;
            foreach (int sgn in new[] { -1, 1 })
            {
                using var fr = new SKPath();
                fr.MoveTo(sgn * fw * 0.04f, -fh * 1.1f);
                fr.CubicTo(sgn * fw * 0.9f, -fh * 1.15f, sgn * fw * 1.3f, -fh * 0.1f, sgn * fw * 1.02f, fh * 0.75f);
                fr.CubicTo(sgn * fw * 1.15f, fh * 1.6f, sgn * fw * 0.9f, fh * 2.2f, sgn * fw * 0.7f, fh * 2.3f); // long lock down the chest
                fr.LineTo(sgn * fw * 0.3f, fh * 2.3f);
                fr.CubicTo(sgn * fw * 0.5f, fh * 1.4f, sgn * fw * 0.55f, fh * 0.4f, sgn * fw * 0.5f, -fh * 0.72f);
                fr.CubicTo(sgn * fw * 0.3f, -fh * 0.82f, sgn * fw * 0.14f, -fh * 0.86f, sgn * fw * 0.04f, -fh * 0.78f);
                fr.Close(); canvas.DrawPath(fr, fill);
            }
            fill.Shader = null;
        }
        stroke.Color = Hsl(295, 0.22, 0.30); stroke.StrokeWidth = size * 0.006f;
        foreach (int sgn in new[] { -1, 1 })
        {
            using var strand = new SKPath();
            strand.MoveTo(sgn * fw * 0.06f, -fh * 1.0f);
            strand.QuadTo(sgn * fw * 0.5f, -fh * 0.95f, sgn * fw * 0.66f, -fh * 0.5f);
            canvas.DrawPath(strand, stroke);
        }

        canvas.Restore();
    }

    /// <summary>HSL → SKColor (h in degrees, s/l in 0..1).</summary>
    private static SKColor Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double R = Hue(p, q, h + 1.0 / 3), G = Hue(p, q, h), B = Hue(p, q, h - 1.0 / 3);
        return new SKColor((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
    }
    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1; if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
