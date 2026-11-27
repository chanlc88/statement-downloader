using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StatementDownloader.Core;

namespace StatementDownloader.App;

/// <summary>
/// The <c>accounts</c> and <c>settings</c> commands: the Settings page on the command line, with
/// JSON output, so a person's AI agent can set the app up for them. Changes go through
/// <see cref="SettingsService"/>, the same checks as the page. While the agent (<c>serve</c>) is
/// running, changes are sent to it so it uses them at once; otherwise config.yaml is edited directly.
/// </summary>
public static class AgentCommands
{
    public const string Usage = """
      accounts list              Accounts with their site, group, tags, folder and file name (JSON)
      accounts add --name <name> --site <site> --credential <reference> --folder <folder>
                   --file-name <template> [--group <group>] [--tags a,b] [--option key=value]...
                                 Add an account (checks the login can be read, then imports
                                 documents already in its folder)
      accounts set <account> [--folder <f>] [--file-name <t>] [--group <g>] [--tags a,b]
                   [--notify on|off]
                                 Change an account ("" clears group or tags)
      accounts preview <account|new> [--folder <f>] [--file-name <t>]
                                 Where a document would be saved, or what's wrong
      settings show              Base folder, accounts, groups and notifications (JSON)
      settings set documents-root <path>
      settings set notifications on|off
      settings set group-email <group> <address>[,<address>...]   ("" removes the addresses)
      settings set group-notify <group> on|off
      settings folders [under]   Existing folders under the base folder
      settings check             Check config.yaml loads (after editing it by hand)
      settings init --documents-root <path>
                                 Create a starting config.yaml if there is none
                                 add --dry-run to add/set to check a change without saving it
      agent-guide [--write [folder]]
                                 Instructions for an AI agent setting this app up; --write saves
                                 them as AGENTS.md and CLAUDE.md (default: next to config.yaml)
    """;

    // Readable output: names and notes keep their own characters (accents, apostrophes, &) rather than escapes.
    public static readonly JsonSerializerOptions Json = new(JsonFile.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---- commands that work without a (valid) config

    /// <summary>Handles commands that don't need the services; null when <paramref name="args"/> isn't one.</summary>
    public static int? RunWithoutServices(AppPaths paths, IReadOnlyList<string> args)
    {
        var o = new Options(args);
        switch (o.Word(0), o.Word(1))
        {
            case ("sites", _) when o.Has("--json"):
                Print(SettingsService.SitesView(StatementDownloader.Sites.SiteRegistry.All(paths.SitesDir)));
                return 0;

            case ("agent-guide", _):
                if (o.Has("--write"))
                {
                    var folder = o.Value("--write") ?? Path.GetDirectoryName(paths.ConfigFile)!;
                    foreach (var file in WriteGuide(paths, folder)) Console.WriteLine($"Wrote {file}");
                }
                else Console.WriteLine(Guide(paths));
                return 0;

            case ("settings", "check"):
                {
                    var config = ConfigLoader.Load(paths.ConfigFile);
                    Print(new { ok = true, configFile = paths.ConfigFile, accounts = config.Accounts.Count });
                    return 0;
                }

            case ("settings", "init"):
                {
                    var root = o.Value("--documents-root") ?? throw new ArgumentException("Give the base folder documents are saved under: settings init --documents-root <path>");
                    if (File.Exists(paths.ConfigFile)) throw new SettingsProblem($"{paths.ConfigFile} already exists. Use 'settings show' and 'settings set' to change it.");
                    var yaml = $"""
                        # Statement Downloader configuration, created by `statement-downloader settings init`.
                        # config.sample.yaml in the project describes every setting.

                        documentsRoot: {ConfigEditor.QuoteValue(root.Trim().Replace('\\', '/'))}

                        ui:
                          port: 5187

                        notifications:
                          enabled: true
                          channels: []

                        accounts: []

                        """.ReplaceLineEndings("\n");
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigFile)!);
                    ConfigEditor.Save(paths.ConfigFile, yaml);
                    WriteGuide(paths, Path.GetDirectoryName(paths.ConfigFile)!);
                    Print(new { created = paths.ConfigFile });
                    return 0;
                }
        }
        return null;
    }

    // ---- commands that need the services

    /// <summary>Handles the accounts and settings commands; null when <paramref name="args"/> isn't one.</summary>
    public static async Task<int?> RunAsync(AppServices s, IReadOnlyList<string> args)
    {
        var o = new Options(args);
        var dryRun = o.Has("--dry-run");
        switch (o.Word(0), o.Word(1))
        {
            case ("accounts", "list"):
                Print(((JsonObject)ToNode(await SettingsService.ViewAsync(s)))["accounts"]);
                return 0;

            case ("accounts", "preview"):
                Print(await SettingsService.PreviewAsync(s, o.Word(2) ?? "", o.Value("--folder"), o.Value("--file-name"), null));
                return 0;

            case ("accounts", "add"):
                {
                    var n = new NewAccount(o.Value("--name"), o.Value("--site"), o.Value("--group"), Tags(o.Value("--tags")),
                        o.Values("--option").Select(KeyValue).ToDictionary(kv => kv.Key, kv => kv.Value), o.Value("--credential"),
                        o.Value("--folder"), o.Value("--file-name"));
                    if (dryRun) { Print(await SettingsService.AddAccountAsync(s, n, dryRun: true)); return 0; }
                    return await SendOrRunAsync(s, "/api/settings/accounts", n, async () => (object)await SettingsService.AddAccountAsync(s, n));
                }

            case ("accounts", "set"):
                {
                    var id = o.Word(2) ?? throw new ArgumentException("Which account? accounts set <account> ...");
                    var change = new SettingsChange(null, [new AccountLocationChange(id, o.Value("--folder"), o.Value("--file-name"),
                        o.Value("--group"), o.Value("--tags") is { } t ? Tags(t) : null, o.Value("--notify") is { } on ? OnOff(on) : null)]);
                    return await ApplyAsync(s, change, dryRun);
                }

            case ("settings", "show"):
                Print(await SettingsService.ViewAsync(s));
                return 0;

            case ("settings", "folders"):
                Print(SettingsService.Folders(s, o.Word(2)));
                return 0;

            case ("settings", "set"):
                {
                    var what = o.Word(2) ?? throw new ArgumentException($"What to set?\n\n{Usage}");
                    string Need(int i) => o.Word(i) ?? throw new ArgumentException($"Missing value: settings set {what} ...\n\n{Usage}");
                    var change = what switch
                    {
                        "documents-root" => new SettingsChange(Need(3), null),
                        "notifications" => new SettingsChange(null, null, new NotificationsChange(OnOff(Need(3)), null)),
                        "group-email" => GroupChange(s, Need(3), g => g with { Email = SettingsService.SplitAddresses([o.Word(4) ?? ""]) }),
                        "group-notify" => GroupChange(s, Need(3), g => g with { Enabled = OnOff(Need(4)) }),
                        _ => throw new ArgumentException($"Can't set '{what}'.\n\n{Usage}"),
                    };
                    return await ApplyAsync(s, change, dryRun);
                }
        }
        return null;
    }

    private static async Task<int> ApplyAsync(AppServices s, SettingsChange change, bool dryRun)
    {
        if (dryRun) { Print(await SettingsService.ApplyAsync(s, change, dryRun: true)); return 0; }
        return await SendOrRunAsync(s, "/api/settings", change, () => SettingsService.ApplyAsync(s, change));
    }

    /// <summary>Every group's notification settings, with one changed (the API replaces the whole list).</summary>
    private static SettingsChange GroupChange(AppServices s, string name, Func<GroupNotificationsChange, GroupNotificationsChange> edit)
    {
        var n = s.Config.Notifications;
        var names = s.Config.Accounts.Select(a => a.Group).OfType<string>().Concat(n.Groups.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var match = names.FirstOrDefault(g => g.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new SettingsProblem($"No group '{name}'. Groups: {(names.Count == 0 ? "none yet (give an account one with accounts set <account> --group <name>)" : string.Join(", ", names))}.");
        var groups = names.Select(g => new GroupNotificationsChange(g, [.. n.Group(g)?.Email ?? []], n.Group(g)?.Enabled ?? true))
            .Select(g => g.Name == match ? edit(g) : g).ToList();
        return new SettingsChange(null, null, new NotificationsChange(null, groups));
    }

    /// <summary>
    /// Sends a change to the running agent when there is one for this config file, so it takes effect
    /// at once; otherwise makes it here.
    /// </summary>
    private static async Task<int> SendOrRunAsync<T>(AppServices s, string path, T body, Func<Task<object>> local)
    {
        using var http = await RunningAgentAsync(s);
        if (http is null)
        {
            Print(await local());
            return 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("X-SD-Key", s.UiKey);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            Print(JsonNode.Parse(text));
            return 0;
        }
        var error = response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? "The running agent refused the change: it uses a different data folder. Run this with the same --data, or stop the agent first."
            : (text.Length > 0 ? JsonNode.Parse(text)?["error"]?.GetValue<string>() : null) ?? $"The running agent answered {(int)response.StatusCode}.";
        throw new SettingsProblem(error);
    }

    /// <summary>A client for the agent running on this config file, or null when none is.</summary>
    private static async Task<HttpClient?> RunningAgentAsync(AppServices s)
    {
        // Adding an account imports its history, which waits for any check under way.
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{s.Config.Ui.Port}"), Timeout = TimeSpan.FromMinutes(20) };
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var settings = await http.GetFromJsonAsync<JsonObject>("/api/settings", Json, probe.Token);
            var file = settings?["configFile"]?.GetValue<string>();
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (file is not null && Path.GetFullPath(file).Equals(Path.GetFullPath(s.Paths.ConfigFile), comparison)) return http;
        }
        // Nothing listening (or something else is): make the change here.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
        http.Dispose();
        return null;
    }

    // ---- the agent guide

    /// <summary>The guide for AI agents, with this install's command and paths filled in.</summary>
    public static string Guide(AppPaths paths)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent-guide.md")
            ?? throw new InvalidOperationException("Agent guide resource missing.");
        var command = string.Join(' ', Install.SelfCommand().Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        return new StreamReader(stream).ReadToEnd()
            .Replace("{{command}}", command)
            .Replace("{{configFile}}", paths.ConfigFile)
            .Replace("{{dataDir}}", paths.DataDir)
            .Replace("{{sitesDir}}", paths.SitesDir);
    }

    private const string Marker = "<!-- written by statement-downloader; it replaces this file on updates -->";

    /// <summary>
    /// Writes the guide as AGENTS.md (read by most coding agents) and a CLAUDE.md that points to it,
    /// so an agent opened in <paramref name="folder"/> finds it. A file the person wrote is left alone.
    /// </summary>
    public static IReadOnlyList<string> WriteGuide(AppPaths paths, string folder)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var (name, text) in new[] { ("AGENTS.md", Guide(paths)), ("CLAUDE.md", "@AGENTS.md\n") })
        {
            var file = Path.Combine(folder, name);
            if (File.Exists(file) && !File.ReadAllText(file).Contains(Marker)) continue;
            File.WriteAllText(file, Marker + "\n" + text);
            written.Add(file);
        }
        return written;
    }

    // ---- helpers

    public static void Print(object? value) =>
        Console.WriteLine(value is JsonNode node ? node.ToJsonString(Json) : JsonSerializer.Serialize(value, Json));

    private static JsonNode ToNode(object value) => JsonSerializer.SerializeToNode(value, Json)!;

    private static List<string> Tags(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool OnOff(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => true,
        "off" or "false" or "no" => false,
        _ => throw new ArgumentException($"Expected on or off, not '{value}'."),
    };

    private static KeyValuePair<string, string> KeyValue(string value) =>
        value.IndexOf('=') is var i and > 0 ? new(value[..i].Trim(), value[(i + 1)..]) : throw new ArgumentException($"--option takes key=value, not '{value}'.");

    /// <summary>Positional words and <c>--name value</c> options.</summary>
    private sealed class Options
    {
        private readonly List<string> _words = [];
        private readonly List<(string Name, string? Value)> _options = [];

        public Options(IReadOnlyList<string> args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { _words.Add(args[i]); continue; }
                var hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                _options.Add((args[i], hasValue ? args[++i] : null));
            }
        }

        public string? Word(int i) => i < _words.Count ? _words[i] : null;
        public bool Has(string name) => _options.Any(o => o.Name == name);
        public string? Value(string name) => _options.LastOrDefault(o => o.Name == name).Value;
        public IEnumerable<string> Values(string name) => _options.Where(o => o.Name == name && o.Value is not null).Select(o => o.Value!);
    }
}
