using System.Text.RegularExpressions;
using StatementDownloader.Core;
using StatementDownloader.Sites.Recipes;

namespace StatementDownloader.App;

/// <summary>A change the person (or an agent) asked for that can't be made; the message says why.</summary>
public sealed class SettingsProblem(string message) : Exception(message);

public sealed record SettingsChange(string? DocumentsRoot, List<AccountLocationChange>? Accounts, NotificationsChange? Notifications = null);
public sealed record NotificationsChange(bool? Enabled, List<GroupNotificationsChange>? Groups);
public sealed record GroupNotificationsChange(string Name, List<string>? Email, bool? Enabled);
public sealed record NewAccount(string? Name, string? Site, string? Group, List<string>? Tags, Dictionary<string, string>? Options, string? Credential, string? Folder, string? FileName);
public sealed record AccountLocationChange(string Id, string? Folder, string? FileName, string? Group = null, List<string>? Tags = null, bool? Notify = null);
public sealed record AddedAccount(string Id, int Imported, bool DryRun = false);

/// <summary>
/// The one place settings are changed: the Settings page and the command line (and so AI agents
/// driving it) both go through here, so they check the same things. Edits config.yaml in place,
/// keeping comments, and applies the change to the running <see cref="AppServices"/>.
/// </summary>
public static class SettingsService
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>
    /// Adds an account to config.yaml, checks its login can be read, and imports the documents
    /// already in its folder. The first sign-in is left to the person. With <paramref name="dryRun"/>
    /// everything is checked but nothing is saved or imported.
    /// </summary>
    public static async Task<AddedAccount> AddAccountAsync(AppServices s, NewAccount n, bool dryRun = false, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        AccountConfig added;
        try
        {
            if (string.IsNullOrWhiteSpace(n.Name)) throw new SettingsProblem("Give the account a name.");
            if (s.Sites.GetValueOrDefault(n.Site ?? "") is not RecipeSiteFetcher site)
                throw new SettingsProblem($"Choose a site. Known sites: {string.Join(", ", s.Sites.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
            var credential = (n.Credential ?? "").Trim();
            if (credential.Length == 0) throw new SettingsProblem("Enter the login: a Bitwarden secret id, or a reference such as keychain:name.");
            if (Guid.TryParse(credential, out _)) credential = "bitwarden:" + credential;
            var folder = CleanFolder(n.Folder ?? "");
            var fileName = (n.FileName ?? "").Trim();
            if ((AccountLocation.FolderProblem(folder) ?? AccountLocation.FileNameProblem(fileName)) is { } problem) throw new SettingsProblem(problem);
            var unknown = (n.Options ?? []).Keys.Where(k => !site.Recipe.Options.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
                throw new SettingsProblem($"{site.Recipe.Name} has no option {string.Join(", ", unknown)}." +
                    (site.Recipe.Options.Count > 0 ? $" Its options: {string.Join(", ", site.Recipe.Options.Keys)}." : " It takes no options."));
            try { await s.Secrets.GetCredentialAsync(credential, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SettingsProblem($"Couldn't read that login: {ex.Message}");
            }

            var slug = Regex.Replace(n.Name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) slug = site.SiteId;
            var id = slug;
            for (var i = 2; s.Config.Accounts.Any(a => a.Id == id); i++) id = $"{slug}-{i}";

            var options = (n.Options ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
            var tags = ConfigEditor.NormalizeTags(n.Tags ?? []);
            var q = ConfigEditor.QuoteValue;
            var entries = new List<(string, string)> { ("id", id), ("name", q(n.Name.Trim())) };
            if (!string.IsNullOrWhiteSpace(n.Group)) entries.Add(("group", q(n.Group.Trim())));
            if (tags.Count > 0) entries.Add(("tags", "[" + string.Join(", ", tags.Select(q)) + "]"));
            entries.Add(("site", site.SiteId));
            entries.Add(("kind", site.Recipe.Kind));
            if (options.Count > 0) entries.Add(("siteOptions", "{ " + string.Join(", ", options.Select(kv => $"{kv.Key}: {q(kv.Value)}")) + " }"));
            entries.Add(("credential", credential));
            entries.Add(("folder", q(folder)));
            entries.Add(("fileName", q(fileName)));
            entries.Add(("schedule", "auto"));

            var path = s.Paths.ConfigFile;
            var yaml = ConfigEditor.AddAccount(await File.ReadAllTextAsync(path, ct), entries);
            var saved = Check(yaml);
            if (dryRun) return new AddedAccount(id, 0, DryRun: true);
            ConfigEditor.Save(path, yaml);
            added = saved.Accounts.Single(a => a.Id == id);
            // A new list rather than Add: a check may be going through the old one.
            s.Config.Accounts = [.. s.Config.Accounts, added];
        }
        finally { Lock.Release(); }

        var (imported, _) = await HistoryImport.RunAsync(s, added, ct);
        return new AddedAccount(added.Id, imported);
    }

    /// <summary>Applies a change to the base folder, accounts' locations, groups, tags and notifications.</summary>
    public static async Task<object> ApplyAsync(AppServices s, SettingsChange change, bool dryRun = false, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            var path = s.Paths.ConfigFile;
            var yaml = await File.ReadAllTextAsync(path, ct);
            var root = change.DocumentsRoot?.Trim();
            if (root is not null)
            {
                if (root.Length == 0) throw new SettingsProblem("The base folder is required.");
                yaml = ConfigEditor.SetDocumentsRoot(yaml, root.Replace('\\', '/'));
            }
            foreach (var a in change.Accounts ?? [])
            {
                var account = s.Config.Accounts.FirstOrDefault(x => x.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new SettingsProblem($"No account '{a.Id}'. Accounts: {string.Join(", ", s.Config.Accounts.Select(x => x.Id))}");
                var folder = CleanFolder(a.Folder ?? account.Folder);
                var fileName = (a.FileName ?? account.FileName).Trim();
                if ((AccountLocation.FolderProblem(folder) ?? AccountLocation.FileNameProblem(fileName)) is { } problem)
                    throw new SettingsProblem($"{account.DisplayName}: {problem}");
                if (folder != account.Folder) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "folder", folder);
                if (fileName != account.FileName) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "fileName", fileName);
                if (a.Group is { } group && group.Trim() != (account.Group ?? "")) yaml = ConfigEditor.SetAccountGroup(yaml, account.Id, group);
                if (a.Tags is { } tags && !ConfigEditor.NormalizeTags(tags).SequenceEqual(account.Tags)) yaml = ConfigEditor.SetAccountTags(yaml, account.Id, tags);
                if (a.Notify is { } notify && notify != account.Notify) yaml = ConfigEditor.SetAccountNotify(yaml, account.Id, notify);
            }
            if (change.Notifications is { } nc)
            {
                if (nc.Enabled is { } on && on != s.Config.Notifications.Enabled) yaml = ConfigEditor.SetNotificationsEnabled(yaml, on);
                if (nc.Groups is { } groups)
                {
                    var list = new List<(string, IReadOnlyList<string>, bool)>();
                    foreach (var g in groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
                    {
                        var email = SplitAddresses(g.Email ?? []);
                        if (email.Select(ConfigLoader.EmailProblem).OfType<string>().FirstOrDefault() is { } bad) throw new SettingsProblem($"{g.Name.Trim()}: {bad}");
                        // A group with no addresses and notifications on needs no entry: it uses the channel's own 'to'.
                        if (email.Count > 0 || g.Enabled == false) list.Add((g.Name.Trim(), email, g.Enabled != false));
                    }
                    yaml = ConfigEditor.SetNotificationGroups(yaml, list);
                }
            }

            var saved = Check(yaml);
            if (dryRun) return new { dryRun = true, ok = true };
            ConfigEditor.Save(path, yaml);

            // Apply to the running agent.
            s.Config.DocumentsRoot = saved.DocumentsRoot;
            foreach (var a in s.Config.Accounts)
                if (saved.Accounts.FirstOrDefault(x => x.Id == a.Id) is { } n) { a.Folder = n.Folder; a.FileName = n.FileName; a.Group = n.Group; a.Tags = n.Tags; a.Notify = n.Notify; }
            s.Config.Notifications.Enabled = saved.Notifications.Enabled;
            s.Config.Notifications.Groups = saved.Notifications.Groups;
        }
        catch (InvalidDataException ex) { throw new SettingsProblem(ex.Message); }
        finally { Lock.Release(); }
        return await ViewAsync(s, ct);
    }

    /// <summary>Addresses from a list whose items may themselves be comma, semicolon or space separated.</summary>
    public static List<string> SplitAddresses(IEnumerable<string> items) =>
        items.SelectMany(e => e.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string CleanFolder(string folder) => folder.Trim().Replace('\\', '/').Trim('/');

    private static AppConfig Check(string yaml)
    {
        try { return ConfigEditor.Check(yaml); }
        catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException) { throw new SettingsProblem(ex.Message); }
    }

    // ---- views

    public static async Task<object> ViewAsync(AppServices s, CancellationToken ct = default)
    {
        var accounts = new List<object>();
        foreach (var a in s.Config.Accounts)
        {
            var example = await ExamplePathAsync(s, a, s.Config.DocumentsRoot, a.Folder, a.FileName, ct);
            accounts.Add(new { a.Id, name = a.DisplayName, a.Site, group = a.Group, tags = a.Tags, notify = a.Notify, sharesSignInWith = Logins.Siblings(s.Config, a).Select(x => x.DisplayName), a.Folder, a.FileName, example, relative = Relative(s.Config.DocumentsRoot, example) });
        }
        return new
        {
            configFile = s.Paths.ConfigFile,
            documentsRoot = s.Config.DocumentsRoot,
            documentsRootExists = Directory.Exists(s.Config.DocumentsRoot),
            groups = s.Config.Accounts.Select(a => a.Group).OfType<string>().Where(g => g.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase),
            tags = s.Config.Accounts.SelectMany(a => a.Tags).Distinct().Order(StringComparer.OrdinalIgnoreCase),
            accounts,
            notifications = NotificationsView(s),
        };
    }

    /// <summary>Who gets which notifications: the on/off switch, each group's addresses, and the email channels.</summary>
    private static object NotificationsView(AppServices s)
    {
        var n = s.Config.Notifications;
        var emailChannels = n.Channels.Where(c => c.GetValueOrDefault("type") == "email").ToList();
        var emailNames = emailChannels.Select(c => c.GetValueOrDefault("name") ?? "email").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var levels = new[] { ("actionNeeded", "action needed"), ("headsUp", "heads-ups"), ("info", "info (new documents, approvals waiting)") };
        var groupNames = s.Config.Accounts.Select(a => a.Group).OfType<string>().Concat(n.Groups.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return new
        {
            enabled = n.Enabled,
            channels = n.Channels.Select(c => new { type = c.GetValueOrDefault("type"), name = c.GetValueOrDefault("name") ?? c.GetValueOrDefault("type") }),
            emailChannels = emailNames,
            // Which levels go by email at all (routes), so the page can say what recipients will get.
            emailedLevels = levels.Where(l => n.Routes.GetValueOrDefault(l.Item1)?.Any(emailNames.Contains) == true).Select(l => l.Item2),
            defaultTo = emailChannels.Select(c => c.GetValueOrDefault("to")).OfType<string>().FirstOrDefault(),
            groups = groupNames.Select(g => new
            {
                name = g,
                email = n.Group(g)?.Email ?? [],
                enabled = n.Group(g)?.Enabled ?? true,
                accounts = s.Config.Accounts.Count(a => string.Equals(a.Group, g, StringComparison.OrdinalIgnoreCase)),
            }),
            ungrouped = s.Config.Accounts.Count(a => a.Group is null),
        };
    }

    /// <summary>The sites accounts can be added for, with what each needs.</summary>
    public static IEnumerable<object> SitesView(IEnumerable<StatementDownloader.Core.ISiteFetcher> sites) => sites
        .Select(x => (x as RecipeSiteFetcher)?.Recipe)
        .OfType<SiteRecipe>()
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Select(r => new { r.Id, r.Name, r.Kind, r.Website, r.Notes, options = r.Options, signIn = r.SignIn.YourBrowser || r.SignIn.Attended ? "window" : "background" });

    /// <summary>Where a document would be saved with these settings, or what's wrong with them.</summary>
    public static async Task<object> PreviewAsync(AppServices s, string account, string? folder, string? fileName, string? root, CancellationToken ct = default)
    {
        // A new account (not saved yet) has no entry: preview it under the id it will get.
        var a = s.Config.Accounts.FirstOrDefault(x => x.Id == account) ?? new AccountConfig { Id = string.IsNullOrWhiteSpace(account) ? "new-account" : account };
        folder ??= a.Folder; fileName ??= a.FileName; root = string.IsNullOrWhiteSpace(root) ? s.Config.DocumentsRoot : ConfigLoader.ExpandHome(root.Trim());
        var problem = AccountLocation.FolderProblem(folder) ?? AccountLocation.FileNameProblem(fileName);
        if (problem is not null) return new { problem };
        var path = await ExamplePathAsync(s, a, root, folder, fileName, ct);
        return new { path, relative = Relative(root, path) };
    }

    /// <summary>Existing subfolders of the base folder, for suggestions while choosing a folder.</summary>
    public static IReadOnlyList<string> Folders(AppServices s, string? under)
    {
        var rel = (under ?? "").Replace('\\', '/').Trim('/');
        if (rel.Split('/').Any(p => p == "..") || Path.IsPathRooted(rel)) throw new SettingsProblem("The folder must be inside the base folder.");
        var dir = Path.Combine(s.Config.DocumentsRoot, rel);
        if (!Directory.Exists(dir)) return [];
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(FileAttributes.Hidden))
                .Select(d => rel.Length == 0 ? d.Name : rel + "/" + d.Name)
                .Order(StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string Relative(string root, string path)
    {
        var r = Path.GetRelativePath(Path.GetFullPath(root), path);
        return r.StartsWith("..", StringComparison.Ordinal) ? path : r.Replace('\\', '/');
    }

    /// <summary>Where the newest document on record would be saved with these settings (or a made-up one dated today).</summary>
    private static async Task<string> ExamplePathAsync(AppServices s, AccountConfig a, string root, string folder, string fileName, CancellationToken ct)
    {
        var last = (await s.Documents.GetAsync(a.Id, ct)).OrderByDescending(d => d.DocumentDate).FirstOrDefault();
        var date = last?.DocumentDate ?? DateOnly.FromDateTime(DateTime.Today);
        var statement = last?.StatementNumber ?? "12345678";
        return Path.GetFullPath(Path.Combine(root,
            FileNameTemplate.Render(folder, a, date, statement),
            FileNameTemplate.Render(fileName, a, date, statement)));
    }
}
