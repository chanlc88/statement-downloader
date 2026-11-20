namespace StatementDownloader.Core;

public enum AuthStatus
{
    /// <summary>Never signed in on this machine.</summary>
    Unknown,
    Healthy,
    /// <summary>The remembered sign-in lapses within the warning window.</summary>
    ExpiringSoon,
    /// <summary>Needs a person: verification code, CAPTCHA, or remembered sign-in expired.</summary>
    NeedsAssist,
    /// <summary>An assisted sign-in window is open.</summary>
    Assisting,
    /// <summary>Signing in again won't fix it: wrong password, site changed, site down.</summary>
    Broken,
}

public enum AuthFailureReason
{
    None,
    VerificationCode,
    Captcha,
    SessionExpired,
    PasswordRejected,
    SiteChanged,
    SiteUnavailable,
    /// <summary>Assisted sign-in window closed or timed out before the person finished.</summary>
    AssistAbandoned,
    /// <summary>The login couldn't be read from the secret store.</summary>
    CredentialUnavailable,
}

/// <summary>Sign-in state of one account on this machine.</summary>
public sealed record VendorAuthState
{
    public required string AccountId { get; init; }
    public AuthStatus Status { get; init; } = AuthStatus.Unknown;
    public AuthFailureReason LastFailure { get; init; }
    public string? LastFailureDetail { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    /// <summary>When the remembered sign-in is expected to lapse.</summary>
    public DateTimeOffset? RememberedUntilUtc { get; init; }
}

/// <summary>One sign-in attempt, kept as history.</summary>
public sealed record AuthEvent
{
    public required string AccountId { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required bool Succeeded { get; init; }
    public bool Assisted { get; init; }
    public AuthFailureReason Reason { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset? RememberedUntilUtc { get; init; }
    public string Machine { get; init; } = Environment.MachineName;
}

/// <summary>Pure state transitions, separate from storage so they're easy to test.</summary>
public static class AuthTransitions
{
    public static VendorAuthState Succeeded(VendorAuthState s, DateTimeOffset now, DateTimeOffset? rememberedUntil, TimeSpan warnWindow)
    {
        var until = rememberedUntil ?? s.RememberedUntilUtc;
        return s with
        {
            Status = until is { } r && r - now <= warnWindow ? AuthStatus.ExpiringSoon : AuthStatus.Healthy,
            LastFailure = AuthFailureReason.None,
            LastFailureDetail = null,
            LastSuccessUtc = now,
            RememberedUntilUtc = until,
        };
    }

    public static VendorAuthState Failed(VendorAuthState s, DateTimeOffset now, AuthFailureReason reason, string? detail) => s with
    {
        Status = NeedsPerson(reason) ? AuthStatus.NeedsAssist : AuthStatus.Broken,
        LastFailure = reason,
        LastFailureDetail = detail,
        LastFailureUtc = now,
    };

    /// <summary>Re-evaluates a working sign-in against its remembered-until date.</summary>
    public static VendorAuthState CheckExpiry(VendorAuthState s, DateTimeOffset now, TimeSpan warnWindow)
    {
        if (s.Status is not (AuthStatus.Healthy or AuthStatus.ExpiringSoon) || s.RememberedUntilUtc is not { } until)
            return s;
        if (until <= now) return s with { Status = AuthStatus.NeedsAssist, LastFailure = AuthFailureReason.SessionExpired, LastFailureUtc = now };
        return s with { Status = until - now <= warnWindow ? AuthStatus.ExpiringSoon : AuthStatus.Healthy };
    }

    public static bool NeedsPerson(AuthFailureReason r) =>
        r is AuthFailureReason.VerificationCode or AuthFailureReason.Captcha or AuthFailureReason.SessionExpired or AuthFailureReason.AssistAbandoned;

    /// <summary>Plain-language explanation and next step for a failure, used in notifications and the UI.</summary>
    public static (string What, string ToDo) Explain(AuthFailureReason reason) => reason switch
    {
        AuthFailureReason.VerificationCode => ("the site asked for a verification code", "Choose Sign in now and enter the code; tick \"remember this device\" if offered."),
        AuthFailureReason.Captcha => ("the site showed a CAPTCHA", "Choose Sign in now and complete it."),
        AuthFailureReason.SessionExpired => ("the remembered sign-in expired", "Choose Sign in now to sign in again and renew it."),
        AuthFailureReason.AssistAbandoned => ("the sign-in window was closed before signing in finished", "Choose Sign in now to try again."),
        AuthFailureReason.PasswordRejected => ("the site rejected the username or password", "Update the login in your password manager, then choose Check now."),
        AuthFailureReason.SiteChanged => ("the site's pages no longer look as expected", "The site fetcher needs updating; a screenshot was saved to the diagnostics folder."),
        AuthFailureReason.SiteUnavailable => ("the site could not be reached or returned an error", "Nothing to do yet; it will retry on the next check."),
        AuthFailureReason.CredentialUnavailable => ("the login couldn't be read from your password manager", "Check the account's credential reference with: statement-downloader secrets test <reference>."),
        _ => ("an unknown problem occurred", "Check the app log."),
    };
}

public interface IAuthStateStore
{
    Task<VendorAuthState> GetAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorAuthState>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(VendorAuthState state, CancellationToken ct = default);
    Task AppendEventAsync(AuthEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<AuthEvent>> GetEventsAsync(string? accountId = null, CancellationToken ct = default);
}

public sealed class JsonAuthStateStore(AppPaths paths) : IAuthStateStore
{
    private string StatePath => paths.File("auth-state.json");
    private string EventsPath => paths.File("auth-events.json");

    public async Task<VendorAuthState> GetAsync(string accountId, CancellationToken ct = default) =>
        (await GetAllAsync(ct)).FirstOrDefault(s => s.AccountId == accountId) ?? new VendorAuthState { AccountId = accountId };

    public async Task<IReadOnlyList<VendorAuthState>> GetAllAsync(CancellationToken ct = default) =>
        await JsonFile.ReadAsync<List<VendorAuthState>>(StatePath, ct) ?? [];

    public Task SaveAsync(VendorAuthState state, CancellationToken ct = default) =>
        JsonFile.UpdateAsync<List<VendorAuthState>>(StatePath, all =>
        {
            all.RemoveAll(s => s.AccountId == state.AccountId);
            all.Add(state);
        }, ct);

    public Task AppendEventAsync(AuthEvent evt, CancellationToken ct = default) =>
        JsonFile.UpdateAsync<List<AuthEvent>>(EventsPath, all => all.Add(evt), ct);

    public async Task<IReadOnlyList<AuthEvent>> GetEventsAsync(string? accountId = null, CancellationToken ct = default)
    {
        var all = await JsonFile.ReadAsync<List<AuthEvent>>(EventsPath, ct) ?? [];
        return accountId is null ? all : all.Where(e => e.AccountId == accountId).ToList();
    }
}
