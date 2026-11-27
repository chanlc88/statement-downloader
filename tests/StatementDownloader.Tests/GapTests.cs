using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.App;
using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public class GapFinderTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>Monthly on the 15th, for the given months of 2021 (1-based).</summary>
    private static DateOnly[] Months(params int[] months) => months.Select(m => D(2021, m, 15)).ToArray();

    [Fact]
    public void Finds_the_months_between_documents()
    {
        var g = GapFinder.Find(Months(1, 2, 3, 8, 9, 10))!;
        Assert.True(g.Monthly);
        Assert.Equal(["2021-04", "2021-05", "2021-06", "2021-07"], g.Slots.Select(s => s.Key));
        Assert.Equal(4, g.MissingCount);
        // Each window stays clear of the documents either side.
        Assert.All(g.Slots, s => Assert.True(s.Window.From > D(2021, 3, 15) && s.Window.To < D(2021, 8, 15)));
        Assert.True(g.Slots[0].Window.Contains(D(2021, 4, 12)));
    }

    [Fact]
    public void A_jittery_monthly_history_has_no_gaps()
    {
        DateOnly[] billed = [D(2026, 4, 17), D(2026, 5, 19), D(2026, 6, 17), D(2026, 7, 21), D(2026, 8, 19), D(2026, 9, 18), D(2026, 10, 19)];
        Assert.Empty(GapFinder.Find(billed)!.Slots);
    }

    [Fact]
    public void Long_gaps_dont_stretch_the_cycle()
    {
        // Years apart, then monthly: the cycle is still a month, so the years between are missing.
        DateOnly[] dates = [D(2019, 6, 15), D(2023, 1, 15), D(2023, 2, 15), D(2023, 3, 15), D(2023, 4, 15)];
        var g = GapFinder.Find(dates)!;
        Assert.True(g.Monthly);
        Assert.Equal(42, g.MissingCount);   // Jul 2019 .. Dec 2022
        Assert.Equal("2019-07", g.Slots[0].Key);
        Assert.Equal("2022-12", g.Slots[^1].Key);
    }

    [Fact]
    public void Months_before_the_oldest_count_once_the_start_is_given()
    {
        var dates = Months(4, 5, 6);
        Assert.Empty(GapFinder.Find(dates)!.Slots);
        var g = GapFinder.Find(dates, expectFrom: D(2021, 1, 1))!;
        Assert.Equal(["2021-01", "2021-02", "2021-03"], g.Slots.Select(s => s.Key));
    }

    [Fact]
    public void Marks_change_what_counts_and_what_a_fill_wants()
    {
        var marks = new Dictionary<string, GapMark> { ["2021-04"] = GapMark.GetMyself, ["2021-05"] = GapMark.NoStatement, ["2021-06"] = GapMark.NotOnline };
        var g = GapFinder.Find([.. Months(1, 2, 3, 8), D(2022, 1, 15), D(2022, 3, 15)], marks: marks)!;
        Assert.Equal(GapMark.GetMyself, g.Slots.Single(s => s.Key == "2021-04").Mark);
        Assert.DoesNotContain(g.Shown, s => s.Key == "2021-05");
        // Jun (not online) and Jul 2021, Sep-Dec 2021 and Feb 2022 count; Apr and May don't.
        Assert.Equal(7, g.MissingCount);
        Assert.Equal(["2021-06", "2021-07", "2021-09", "2021-10", "2021-11", "2021-12"], g.Wanted(2021).Select(s => s.Key));
        Assert.Equal(["2022-02"], g.Wanted(2022).Select(s => s.Key));
    }

    [Fact]
    public void Needs_three_documents() => Assert.Null(GapFinder.Find(Months(1, 9)));

    [Fact]
    public void Quarterly_bills_step_by_days()
    {
        DateOnly[] dates = [D(2021, 1, 10), D(2021, 4, 11), D(2021, 7, 10), D(2022, 1, 9)];
        var g = GapFinder.Find(dates)!;
        Assert.False(g.Monthly);
        var gap = Assert.Single(g.Slots);
        Assert.True(gap.Expected > D(2021, 9, 20) && gap.Expected < D(2021, 11, 1));
    }

    [Fact]
    public void Ranges_merge_neighbouring_windows()
    {
        var g = GapFinder.Find(Months(1, 2, 6, 7, 10, 11))!;
        var ranges = GapFinder.Ranges(g.Slots);
        Assert.Equal(2, ranges.Count);                         // Mar–May, Aug–Sep
        Assert.True(ranges[0].Contains(D(2021, 4, 15)) && !ranges[0].Contains(D(2021, 6, 15)));
        Assert.True(ranges[1].Contains(D(2021, 9, 1)));
    }
}

/// <summary>Filling gaps through the real runner, with a site that lists what it's given.</summary>
public sealed class FillGapsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sd-fill-").FullName;

    public FillGapsTests() => Environment.SetEnvironmentVariable("SD_TEST_LOGIN", """{"username":"u","password":"p"}""");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A site whose list holds the given dates (each downloads as a small file), and remembers what it was asked for.</summary>
    private sealed class ListingSite(params DateOnly[] listed) : ISiteFetcher
    {
        public string SiteId => "dekalb-water";
        public string DisplayName => "Test";
        public SiteContext? Last { get; private set; }

        public Task<SiteResult> RunAsync(SiteContext ctx, CancellationToken ct = default)
        {
            Last = ctx;
            var want = listed.Where(d => ctx.Backfill is { } r ? r.Any(x => x.Contains(d)) : ctx.Known.All(k => k.DocumentDate < d));
            var docs = want.Select(d =>
            {
                var path = Path.Combine(ctx.WorkDir, $"{d:yyyy-MM-dd}.pdf");
                File.WriteAllText(path, $"statement {d}");
                return new FoundDocument { TempPath = path, DocumentDate = d, Amount = 10 };
            }).ToList();
            return Task.FromResult(new SiteResult { SignedIn = true, Documents = docs, OldestListed = listed.Min() });
        }
    }

    private (AppServices S, TickRunner Ticks) Build(ListingSite site, bool withAction = false)
    {
        var config = Path.Combine(_dir, "config.yaml");
        var echo = OperatingSystem.IsWindows() ? "cmd\n        args: '/c echo ran'" : "sh\n        args: '-c \"echo ran\"'";
        File.WriteAllText(config, $"""
            documentsRoot: "{_dir.Replace(Path.DirectorySeparatorChar, '/')}/docs"
            accounts:
              - id: water
                site: dekalb-water
                credential: env:SD_TEST_LOGIN
                folder: Bills
                fileName: "{"{date:yyyy-MM-dd}"}-Water.pdf"
                review: true
            {(withAction ? $"    actions:\n      - type: run-command\n        command: {echo}\n" : "")}
            """);
        var s = AppServices.Build(new AppPaths(config, Path.Combine(_dir, "data")), NullLoggerFactory.Instance);
        var sites = new Dictionary<string, ISiteFetcher>(StringComparer.OrdinalIgnoreCase) { [site.SiteId] = site };
        var runner = new AccountRunner(s.Config, s.Paths, sites, s.Secrets, s.Auth, s.Documents, s.Schedule, s.Actions, s.Notify, s.Links, TimeProvider.System, NullLoggerFactory.Instance);
        var ticks = new TickRunner(s.Config, s.Paths, runner, s.Documents, s.Schedule, s.Auth, s.Notify, s.Links, TimeProvider.System, TimeZoneInfo.Local, NullLogger<TickRunner>.Instance);
        return (s, ticks);
    }

    private static async Task Have(AppServices s, params DateOnly[] dates)
    {
        foreach (var d in dates)
            await s.Documents.AddAsync(new DocumentRecord { AccountId = "water", DocumentDate = d, Path = $"{d}.pdf", Imported = true });
    }

    private static DateOnly M(int y, int m) => new(y, m, 15);

    [Fact]
    public async Task Fills_what_the_site_has_and_marks_the_rest_not_online()
    {
        // On record: Jan–Mar and Aug 2021. The site lists Mar 2021 onwards, so Apr–Jul can be found.
        var site = new ListingSite(M(2021, 3), M(2021, 5), M(2021, 6), M(2021, 8));
        var (s, ticks) = Build(site);
        await Have(s, M(2020, 10), M(2020, 11), M(2021, 1), M(2021, 3), M(2021, 8));

        var outcome = await ticks.FillGapsAsync(s.Account("water"), year: null, interactive: false);

        Assert.Equal(2, outcome.Found);           // May and Jun
        Assert.Equal(6, outcome.LookedFor);       // Dec 2020, Feb, Apr, May, Jun, Jul 2021
        var docs = await s.Documents.GetAsync("water");
        var found = docs.Where(d => d.Backfilled).ToList();
        Assert.Equal([M(2021, 5), M(2021, 6)], found.Select(d => d.DocumentDate).Order());
        // Old documents don't wait for review, and nothing new arrived for the schedule to count.
        Assert.All(found, d => Assert.Equal(ReviewState.None, d.Review));
        Assert.Null((await s.Schedule.GetAsync("water")).LastFullCheckUtc);

        var gaps = (await ticks.GapsAsync(s.Account("water")))!;
        Assert.All(gaps.Slots, g => Assert.Equal(GapMark.NotOnline, g.Mark));
        Assert.Contains("back to March 2021", outcome.Message);
    }

    [Fact]
    public async Task One_year_only_asks_for_that_years_ranges()
    {
        var site = new ListingSite(M(2020, 12), M(2021, 2));
        var (s, ticks) = Build(site);
        await Have(s, M(2020, 10), M(2020, 11), M(2021, 1), M(2021, 3));

        var outcome = await ticks.FillGapsAsync(s.Account("water"), year: 2021, interactive: false);

        Assert.Equal(1, outcome.Found);
        Assert.All(site.Last!.Backfill!, r => Assert.Equal(2021, r.From.Year));
        var gaps = (await ticks.GapsAsync(s.Account("water")))!;
        // December 2020 wasn't looked for, so it's still plainly missing.
        Assert.Equal(GapMark.None, gaps.Slots.Single(g => g.Key == "2020-12").Mark);
    }

    [Fact]
    public async Task Actions_skip_backfilled_documents_until_asked()
    {
        var site = new ListingSite(M(2021, 2));
        var (s, ticks) = Build(site, withAction: true);
        await Have(s, M(2020, 12), M(2021, 1), M(2021, 3));

        await ticks.FillGapsAsync(s.Account("water"), year: null, interactive: false);
        var doc = (await s.Documents.GetAsync("water")).Single(d => d.Backfilled);
        Assert.Empty(doc.Actions);

        Assert.Equal(0, await ticks.RunActionsOnAsync(doc.Id));
        doc = (await s.Documents.GetAsync("water")).Single(d => d.Id == doc.Id);
        Assert.True(doc.ActionsRequested);
        Assert.True(Assert.Single(doc.Actions).Succeeded);
    }

    [Fact]
    public async Task Nothing_missing_skips_the_site()
    {
        var site = new ListingSite(M(2021, 2));
        var (s, ticks) = Build(site);
        await Have(s, M(2021, 1), M(2021, 2), M(2021, 3));
        var outcome = await ticks.FillGapsAsync(s.Account("water"), year: null, interactive: false);
        Assert.Equal(0, outcome.LookedFor);
        Assert.Null(site.Last);
    }
}
