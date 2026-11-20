using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using StatementDownloader.Core;

namespace StatementDownloader.Actions;

public static class NotificationChannels
{
    /// <summary>Builds channels from the config's <c>notifications.channels</c> list.</summary>
    public static IReadOnlyList<INotificationChannel> Create(IEnumerable<Dictionary<string, string>> definitions, ISecretProvider secrets, HttpClient http) =>
        definitions.Select(d =>
        {
            var type = d.GetValueOrDefault("type") ?? throw new InvalidDataException("Every notification channel needs a type.");
            var name = d.GetValueOrDefault("name") ?? type;
            return type switch
            {
                "ntfy" => (INotificationChannel)new NtfyChannel(name, d, secrets, http),
                "email" => new EmailChannel(name, d, secrets),
                _ => throw new InvalidDataException($"Unknown notification channel type '{type}'. Use ntfy or email."),
            };
        }).ToList();
}

/// <summary>Push notifications through ntfy (ntfy.sh or self-hosted). Settings: server, topic; optional tokenSecret.</summary>
public sealed class NtfyChannel(string name, IReadOnlyDictionary<string, string> settings, ISecretProvider secrets, HttpClient http) : INotificationChannel
{
    public string Name => name;

    public async Task SendAsync(Notification n, CancellationToken ct = default)
    {
        var server = (settings.GetValueOrDefault("server") ?? "https://ntfy.sh").TrimEnd('/');
        var topic = settings.GetValueOrDefault("topic") ?? throw new InvalidDataException($"ntfy channel '{name}' needs a topic.");
        // JSON publishing keeps titles UTF-8 safe (headers would need encoding).
        using var req = new HttpRequestMessage(HttpMethod.Post, server)
        {
            Content = JsonContent.Create(new
            {
                topic,
                title = n.Title,
                message = n.Message,
                priority = n.Level switch { NotificationLevel.ActionNeeded => 4, NotificationLevel.HeadsUp => 3, _ => 2 },
                tags = new[] { n.Level switch { NotificationLevel.ActionNeeded => "warning", NotificationLevel.HeadsUp => "hourglass", _ => "page_facing_up" } },
                click = n.Link,
            }),
        };
        if (settings.GetValueOrDefault("tokenSecret") is { } secret)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await secrets.GetValueAsync(secret, ct));
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}

/// <summary>Email via SMTP. Settings: smtpHost, smtpPort, from, to; optional username, passwordSecret, ssl (default true).</summary>
public sealed class EmailChannel(string name, IReadOnlyDictionary<string, string> settings, ISecretProvider secrets) : INotificationChannel
{
    public string Name => name;

    public async Task SendAsync(Notification n, CancellationToken ct = default)
    {
        string Need(string key) => settings.GetValueOrDefault(key) ?? throw new InvalidDataException($"email channel '{name}' needs {key}.");
        using var client = new SmtpClient(Need("smtpHost"), int.Parse(settings.GetValueOrDefault("smtpPort") ?? "587", CultureInfo.InvariantCulture))
        {
            EnableSsl = !string.Equals(settings.GetValueOrDefault("ssl"), "false", StringComparison.OrdinalIgnoreCase),
        };
        if (settings.GetValueOrDefault("username") is { } user)
            client.Credentials = new NetworkCredential(user, await secrets.GetValueAsync(Need("passwordSecret"), ct));

        var prefix = n.Level switch { NotificationLevel.ActionNeeded => "[Action needed] ", NotificationLevel.HeadsUp => "[Heads-up] ", _ => "" };
        var body = n.Message + (n.Link is null ? "" : $"\n\nOpen: {n.Link}") + "\n\n— Statement Downloader";
        using var msg = new MailMessage(Need("from"), Need("to"), prefix + n.Title, body);
        await client.SendMailAsync(msg, ct);
    }
}
