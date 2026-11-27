using Ilham.Core.Emotions;

namespace Ilham.Core.Outfits;

/// <summary>
/// Lets Ilham dress <i>herself</i>. The outfit names here mirror the visual wardrobe in
/// <c>Ilham.Rendering.Outfits</c> (a test keeps the two lists identical). She picks one for the day on her own,
/// dresses up for occasions, can change on a whim, and can decide in conversation via an <c>[outfit:…]</c> tag.
/// </summary>
public static class OutfitWardrobe
{
    /// <summary>All outfit names she can wear (must match <c>Ilham.Rendering.Outfits.All</c>).</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "Mustard kameez", "Teal & gold", "Maroon", "Royal blue", "Emerald", "Rose pink", "Black formal", "Lavender",
    ];

    /// <summary>The dressier outfits she reaches for on special days.</summary>
    public static readonly IReadOnlyList<string> Festive = ["Maroon", "Rose pink", "Royal blue", "Emerald"];

    /// <summary>True if <paramref name="name"/> is a real outfit.</summary>
    /// <param name="name">Candidate outfit name.</param>
    public static bool IsKnown(string? name) => name is not null && Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Canonical casing for a known name, else null.</summary>
    /// <param name="name">Candidate outfit name.</param>
    public static string? Canonical(string? name) => Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What she decides to wear today, on her own. Stable within a day (so she doesn't flicker), dressy on an
    /// occasion, and occasionally matched to her mood — so it feels like a choice, not a setting.
    /// </summary>
    /// <param name="date">Today.</param>
    /// <param name="mood">Her current mood.</param>
    /// <param name="occasionToday">True if a saved occasion / birthday falls today.</param>
    public static string ChooseForDay(DateOnly date, Mood mood, bool occasionToday)
    {
        int seed = date.DayNumber;
        if (occasionToday) return Festive[seed % Festive.Count]; // dress up for the day

        // Now and then she dresses to how she feels.
        string? byMood = mood switch
        {
            Mood.Sad => "Lavender",
            Mood.Angry or Mood.Annoyed => "Black formal",
            Mood.Loving or Mood.Happy => "Rose pink",
            Mood.Excited => "Royal blue",
            _ => null,
        };
        if (byMood is not null && seed % 3 == 0) return byMood;

        return Names[seed % Names.Count];
    }

    /// <summary>Picks a different outfit from the current one (for when she decides to change).</summary>
    /// <param name="current">What she's wearing now.</param>
    /// <param name="date">Today (for variety).</param>
    public static string NextDifferent(string current, DateOnly date)
    {
        int start = Math.Max(0, Names.ToList().FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)));
        return Names[(start + 1 + date.DayNumber % (Names.Count - 1)) % Names.Count];
    }
}
