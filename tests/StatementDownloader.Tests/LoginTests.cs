using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public class LoginTests
{
    private static AccountConfig Account(string id, string site = "chase", string credential = "bitwarden:abc", bool enabled = true) =>
        new() { Id = id, Name = id.ToUpperInvariant(), Site = site, Credential = credential, Enabled = enabled };

    private static readonly AppConfig Config = new()
    {
        Accounts =
        [
            Account("bank"), Account("card"), Account("other-login", credential: "bitwarden:xyz"),
            Account("other-site", site: "scana-energy"), Account("off", enabled: false),
        ],
    };

    [Fact]
    public void Same_site_and_credential_is_one_login()
    {
        Assert.Equal(["card"], Logins.Siblings(Config, Config.Accounts[0]).Select(a => a.Id));
        Assert.Empty(Logins.Siblings(Config, Config.Accounts[2]));
        Assert.Empty(Logins.Siblings(Config, Config.Accounts[3]));
        Assert.Equal(Logins.Id(Config.Accounts[0]), Logins.Id(Config.Accounts[1]));
        Assert.NotEqual(Logins.Id(Config.Accounts[0]), Logins.Id(Config.Accounts[2]));
        Assert.Equal("BANK, CARD", Logins.Label(Config, Config.Accounts[0]));
    }

    [Fact]
    public void The_login_takes_over_the_most_recent_old_profile()
    {
        var dir = Directory.CreateTempSubdirectory("sd-login-").FullName;
        try
        {
            var paths = new AppPaths(Path.Combine(dir, "config.yaml"), dir);
            Directory.CreateDirectory(paths.ProfileDir("bank"));
            var card = Directory.CreateDirectory(paths.ProfileDir("card"));
            File.WriteAllText(Path.Combine(card.FullName, "Cookies"), "signed in");
            Directory.SetLastWriteTimeUtc(paths.ProfileDir("bank"), DateTime.UtcNow.AddDays(-3));
            Directory.SetLastWriteTimeUtc(card.FullName, DateTime.UtcNow);

            var profile = Logins.ProfileDir(paths, Config, Config.Accounts[0]);
            Assert.Equal(paths.ProfileDir(Logins.Id(Config.Accounts[0])), profile);
            Assert.True(File.Exists(Path.Combine(profile, "Cookies")));
            // Both accounts now use it.
            Assert.Equal(profile, Logins.ProfileDir(paths, Config, Config.Accounts[1]));
        }
        finally { Directory.Delete(dir, true); }
    }
}
