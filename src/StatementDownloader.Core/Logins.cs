using System.Security.Cryptography;
using System.Text;

namespace StatementDownloader.Core;

/// <summary>
/// Accounts on the same site with the same credential are one login — for example a bank
/// account and a card under one Chase sign-in. They share a browser profile (one sign-in,
/// one verification code), one "needs you" prompt, and are fetched together.
/// </summary>
public static class Logins
{
    /// <summary>What makes two accounts the same login: the site and the credential reference.</summary>
    public static string Key(AccountConfig a) =>
        a.Site.Trim().ToLowerInvariant() + "|" + a.Credential.Trim();

    /// <summary>The other enabled accounts that share this account's login, in config order.</summary>
    public static IReadOnlyList<AccountConfig> Siblings(AppConfig config, AccountConfig account) =>
        config.Accounts.Where(a => a.Enabled && a.Id != account.Id && Key(a) == Key(account)).ToList();

    /// <summary>A stable id for the login, used for the shared browser profile and its notifications.</summary>
    public static string Id(AccountConfig account) =>
        "login-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key(account))))[..12];

    /// <summary>
    /// The login's browser profile folder. The first time, an existing per-account profile from
    /// an older version is taken over (the most recently used one), so nobody has to sign in again.
    /// </summary>
    public static string ProfileDir(AppPaths paths, AppConfig config, AccountConfig account)
    {
        var dir = paths.ProfileDir(Id(account));
        if (Directory.Exists(dir)) return dir;
        var old = new[] { account }.Concat(Siblings(config, account))
            .Select(a => new DirectoryInfo(paths.ProfileDir(a.Id)))
            .Where(d => d.Exists)
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();
        if (old is not null)
        {
            try { Directory.Move(old.FullName, dir); }
            catch (IOException) { return old.FullName; }   // in use; try again next run
        }
        return dir;
    }

    /// <summary>"Chase Business Checking, Chase Business Card" — the accounts on one login.</summary>
    public static string Label(AppConfig config, AccountConfig account) =>
        string.Join(", ", new[] { account }.Concat(Siblings(config, account)).Select(a => a.DisplayName));
}
