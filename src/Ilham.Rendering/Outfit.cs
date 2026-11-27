using SkiaSharp;

namespace Ilham.Rendering;

/// <summary>What Ilham is wearing — the kameez colour, its shade, the dupatta, and the embroidery trim.</summary>
/// <param name="Name">Display name of the outfit.</param>
/// <param name="Kameez">Main kameez colour.</param>
/// <param name="KameezShade">Darker shade for the kameez gradient.</param>
/// <param name="Dupatta">Draped dupatta colour.</param>
/// <param name="Trim">Embroidered neckline colour.</param>
public readonly record struct Outfit(string Name, SKColor Kameez, SKColor KameezShade, SKColor Dupatta, SKColor Trim);

/// <summary>Ilham's wardrobe. Swap outfits by passing one to <see cref="AvatarRenderer.Draw"/>.</summary>
public static class Outfits
{
    private static SKColor C(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Mustard kameez with a cream dupatta — the default, styled after the reference image.</summary>
    public static readonly Outfit Mustard = new("Mustard kameez", C(0xe0a533), C(0xc68c22), C(0xf0e6cf), C(0xf2d99a));

    /// <summary>Deep teal with a gold dupatta.</summary>
    public static readonly Outfit Teal = new("Teal & gold", C(0x1f7a74), C(0x155a55), C(0xe9c877), C(0xf0dd9e));

    /// <summary>Maroon with a blush-pink dupatta.</summary>
    public static readonly Outfit Maroon = new("Maroon", C(0x8e2b3f), C(0x6c1d2d), C(0xe9c3cc), C(0xf1cfa0));

    /// <summary>Royal blue with silver trim.</summary>
    public static readonly Outfit RoyalBlue = new("Royal blue", C(0x2f4ba0), C(0x22377a), C(0xcdd6ea), C(0xd9def0));

    /// <summary>Emerald green with a cream dupatta.</summary>
    public static readonly Outfit Emerald = new("Emerald", C(0x1f7a4d), C(0x155a39), C(0xede6d2), C(0xf2e0a8));

    /// <summary>Rose pink with a soft gold dupatta.</summary>
    public static readonly Outfit Rose = new("Rose pink", C(0xd96a93), C(0xb84f77), C(0xf3e4d4), C(0xf6ddb0));

    /// <summary>Charcoal black formal with silver embroidery.</summary>
    public static readonly Outfit Black = new("Black formal", C(0x2b2b33), C(0x1b1b21), C(0xb9bcc9), C(0xcfd3df));

    /// <summary>Lavender with a cream dupatta.</summary>
    public static readonly Outfit Lavender = new("Lavender", C(0x8a6fc4), C(0x6e55a4), C(0xefe7d6), C(0xe8dcf2));

    /// <summary>All outfits, in order, for pickers and the model sheet.</summary>
    public static readonly IReadOnlyList<Outfit> All = [Mustard, Teal, Maroon, RoyalBlue, Emerald, Rose, Black, Lavender];

    /// <summary>Finds an outfit by name (case-insensitive); falls back to <see cref="Mustard"/>.</summary>
    /// <param name="name">Outfit name.</param>
    public static Outfit ByName(string? name) =>
        All.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase), Mustard);
}
