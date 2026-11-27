namespace StatementDownloader.Core;

/// <summary>A span of document dates, both ends included.</summary>
public sealed record DateRange(DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly d) => d >= From && d <= To;
}

/// <summary>What the person (or the last fill) said about a missing document.</summary>
public enum GapMark
{
    /// <summary>Missing, nothing known yet.</summary>
    None,
    /// <summary>A fill looked for it and the site didn't have it.</summary>
    NotOnline,
    /// <summary>The person will get it some other way; still shown, no longer counted.</summary>
    GetMyself,
    /// <summary>There was no document for this period (account not open, closed, no activity); hidden.</summary>
    NoStatement,
}

/// <summary>One document that should be on record but isn't.</summary>
/// <param name="Key">Stable name for the period (<c>yyyy-MM</c> for monthly accounts, otherwise the expected date).</param>
/// <param name="Expected">About when its date would be.</param>
/// <param name="Window">The listed dates that count as this document when filling.</param>
public sealed record GapSlot(string Key, DateOnly Expected, DateRange Window, GapMark Mark)
{
    /// <summary>Still asks for attention: not yet marked by the person.</summary>
    public bool Counts => Mark is GapMark.None or GapMark.NotOnline;
    /// <summary>A fill looks for it.</summary>
    public bool Wanted => Mark is GapMark.None or GapMark.NotOnline;
}

public sealed record GapReport(int IntervalDays, bool Monthly, IReadOnlyList<GapSlot> Slots)
{
    /// <summary>Slots shown to the person (not the ones marked as never existing).</summary>
    public IEnumerable<GapSlot> Shown => Slots.Where(s => s.Mark != GapMark.NoStatement);
    public int MissingCount => Slots.Count(s => s.Counts);

    /// <summary>The slots a fill looks for, optionally only one year's.</summary>
    public IReadOnlyList<GapSlot> Wanted(int? year = null) =>
        Slots.Where(s => s.Wanted && (year is null || s.Expected.Year == year)).ToList();
}

/// <summary>
/// Finds documents missing from an account's history: between documents on record, and before the
/// oldest one back to the date the person says documents start. Documents after the newest one
/// are the schedule's business, not a gap.
/// </summary>
public static class GapFinder
{
    public const int MinimumDocuments = 3;

    public static GapReport? Find(IEnumerable<DateOnly> documentDates, DateOnly? expectFrom = null, IReadOnlyDictionary<string, GapMark>? marks = null)
    {
        var dates = documentDates.Distinct().Order().ToList();
        if (dates.Count < MinimumDocuments) return null;
        var interval = Cycle(dates);
        if (interval <= 0) return null;

        var monthly = interval is >= 26 and <= 34;
        var half = interval / 2;
        var margin = Math.Max(2, interval / 6);
        DateOnly Step(DateOnly d, int i) => monthly ? d.AddMonths(i) : d.AddDays(i * interval);
        string Key(DateOnly d) => monthly ? d.ToString("yyyy-MM") : d.ToString("yyyy-MM-dd");

        var slots = new List<GapSlot>();
        void Add(DateOnly expected, DateOnly lo, DateOnly hi)
        {
            var key = Key(expected);
            if (slots.Any(s => s.Key == key)) return;
            var from = Max(expected.AddDays(-half), lo);
            var to = Min(expected.AddDays(half), hi);
            if (from > to) return;
            slots.Add(new(key, expected, new(from, to), marks?.GetValueOrDefault(key) ?? GapMark.None));
        }

        // Before the oldest, back to where the person says documents start.
        var oldest = dates[0];
        if (expectFrom is { } start && start < oldest)
        {
            var before = new List<DateOnly>();
            for (var i = 1; i < 1000; i++)
            {
                var e = Step(oldest, -i);
                if (e < start) break;
                before.Add(e);
            }
            foreach (var e in before.AsEnumerable().Reverse())
                Add(e, start.AddDays(-half), oldest.AddDays(-margin));
        }

        // Between documents on record.
        foreach (var (a, b) in dates.Zip(dates.Skip(1)))
            for (var i = 1; i < 1000; i++)
            {
                var e = Step(a, i);
                if (e > b.AddDays(-half)) break;
                Add(e, a.AddDays(margin), b.AddDays(-margin));
            }

        return new(interval, monthly, slots.OrderBy(s => s.Expected).ToList());
    }

    /// <summary>
    /// The usual time between documents: the lower quartile of the intervals, so long gaps in the
    /// history don't stretch it.
    /// </summary>
    public static int Cycle(IReadOnlyList<DateOnly> sortedDates)
    {
        var intervals = sortedDates.Zip(sortedDates.Skip(1), (a, b) => b.DayNumber - a.DayNumber)
            // Two documents days apart (a corrected statement) say nothing about the cycle.
            .Where(i => i >= 7).Order().ToList();
        if (intervals.Count == 0) return 0;
        return intervals[(intervals.Count - 1) / 4];
    }

    /// <summary>The listed dates a fill downloads: the windows of the given slots, merged.</summary>
    public static IReadOnlyList<DateRange> Ranges(IEnumerable<GapSlot> slots)
    {
        var merged = new List<DateRange>();
        foreach (var w in slots.Select(s => s.Window).OrderBy(w => w.From))
        {
            if (merged.Count > 0 && w.From <= merged[^1].To.AddDays(1))
                merged[^1] = merged[^1] with { To = Max(merged[^1].To, w.To) };
            else merged.Add(w);
        }
        return merged;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
