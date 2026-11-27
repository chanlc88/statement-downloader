using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.App;
using StatementDownloader.Core;

namespace StatementDownloader.Tests;

/// <summary>The settings changes the Settings page and the command line (AI agents) share.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sd-settings-").FullName;
    private string ConfigFile => Path.Combine(_dir, "config.yaml");

    static SettingsServiceTests() =>
        Environment.SetEnvironmentVariable("SD_TEST_LOGIN", """{"username": "someone", "password": "not-a-real-one"}""");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AppServices Services()
    {
        File.WriteAllText(ConfigFile, $$"""
            # keep this comment
            documentsRoot: "{{_dir.Replace(Path.DirectorySeparatorChar, '/')}}/docs"
            notifications:
              enabled: true
            accounts:
              - id: water
                site: dekalb-water
                credential: env:SD_TEST_LOGIN
                folder: Bills
                fileName: "{date:yyyy-MM-dd}-Water.pdf"
            """.ReplaceLineEndings("\n"));
        return AppServices.Build(new AppPaths(ConfigFile, Path.Combine(_dir, "data")), NullLoggerFactory.Instance);
    }

    private static NewAccount Gas(string? site = "scana-energy", string folder = "Utilities/Gas/{year}", string? credential = "env:SD_TEST_LOGIN") =>
        new("SCANA Gas", site, "Personal", ["Utility", "gas"], null, credential, folder, "{date:yyyy-MM-dd}-Gas.pdf");

    [Fact]
    public async Task A_dry_run_checks_an_account_but_saves_nothing()
    {
        using var s = Services();
        var before = File.ReadAllText(ConfigFile);
        var result = await SettingsService.AddAccountAsync(s, Gas(), dryRun: true);
        Assert.True(result.DryRun);
        Assert.Equal("scana-gas", result.Id);
        Assert.Equal(before, File.ReadAllText(ConfigFile));
        Assert.Single(s.Config.Accounts);
    }

    [Fact]
    public async Task Adding_an_account_saves_it_and_the_running_config_sees_it()
    {
        using var s = Services();
        var result = await SettingsService.AddAccountAsync(s, Gas());
        Assert.Equal("scana-gas", result.Id);
        var saved = ConfigLoader.Load(ConfigFile).Accounts.Single(a => a.Id == "scana-gas");
        Assert.Equal(("Personal", "Utilities/Gas/{year}"), (saved.Group, saved.Folder));
        Assert.Equal(["utility", "gas"], saved.Tags);
        Assert.Contains("# keep this comment", File.ReadAllText(ConfigFile));
        Assert.Contains(s.Config.Accounts, a => a.Id == "scana-gas");
    }

    [Theory]
    [InlineData("no-such-site", "Utilities", "env:SD_TEST_LOGIN", "Choose a site")]
    [InlineData("scana-energy", "../outside", "env:SD_TEST_LOGIN", "..")]
    [InlineData("scana-energy", "Utilities", "env:SD_NO_SUCH_LOGIN", "Couldn't read that login")]
    [InlineData("scana-energy", "Utilities", "", "Enter the login")]
    public async Task Bad_accounts_are_refused_with_a_reason(string site, string folder, string credential, string reason)
    {
        using var s = Services();
        var before = File.ReadAllText(ConfigFile);
        var ex = await Assert.ThrowsAsync<SettingsProblem>(() => SettingsService.AddAccountAsync(s, Gas(site, folder, credential)));
        Assert.Contains(reason, ex.Message);
        Assert.Equal(before, File.ReadAllText(ConfigFile));
    }

    [Fact]
    public async Task Unknown_site_options_are_refused()
    {
        using var s = Services();
        var ex = await Assert.ThrowsAsync<SettingsProblem>(() => SettingsService.AddAccountAsync(s, Gas() with { Options = new() { ["nope"] = "x" } }));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Changes_apply_to_the_file_and_the_running_config()
    {
        using var s = Services();
        await SettingsService.ApplyAsync(s, new SettingsChange(null,
            [new AccountLocationChange("water", "Utilities/Water/{year}", null, "Rentals", ["water"], Notify: false)],
            new NotificationsChange(null, [new GroupNotificationsChange("Rentals", ["a@example.com, b@example.com"], true)])));
        var water = s.Account("water");
        Assert.Equal(("Utilities/Water/{year}", "Rentals", false), (water.Folder, water.Group, water.Notify));
        Assert.Equal(["a@example.com", "b@example.com"], s.Config.Notifications.Group("Rentals")!.Email);
        Assert.Equal("Rentals", ConfigLoader.Load(ConfigFile).Accounts.Single().Group);
    }

    [Fact]
    public async Task A_dry_run_change_saves_nothing_and_a_bad_one_says_why()
    {
        using var s = Services();
        var before = File.ReadAllText(ConfigFile);
        await SettingsService.ApplyAsync(s, new SettingsChange("D:/Elsewhere", null), dryRun: true);
        Assert.Equal(before, File.ReadAllText(ConfigFile));
        Assert.NotEqual("D:/Elsewhere", s.Config.DocumentsRoot);

        var ex = await Assert.ThrowsAsync<SettingsProblem>(() => SettingsService.ApplyAsync(s,
            new SettingsChange(null, [new AccountLocationChange("water", null, "no-date.pdf")])));
        Assert.Contains("date token", ex.Message);
        ex = await Assert.ThrowsAsync<SettingsProblem>(() => SettingsService.ApplyAsync(s,
            new SettingsChange(null, [new AccountLocationChange("nope", "x", null)])));
        Assert.Contains("No account 'nope'", ex.Message);
        Assert.Equal(before, File.ReadAllText(ConfigFile));
    }

    // ---- the guide for agents

    [Fact]
    public void The_agent_guide_names_this_install()
    {
        var paths = new AppPaths(ConfigFile, Path.Combine(_dir, "data"));
        var guide = AgentCommands.Guide(paths);
        Assert.Contains(ConfigFile, guide);
        Assert.DoesNotContain("{{", guide);
        Assert.Contains("Never handle passwords", guide);
    }

    [Fact]
    public void Writing_the_guide_leaves_the_persons_own_files_alone()
    {
        var paths = new AppPaths(ConfigFile, Path.Combine(_dir, "data"));
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "my own notes");
        var written = AgentCommands.WriteGuide(paths, _dir);
        Assert.Equal([Path.Combine(_dir, "AGENTS.md")], written);
        Assert.Equal("my own notes", File.ReadAllText(Path.Combine(_dir, "CLAUDE.md")));
        // Its own file is replaced on the next write.
        Assert.Equal([Path.Combine(_dir, "AGENTS.md")], AgentCommands.WriteGuide(paths, _dir));
    }
}
