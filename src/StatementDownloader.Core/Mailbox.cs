using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace StatementDownloader.Core;

/// <summary>
/// The mailbox that sign-in codes are emailed to, read over IMAP (for Gmail: imap.gmail.com with an
/// app password). Only read: messages are never changed, moved or deleted.
/// </summary>
public sealed class MailboxConfig
{
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public string Username { get; set; } = "";
    /// <summary>Secret reference for the (app) password, e.g. <c>bitwarden:&lt;id&gt;</c>.</summary>
    public string Password { get; set; } = "";
}

public sealed record MailSummary(DateTimeOffset Date, string From, string Subject, string Text);

public sealed class Mailbox(MailboxConfig config, ISecretProvider secrets)
{
    /// <summary>Messages in the inbox received at or after <paramref name="since"/> whose sender matches <paramref name="from"/>, newest first.</summary>
    public async Task<IReadOnlyList<MailSummary>> RecentAsync(string from, DateTimeOffset since, int max = 5, CancellationToken ct = default)
    {
        // App passwords are shown in groups of four; the spaces aren't part of it.
        var password = (await secrets.GetValueAsync(config.Password, ct)).Replace(" ", "");
        using var client = new ImapClient();
        await client.ConnectAsync(config.Host, config.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(config.Username, password, ct);
        try
        {
            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            // IMAP dates are whole days: search from the day before, then compare the exact times.
            var ids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)), ct);
            var found = new List<MailSummary>();
            foreach (var id in ids.Reverse())
            {
                var message = await inbox.GetMessageAsync(id, ct);
                if (message.Date < since) break;
                var sender = message.From.ToString();
                if (!Regex.IsMatch(sender, from, RegexOptions.IgnoreCase)) continue;
                found.Add(new MailSummary(message.Date, sender, message.Subject ?? "", TextOf(message)));
                if (found.Count >= max) break;
            }
            return found;
        }
        finally { await client.DisconnectAsync(true, ct); }
    }

    /// <summary>
    /// Waits for a code email sent after <paramref name="since"/> and returns group 1 of
    /// <paramref name="pattern"/> from its subject or text, or null if none arrives in time.
    /// </summary>
    public async Task<string?> WaitForCodeAsync(string from, string pattern, DateTimeOffset since, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var m in await RecentAsync(from, since, 3, ct))
                if (Regex.Match(m.Subject + "\n" + m.Text, pattern, RegexOptions.IgnoreCase) is { Success: true } match)
                    return match.Groups[1].Value;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static string TextOf(MimeMessage message)
    {
        if (message.TextBody is { Length: > 0 } text) return text;
        var html = message.HtmlBody ?? "";
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();
    }
}
