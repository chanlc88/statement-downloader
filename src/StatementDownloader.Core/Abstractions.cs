using Microsoft.Extensions.Logging;

namespace StatementDownloader.Core;

/// <summary>Username/password for a site, read from the secret store at run time only.</summary>
public sealed record SiteCredential(string Username, string Password)
{
    public override string ToString() => $"SiteCredential({Username}, ***)";
}

public interface ISecretProvider
{
    /// <summary>A username/password login, e.g. <c>bitwarden:&lt;id&gt;</c> or <c>env:NAME</c>.</summary>
    Task<SiteCredential> GetCredentialAsync(string reference, CancellationToken ct = default);
    /// <summary>A single secret value such as an API token.</summary>
    Task<string> GetValueAsync(string reference, CancellationToken ct = default);
}

public sealed record SiteContext
{
    public required AccountConfig Account { get; init; }
    public required SiteCredential Credential { get; init; }
    /// <summary>Persistent browser profile for this account on this machine.</summary>
    public required string ProfileDir { get; init; }
    /// <summary>Empty folder for this run's downloads.</summary>
    public required string WorkDir { get; init; }
    public required string DiagnosticsDir { get; init; }
    /// <summary>Documents already on record, so only new ones are downloaded.</summary>
    public IReadOnlyList<KnownDocument> Known { get; init; } = [];
    /// <summary>Only confirm the sign-in works (pre-flight / keep-alive).</summary>
    public bool SignInOnly { get; init; }
    /// <summary>Assisted sign-in: show the browser and wait for the person.</summary>
    public bool Interactive { get; init; }
    /// <summary>Other accounts share this sign-in (and run right after this one in the same browser).</summary>
    public bool SharedLogin { get; init; }
    /// <summary>Save a snapshot of each page visited after sign-in (for mapping a new site).</summary>
    public bool Capture { get; init; }
    public TimeSpan AssistTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>The mailbox sign-in codes are emailed to, when one is configured.</summary>
    public Mailbox? Mailbox { get; init; }
    public required ILogger Log { get; init; }
}

public sealed record SiteResult
{
    public required bool SignedIn { get; init; }
    public AuthFailureReason Failure { get; init; }
    public string? FailureDetail { get; init; }
    public DateTimeOffset? RememberedUntilUtc { get; init; }
    public IReadOnlyList<FoundDocument> Documents { get; init; } = [];
    /// <summary>Set when signed in but listing/downloading documents failed.</summary>
    public string? DocumentError { get; init; }
    public string? DiagnosticsPath { get; init; }

    public static SiteResult AuthFailed(AuthFailureReason reason, string? detail = null, string? diagnostics = null) =>
        new() { SignedIn = false, Failure = reason, FailureDetail = detail, DiagnosticsPath = diagnostics };
}

/// <summary>One implementation per website.</summary>
public interface ISiteFetcher
{
    string SiteId { get; }
    string DisplayName { get; }
    /// <summary>The site is only visited with the person present (assisted every time).</summary>
    bool NeedsPerson => false;
    Task<SiteResult> RunAsync(SiteContext context, CancellationToken ct = default);
}

public sealed record ActionContext
{
    public required AccountConfig Account { get; init; }
    public required DocumentRecord Document { get; init; }
    public required IReadOnlyDictionary<string, string> Settings { get; init; }
    public required ISecretProvider Secrets { get; init; }
    public required ILogger Log { get; init; }

    public string Require(string key) =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new InvalidDataException($"Action '{Settings.GetValueOrDefault("type")}' on {Account.Id} needs '{key}'.");

    public string? Optional(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

public sealed record ActionResult(bool Succeeded, string Summary)
{
    public static ActionResult Ok(string summary) => new(true, summary);
    public static ActionResult Fail(string summary) => new(false, summary);
}

/// <summary>Something done with each new document (record an expense, call a webhook…).</summary>
public interface IDocumentAction
{
    string Type { get; }
    Task<ActionResult> RunAsync(ActionContext context, CancellationToken ct = default);
}

/// <summary>Builds links into the local UI for notifications.</summary>
public sealed record UiLinks(string BaseUrl)
{
    public string Dashboard => BaseUrl;
    public string Account(string id) => $"{BaseUrl}/accounts/{Uri.EscapeDataString(id)}";
    public string Assist(string id) => $"{BaseUrl}/accounts/{Uri.EscapeDataString(id)}?assist=1";
    public string Review => $"{BaseUrl}/review";
}
