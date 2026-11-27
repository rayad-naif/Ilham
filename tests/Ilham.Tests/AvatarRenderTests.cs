using Ilham.Rendering;
using SkiaSharp;

namespace Ilham.Tests;

public class AvatarRenderTests
{
    private static byte[] RenderMood(string mood, double mouthOpen = 0)
    {
        var s = new AvatarState();
        s.SetMood(mood);
        s.Amplitude = mouthOpen;
        for (int i = 0; i < 120; i++) s.Step(0.016); // settle the eased expression (~2s)
        using var bmp = new SKBitmap(420, 420);
        using (var c = new SKCanvas(bmp)) AvatarRenderer.Draw(c, 420, s, new SKColor(0x14, 0x12, 0x21));
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 92).ToArray();
    }

    [Theory]
    [InlineData("neutral")] [InlineData("happy")] [InlineData("loving")] [InlineData("teasing")]
    [InlineData("excited")] [InlineData("worried")] [InlineData("annoyed")] [InlineData("angry")] [InlineData("sad")]
    public void EveryMood_RendersAPng(string mood)
    {
        byte[] png = RenderMood(mood);
        Assert.True(png.Length > 1000, "should produce a real PNG");
        Assert.Equal(0x89, png[0]); // PNG magic
        // Save for manual inspection when running locally.
        string dir = Environment.GetEnvironmentVariable("ILHAM_FACE_OUT");
        if (dir is not null) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, $"face-{mood}.png"), png); }
    }

    [Fact]
    public void MouthOpens_WhenSpeaking()
    {
        // A rough check that amplitude changes the pixels (mouth area differs closed vs open).
        Assert.NotEqual(Convert.ToBase64String(RenderMood("happy", 0)), Convert.ToBase64String(RenderMood("happy", 1)));
    }

    [Fact]
    public void Outfits_ChangeTheRender_AndByNameResolves()
    {
        var s = new AvatarState(); s.SetMood("happy");
        for (int i = 0; i < 120; i++) s.Step(0.016);
        byte[] Render(Outfit fit) { using var b = new SKBitmap(300, 300); using (var c = new SKCanvas(b)) AvatarRenderer.Draw(c, 300, s, new SKColor(20, 18, 33), fit); using var img = SKImage.FromBitmap(b); return img.Encode(SKEncodedImageFormat.Png, 90).ToArray(); }
        Assert.Equal(8, Outfits.All.Count);
        Assert.NotEqual(Convert.ToBase64String(Render(Outfits.Mustard)), Convert.ToBase64String(Render(Outfits.Emerald)));
        Assert.Equal("Royal blue", Outfits.ByName("royal blue").Name);
        Assert.Equal(Outfits.Mustard, Outfits.ByName("does-not-exist")); // safe fallback
    }

    [Fact]
    public void State_EasesTowardMood_AndBlinks()
    {
        var s = new AvatarState();
        s.SetMood("angry");
        double startSmile = s.Current.Smile;
        for (int i = 0; i < 200; i++) s.Step(0.016);
        Assert.True(s.Current.Smile < startSmile); // angry frown pulled the mouth down
        Assert.InRange(s.Blink, 0, 1);
    }
}
