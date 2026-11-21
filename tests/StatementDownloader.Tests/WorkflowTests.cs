using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.App;
using StatementDownloader.Core;

namespace StatementDownloader.Tests;

/// <summary>Approval, action identity and the shared run lock, through the real services.</summary>
public sealed class WorkflowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sd-flow-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>An action that prints a line naming itself, so its result says which one ran.</summary>
    private static string Echo(string text, string? id = null)
    {
        var (command, args) = OperatingSystem.IsWindows() ? ("cmd", $"/c echo {text}") : ("sh", $"-c \"echo {text}\"");
        return $"""
                  - type: run-command
            {(id is null ? "" : $"        id: {id}\n")}        command: {command}
                    args: '{args}'

            """;
    }

    /// <summary>Services over a shared data folder, with the given actions configured (config can change between calls).</summary>
    private AppServices Services(params string[] actions)
    {
        var config = Path.Combine(_dir, "config.yaml");
        File.WriteAllText(config, $$"""
            documentsRoot: "{{_dir.Replace(Path.DirectorySeparatorChar, '/')}}/docs"
            accounts:
              - id: water
                site: dekalb-water
                credential: env:NONE
                folder: Bills
                fileName: "{date:yyyy-MM-dd}-Water.pdf"
                actions:

            """ + string.Concat(actions));
        return AppServices.Build(new AppPaths(config, Path.Combine(_dir, "data")), NullLoggerFactory.Instance);
    }

    private static async Task<DocumentRecord> AddDoc(AppServices s, ReviewState review = ReviewState.None, DateTimeOffset? seen = null, params ActionRecord[] done)
    {
        var doc = new DocumentRecord
        {
            AccountId = "water", DocumentDate = new(2026, 11, 18), Amount = 73.21m, Path = "bill.pdf",
            Review = review, FirstSeenUtc = seen ?? DateTimeOffset.UtcNow, Actions = [.. done],
        };
        await s.Documents.AddAsync(doc);
        return doc;
    }

    private static async Task<DocumentRecord> Reload(AppServices s, DocumentRecord doc) => (await s.Documents.GetAsync("water")).Single(d => d.Id == doc.Id);

    // ---- approval goes through the run lock

    [Fact]
    public async Task Approving_runs_the_actions_once()
    {
        var s = Services(Echo("recorded {amount}"));
        var doc = await AddDoc(s, ReviewState.Pending);
        Assert.Equal(0, await s.Ticks.ReviewAsync(doc.Id, approve: true));
        var after = await Reload(s, doc);
        Assert.Equal(ReviewState.Approved, after.Review);
        Assert.Equal("recorded 73.21", Assert.Single(after.Actions).Summary);
        // A second approval finds nothing waiting and runs nothing.
        Assert.Null(await s.Ticks.ReviewAsync(doc.Id, approve: true));
        Assert.Equal(1, Assert.Single((await Reload(s, doc)).Actions).Attempts);
    }

    [Fact]
    public async Task Rejecting_runs_nothing()
    {
        var s = Services(Echo("recorded"));
        var doc = await AddDoc(s, ReviewState.Pending);
        Assert.Equal(0, await s.Ticks.ReviewAsync(doc.Id, approve: false));
        var after = await Reload(s, doc);
        Assert.Equal(ReviewState.Rejected, after.Review);
        Assert.Empty(after.Actions);
    }

    [Fact]
    public async Task Approval_waits_for_a_running_check()
    {
        var s = Services(Echo("recorded"));
        var doc = await AddDoc(s, ReviewState.Pending);
        // Another run (scheduled, Check now, or another process) holds the run lock.
        using (new FileStream(s.Paths.File("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Ticks.ReviewAsync(doc.Id, approve: true, lockWait: TimeSpan.Zero));
        var after = await Reload(s, doc);
        Assert.Equal(ReviewState.Pending, after.Review);
        Assert.Empty(after.Actions);
    }

    // ---- actions are remembered by identity, not position

    [Fact]
    public async Task Reordering_actions_reruns_nothing()
    {
        var s = Services(Echo("first", id: "a"), Echo("second", id: "b"));
        var doc = await AddDoc(s);
        await s.Actions.RunPendingAsync(s.Config.Accounts[0]);
        Assert.Equal(2, (await Reload(s, doc)).Actions.Count(a => a.Succeeded));

        s = Services(Echo("second", id: "b"), Echo("first", id: "a"));
        Assert.Equal(0, await s.Actions.RunPendingAsync(s.Config.Accounts[0]));
        var after = await Reload(s, doc);
        Assert.All(after.Actions, a => Assert.Equal(1, a.Attempts));
        Assert.Equal("first", after.Actions.Single(a => a.Key == "id:a").Summary);
    }

    [Fact]
    public async Task A_new_action_only_applies_to_documents_found_after_it_was_added()
    {
        var s = Services(Echo("first"));
        var old = await AddDoc(s, seen: DateTimeOffset.UtcNow.AddDays(-30));
        await s.Actions.RunPendingAsync(s.Config.Accounts[0]);

        s = Services(Echo("first"), Echo("added", id: "new"));
        await s.Actions.RunPendingAsync(s.Config.Accounts[0]);
        Assert.DoesNotContain((await Reload(s, old)).Actions, a => a.Key == "id:new");

        var fresh = await AddDoc(s);
        await s.Actions.RunPendingAsync(s.Config.Accounts[0]);
        Assert.Equal(["added", "first"], (await Reload(s, fresh)).Actions.Select(a => a.Summary).Order());
    }

    [Fact]
    public async Task Records_from_before_stable_keys_are_adopted_when_the_type_matches()
    {
        var s = Services(Echo("again"));
        var doc = await AddDoc(s, seen: DateTimeOffset.UtcNow.AddDays(-30),
            done: new ActionRecord { Index = 0, Type = "run-command", Succeeded = true, Attempts = 1, Summary = "earlier" });
        Assert.Equal(0, await s.Actions.RunPendingAsync(s.Config.Accounts[0]));
        var rec = Assert.Single((await Reload(s, doc)).Actions);
        Assert.Equal("earlier", rec.Summary);
        Assert.Equal(1, rec.Attempts);
    }

    [Fact]
    public void Keys_use_the_id_or_the_type_numbered_by_repeats()
    {
        var account = new AccountConfig
        {
            Actions = [new() { ["type"] = "webhook" }, new() { ["type"] = "run-command" }, new() { ["type"] = "webhook" }, new() { ["type"] = "webhook", ["id"] = "audit" }],
        };
        Assert.Equal(["webhook", "run-command", "webhook#2", "id:audit"], ActionKeys.For(account));
    }

    [Fact]
    public void Duplicate_action_ids_are_a_config_error()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Services(Echo("x", id: "same"), Echo("y", id: "same")));
        Assert.Contains("used twice", ex.Message);
    }
}
