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

/// <summary>
/// Email via SMTP. Settings: smtpHost, smtpPort, from; optional to (comma-separated), username,
/// passwordSecret, ssl (default true). A notification for a group with addresses in
/// <c>notifications.groups</c> goes to those addresses instead of <c>to</c>.
/// </summary>
public sealed class EmailChannel(string name, IReadOnlyDictionary<string, string> settings, ISecretProvider secrets) : IEmailChannel
{
    public string Name => name;
    public bool HasDefaultRecipient => DefaultRecipients.Count > 0;
    private List<string> DefaultRecipients => (settings.GetValueOrDefault("to") ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Who <paramref name="n"/> goes to: its group's addresses, else the channel's <c>to</c>.</summary>
    public IReadOnlyList<string> RecipientsFor(Notification n) => n.EmailTo is { Count: > 0 } to ? to : DefaultRecipients;

    public static (string Subject, string Body) Compose(Notification n)
    {
        var prefix = n.Level switch { NotificationLevel.ActionNeeded => "[Action needed] ", NotificationLevel.HeadsUp => "[Heads-up] ", _ => "" };
        var body = n.Message + (n.Link is null ? "" : $"\n\nOpen: {n.Link}") + "\n\n— Statement Downloader";
        return (prefix + n.Title, body);
    }

    public async Task SendAsync(Notification n, CancellationToken ct = default)
    {
        string Need(string key) => settings.GetValueOrDefault(key) ?? throw new InvalidDataException($"email channel '{name}' needs {key}.");
        var to = RecipientsFor(n);
        if (to.Count == 0)
            throw new InvalidDataException($"email channel '{name}' has no recipient: set its 'to', or email addresses for the account's group in notifications.groups.");
        var host = Need("smtpHost");
        using var client = new SmtpClient(host, int.Parse(settings.GetValueOrDefault("smtpPort") ?? "587", CultureInfo.InvariantCulture))
        {
            EnableSsl = !string.Equals(settings.GetValueOrDefault("ssl"), "false", StringComparison.OrdinalIgnoreCase),
        };
        if (settings.GetValueOrDefault("username") is { } user)
        {
            var password = await secrets.GetValueAsync(Need("passwordSecret"), ct);
            // Gmail shows app passwords in groups of four; the spaces aren't part of it.
            if (host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase)) password = password.Replace(" ", "");
            client.Credentials = new NetworkCredential(user, password);
        }

        var (subject, body) = Compose(n);
        using var msg = new MailMessage { From = new MailAddress(Need("from")), Subject = subject, Body = body };
        foreach (var address in to) msg.To.Add(address);
        await client.SendMailAsync(msg, ct);
    }
}
