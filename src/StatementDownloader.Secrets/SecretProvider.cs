using System.Text.Json;
using Bitwarden.Sdk;
using StatementDownloader.Core;

namespace StatementDownloader.Secrets;

/// <summary>
/// Resolves secret references:
/// <list type="bullet">
/// <item><c>bitwarden:&lt;secret-id&gt;</c> — Bitwarden Secrets Manager.</item>
/// <item><c>keychain:&lt;name&gt;</c> — the OS credential store.</item>
/// <item><c>env:&lt;VARIABLE&gt;</c> — an environment variable (development).</item>
/// </list>
/// Logins are JSON <c>{"username":"…","password":"…"}</c>, or (Bitwarden only) the password as
/// the value with the username on the first line of the note.
/// </summary>
public sealed class SecretProvider(BitwardenConfig bitwarden, AppPaths paths, ITokenStore tokens) : ISecretProvider, IDisposable
{
    public const string BitwardenTokenName = "bitwarden-access-token";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BitwardenClient? _client;

    public async Task<SiteCredential> GetCredentialAsync(string reference, CancellationToken ct = default)
    {
        var (value, note) = await ResolveAsync(reference, ct);
        if (TryParseJsonLogin(value) is { } fromJson) return fromJson;
        var username = note?.Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(username))
            throw new InvalidDataException($"Secret {reference} isn't a login: use JSON {{\"username\":…,\"password\":…}} or put the username on the first line of the note.");
        return new SiteCredential(username, value);
    }

    public async Task<string> GetValueAsync(string reference, CancellationToken ct = default) => (await ResolveAsync(reference, ct)).Value;

    private async Task<(string Value, string? Note)> ResolveAsync(string reference, CancellationToken ct)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0) throw new InvalidDataException($"Secret reference '{reference}' needs a prefix: bitwarden:, keychain: or env:.");
        var kind = reference[..colon];
        var key = reference[(colon + 1)..];
        switch (kind)
        {
            case "env":
                return (Environment.GetEnvironmentVariable(key) ?? throw new InvalidDataException($"Environment variable {key} is not set."), null);
            case "keychain":
                return (tokens.Get(key) ?? throw new InvalidDataException($"No '{key}' in {tokens.Description}."), null);
            case "bitwarden":
                if (!Guid.TryParse(key, out var id)) throw new InvalidDataException($"'{key}' is not a Bitwarden secret id.");
                var client = await GetClientAsync(ct);
                var secret = await Task.Run(() => client.Secrets.Get(id), ct);
                return (secret.Value, secret.Note);
            default:
                throw new InvalidDataException($"Unknown secret reference type '{kind}'.");
        }
    }

    private async Task<BitwardenClient> GetClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;
            var token = tokens.Get(BitwardenTokenName)
                ?? throw new InvalidDataException($"No Bitwarden access token in {tokens.Description}. Run: statement-downloader secrets set-token");
            var client = new BitwardenClient(new BitwardenSettings { ApiUrl = bitwarden.ApiUrl, IdentityUrl = bitwarden.IdentityUrl });
            var stateDir = Path.Combine(paths.DataDir, "bitwarden");
            Directory.CreateDirectory(stateDir);
            await Task.Run(() => client.Auth.LoginAccessToken(token, Path.Combine(stateDir, "state")), ct);
            return _client = client;
        }
        finally { _gate.Release(); }
    }

    public static SiteCredential? TryParseJsonLogin(string value)
    {
        if (!value.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            string? Get(params string[] names) => names.Select(n => root.TryGetProperty(n, out var p) ? p.GetString() : null).FirstOrDefault(v => v is not null);
            var user = Get("username", "user", "login");
            var pass = Get("password", "pass");
            return user is null || pass is null ? null : new SiteCredential(user, pass);
        }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _client?.Dispose();
}
