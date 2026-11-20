using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StatementDownloader.Core;

public enum DocumentKind { Bill, Invoice, Statement }

public sealed class AppConfig
{
    /// <summary>Per-machine folder that account folders are relative to.</summary>
    public string DocumentsRoot { get; set; } = "";
    public UiConfig Ui { get; set; } = new();
    public SecretsConfig Secrets { get; set; } = new();
    public ScheduleDefaults Schedule { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public List<AccountConfig> Accounts { get; set; } = [];
}

public sealed class UiConfig
{
    public int Port { get; set; } = 5187;
}

public sealed class SecretsConfig
{
    public BitwardenConfig Bitwarden { get; set; } = new();
}

public sealed class BitwardenConfig
{
    public string ApiUrl { get; set; } = "https://api.bitwarden.com";
    public string IdentityUrl { get; set; } = "https://identity.bitwarden.com";
}

public sealed class ScheduleDefaults
{
    /// <summary>How many days before the window opens to do a sign-in-only check.</summary>
    public int PreflightDays { get; set; } = 3;
    /// <summary>Full checks per day while inside the window.</summary>
    public int ChecksPerDay { get; set; } = 2;
    /// <summary>Days after the window closes to keep checking daily before raising an issue.</summary>
    public int OverdueGraceDays { get; set; } = 7;
    public int MinSpreadDays { get; set; } = 2;
    public int MaxSpreadDays { get; set; } = 7;
    /// <summary>Sign-in-only check every N days outside windows to keep sessions alive (0 = off).</summary>
    public int KeepAliveDays { get; set; }
    /// <summary>Warn when a remembered sign-in lapses within this many days.</summary>
    public int ExpiryWarningDays { get; set; } = 5;
    /// <summary>Hours between reminders for open action-needed issues.</summary>
    public int ReminderHours { get; set; } = 24;
}

public sealed class NotificationConfig
{
    /// <summary>Channel definitions. Each has <c>type</c> and <c>name</c> plus type-specific keys.</summary>
    public List<Dictionary<string, string>> Channels { get; set; } = [];
    /// <summary>Channel names per level: actionNeeded, headsUp, info.</summary>
    public Dictionary<string, List<string>> Routes { get; set; } = [];
    /// <summary>e.g. "22:00-07:00". Pushes are held until it ends.</summary>
    public string? QuietHours { get; set; }
    /// <summary>Collect info notifications into one message per day.</summary>
    public bool Digest { get; set; }
    /// <summary>Local hour the digest is sent.</summary>
    public int DigestHour { get; set; } = 18;
}

public sealed class AccountConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string Site { get; set; } = "";
    public DocumentKind Kind { get; set; } = DocumentKind.Bill;
    public string Credential { get; set; } = "";
    public Dictionary<string, string> SiteOptions { get; set; } = [];
    public string Folder { get; set; } = "";
    public string FileName { get; set; } = "";
    public ScheduleConfig Schedule { get; set; } = new();
    public bool Review { get; set; }
    /// <summary>Each entry has <c>type</c> plus action-specific keys.</summary>
    public List<Dictionary<string, string>> Actions { get; set; } = [];
    public bool Enabled { get; set; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;
    public string KindWord => Kind.ToString().ToLowerInvariant();
}

public enum ScheduleMode { Auto, Window, Interval }

/// <summary>
/// <c>schedule: auto</c> (or omitted), <c>schedule: { dayOfMonth: "15-22" }</c>, or <c>schedule: { every: 7d }</c>.
/// </summary>
public sealed class ScheduleConfig
{
    public string? DayOfMonth { get; set; }
    public string? Every { get; set; }

    public ScheduleMode Mode => DayOfMonth is not null ? ScheduleMode.Window : Every is not null ? ScheduleMode.Interval : ScheduleMode.Auto;

    public (int From, int To) DayRange
    {
        get
        {
            var m = Regex.Match(DayOfMonth ?? "", @"^\s*(\d{1,2})\s*-\s*(\d{1,2})\s*$");
            if (!m.Success) throw new FormatException($"dayOfMonth must look like \"15-22\" (got \"{DayOfMonth}\").");
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
    }

    public TimeSpan Interval => Durations.Parse(Every ?? "1d");
}

public static class Durations
{
    /// <summary>Parses "7d", "12h", "90m", "30s".</summary>
    public static TimeSpan Parse(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*([dhms])$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException($"Duration must look like 7d, 12h, 90m or 30s (got \"{text}\").");
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(m.Groups[2].Value[0]) switch
        {
            'd' => TimeSpan.FromDays(n),
            'h' => TimeSpan.FromHours(n),
            's' => TimeSpan.FromSeconds(n),
            _ => TimeSpan.FromMinutes(n),
        };
    }
}

public static class ConfigLoader
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Config file not found: {path}. Copy config.sample.yaml there to start.", path);
        return Parse(File.ReadAllText(path));
    }

    public static AppConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new ScheduleConfigConverter())
            .Build();
        var config = deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
        config.DocumentsRoot = ExpandHome(config.DocumentsRoot);
        Validate(config);
        return config;
    }

    public static string ExpandHome(string path) =>
        path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path;

    public static void Validate(AppConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(config.DocumentsRoot)) errors.Add("documentsRoot is required.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in config.Accounts)
        {
            var at = string.IsNullOrWhiteSpace(a.Id) ? "(account without id)" : a.Id;
            if (string.IsNullOrWhiteSpace(a.Id)) errors.Add("Every account needs an id.");
            else if (!Regex.IsMatch(a.Id, "^[a-z0-9][a-z0-9-]*$")) errors.Add($"{at}: id may contain only lowercase letters, digits and dashes.");
            else if (!ids.Add(a.Id)) errors.Add($"{at}: duplicate id.");
            if (string.IsNullOrWhiteSpace(a.Site)) errors.Add($"{at}: site is required.");
            if (string.IsNullOrWhiteSpace(a.Credential)) errors.Add($"{at}: credential is required.");
            if (string.IsNullOrWhiteSpace(a.Folder)) errors.Add($"{at}: folder is required.");
            if (!FileNameTemplate.HasDateToken(a.FileName)) errors.Add($"{at}: fileName must contain a {{date:FORMAT}} token.");
            try
            {
                _ = a.Schedule.Mode switch { ScheduleMode.Window => (object)a.Schedule.DayRange, ScheduleMode.Interval => a.Schedule.Interval, _ => 0 };
            }
            catch (FormatException ex) { errors.Add($"{at}: {ex.Message}"); }
            foreach (var act in a.Actions)
                if (!act.ContainsKey("type")) errors.Add($"{at}: every action needs a type.");
        }
        if (errors.Count > 0) throw new InvalidDataException("Config is not valid:\n  - " + string.Join("\n  - ", errors));
    }

    /// <summary>Lets <c>schedule: auto</c> be written as a plain scalar.</summary>
    private sealed class ScheduleConfigConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ScheduleConfig);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (string.IsNullOrWhiteSpace(scalar.Value) || scalar.Value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return new ScheduleConfig();
                throw new YamlException(scalar.Start, scalar.End, $"schedule must be 'auto' or a mapping (got '{scalar.Value}').");
            }
            var result = new ScheduleConfig();
            parser.Consume<MappingStart>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                var value = parser.Consume<Scalar>().Value;
                switch (key)
                {
                    case "dayOfMonth": result.DayOfMonth = value; break;
                    case "every": result.Every = value; break;
                    case "mode" when value.Equals("auto", StringComparison.OrdinalIgnoreCase): break;
                    default: throw new YamlException($"Unknown schedule key '{key}'. Use dayOfMonth or every.");
                }
            }
            return result;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException();
    }
}

/// <summary>Per-user locations for config and state.</summary>
public sealed record AppPaths(string ConfigFile, string DataDir)
{
    public static AppPaths Default(string? configOverride = null)
    {
        string configDir, dataDir;
        if (OperatingSystem.IsWindows())
        {
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "statement-downloader");
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "statement-downloader");
        }
        else if (OperatingSystem.IsMacOS())
        {
            configDir = dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "statement-downloader");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configDir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "statement-downloader");
            dataDir = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "statement-downloader");
        }
        var dataOverride = Environment.GetEnvironmentVariable("STATEMENT_DOWNLOADER_DATA");
        return new(configOverride ?? Path.Combine(configDir, "config.yaml"), dataOverride ?? dataDir);
    }

    public string ProfileDir(string accountId) => Path.Combine(DataDir, "profiles", accountId);
    public string DiagnosticsDir(string accountId) => Path.Combine(DataDir, "diagnostics", accountId);
    public string WorkDir => Path.Combine(DataDir, "work");
    /// <summary>Your own site recipes: a <c>sites</c> folder next to the config file.</summary>
    public string SitesDir => Path.Combine(Path.GetDirectoryName(ConfigFile) ?? ".", "sites");
    public string File(string name) => Path.Combine(DataDir, name);
}
