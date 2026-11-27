using Ilham.Core.Memory;
using Ilham.Core.Profile;

namespace Ilham.Core.Occasions;

/// <summary>An occasion as it falls this year.</summary>
/// <param name="Title">"Your birthday", "Ammi's birthday", "Exam"…</param>
/// <param name="Date">Next date it occurs (today or later).</param>
/// <param name="DaysAway">0 = today.</param>
/// <param name="Turning">For birthdays with a known year: the age being reached.</param>
/// <param name="IsUserBirthday">True for your own birthday.</param>
public sealed record UpcomingOccasion(string Title, DateOnly Date, int DaysAway, int? Turning, bool IsUserBirthday);

/// <summary>Works out which birthdays and events are today or coming up.</summary>
public static class OccasionCalendar
{
    /// <summary>Every saved occasion plus your birthday, sorted by how soon it comes.</summary>
    /// <param name="profile">Your profile (for your birthday).</param>
    /// <param name="occasions">Saved occasions.</param>
    /// <param name="today">Today's local date.</param>
    public static IReadOnlyList<UpcomingOccasion> Upcoming(UserProfile profile, IEnumerable<Occasion> occasions, DateOnly today)
    {
        var list = new List<UpcomingOccasion>();
        if (profile.Birthday is DateOnly b)
            list.Add(Next("Your birthday", b.Month, b.Day, b.Year > 1 ? b.Year : null, today, isUser: true));
        foreach (var o in occasions)
            list.Add(Next(o.Title, o.Month, o.Day, o.Year, today, isUser: false));
        return list.OrderBy(o => o.DaysAway).ThenBy(o => o.Title).ToList();
    }

    /// <summary>
    /// Next occurrence on or after <paramref name="today"/>. A 29 February date is celebrated on
    /// 28 February in non-leap years (so it's never skipped).
    /// </summary>
    public static UpcomingOccasion Next(string title, int month, int day, int? year, DateOnly today, bool isUser)
    {
        DateOnly On(int y) => new(y, month, Math.Min(day, DateTime.DaysInMonth(y, month)));
        DateOnly date = On(today.Year);
        if (date < today) date = On(today.Year + 1);
        int? turning = year is int y0 && y0 < date.Year ? date.Year - y0 : null;
        return new UpcomingOccasion(title, date, date.DayNumber - today.DayNumber, turning, isUser);
    }
}
