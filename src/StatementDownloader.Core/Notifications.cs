using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace StatementDownloader.Core;

public enum NotificationLevel { Info, HeadsUp, ActionNeeded }

public sealed record Notification
{
    public required NotificationLevel Level { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? AccountId { get; init; }
    /// <summary>Link to open (e.g. the UI's sign-in page for the account).</summary>
    public string? Link { get; init; }
}

public interface INotificationChannel
{
    string Name { get; }
    Task SendAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>Something waiting on the user, keyed to its cause so it's reported once.</summary>
public sealed class Issue
{
    public string Key { get; set; } = "";
    public NotificationLevel Level { get; set; }
    public string? AccountId { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Link { get; set; }
    public DateTimeOffset OpenedUtc { get; set; }
    public DateTimeOffset? LastSentUtc { get; set; }
    public int Reminders { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
    public bool IsOpen => ResolvedUtc is null;
}

public sealed class HeldNotification
{
    public Notification Notification { get; set; } = null!;
    public DateTimeOffset ReleaseAfterUtc { get; set; }
    public bool Digest { get; set; }
    /// <summary>The issue it announces, if any: dropped if that issue is resolved before release.</summary>
    public string? IssueKey { get; set; }
    /// <summary>When it was held (null in data from older versions).</summary>
    public DateTimeOffset? QueuedUtc { get; set; }
}

public sealed class NotificationLogEntry
{
    public DateTimeOffset AtUtc { get; set; }
    public Notification Notification { get; set; } = null!;
    public List<string> Channels { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class NotificationData
{
    public List<Issue> Issues { get; set; } = [];
    public List<HeldNotification> Held { get; set; } = [];
    public List<NotificationLogEntry> Log { get; set; } = [];
}

/// <summary>
/// Routes notifications to channels by level, tracks issues (one notification per cause plus
/// reminders), and holds pushes during quiet hours or for the daily digest.
/// </summary>
public sealed class NotificationCenter(
    IEnumerable<INotificationChannel> channels,
    NotificationConfig config,
    ScheduleDefaults defaults,
    AppPaths paths,
    TimeProvider clock,
    TimeZoneInfo zone,
    ILogger<NotificationCenter> log)
{
    private const int MaxLog = 500;
    private readonly Dictionary<string, INotificationChannel> _channels = channels.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    private string FilePath => paths.File("notifications.json");

    public Task<NotificationData> ReadAsync(CancellationToken ct = default) =>
        JsonFile.ReadAsync<NotificationData>(FilePath, ct).ContinueWith(t => t.Result ?? new NotificationData(), ct);

    /// <summary>Opens an issue and notifies, unless an issue with this key is already open (then only its text is refreshed).</summary>
    public async Task RaiseAsync(string key, Notification n, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var isNew = await JsonFile.UpdateAsync<NotificationData, bool>(FilePath, data =>
        {
            var open = data.Issues.FirstOrDefault(i => i.Key == key && i.IsOpen);
            if (open is not null)
            {
                open.Title = n.Title; open.Message = n.Message; open.Level = n.Level; open.Link = n.Link;
                return false;
            }
            data.Issues.Add(new Issue { Key = key, Level = n.Level, AccountId = n.AccountId, Title = n.Title, Message = n.Message, Link = n.Link, OpenedUtc = now, LastSentUtc = now });
            return true;
        }, ct);
        if (isNew) await NotifyAsync(n, ct, key);
    }

    /// <summary>Resolves an open issue; sends an info notice if <paramref name="resolvedMessage"/> is given.</summary>
    public async Task ResolveAsync(string key, string? resolvedMessage = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (resolved, unannounced) = await JsonFile.UpdateAsync<NotificationData, (Issue?, bool)>(FilePath, data =>
        {
            var open = data.Issues.FirstOrDefault(i => i.Key == key && i.IsOpen);
            if (open is not null) open.ResolvedUtc = now;
            // Still held (quiet hours): it was never sent, so neither it nor the "resolved" notice is.
            var held = data.Held.RemoveAll(h => h.IssueKey == key) > 0;
            return (open, held);
        }, ct);
        if (resolved is not null && resolvedMessage is not null && !unannounced)
            await NotifyAsync(new Notification { Level = NotificationLevel.Info, Title = "Resolved: " + resolved.Title, Message = resolvedMessage, AccountId = resolved.AccountId }, ct);
    }

    /// <summary>Resolves every open issue without notices (the person cleared them). Returns how many.</summary>
    public Task<int> ResolveAllAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return JsonFile.UpdateAsync<NotificationData, int>(FilePath, data =>
        {
            var open = data.Issues.Where(i => i.IsOpen).ToList();
            foreach (var i in open) i.ResolvedUtc = now;
            data.Held.RemoveAll(h => h.IssueKey is not null && open.Any(i => i.Key == h.IssueKey));
            return open.Count;
        }, ct);
    }

    /// <summary>Empties the list of sent notifications and resolved issues (open issues stay).</summary>
    public Task ClearHistoryAsync(CancellationToken ct = default) =>
        JsonFile.UpdateAsync<NotificationData>(FilePath, data =>
        {
            data.Log.Clear();
            data.Issues.RemoveAll(i => !i.IsOpen);
        }, ct);

    public async Task<IReadOnlyList<Issue>> OpenIssuesAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct)).Issues.Where(i => i.IsOpen).ToList();

    /// <summary>Sends a test message to every channel now (ignores routes and quiet hours). Returns each channel's result.</summary>
    public async Task<IReadOnlyList<(string Channel, string? Error)>> SendTestAsync(CancellationToken ct = default)
    {
        var n = new Notification
        {
            Level = NotificationLevel.Info,
            Title = "Statement Downloader test",
            Message = $"Notifications from {Environment.MachineName} are working.",
        };
        var results = new List<(string, string?)>();
        foreach (var ch in _channels.Values)
        {
            try { await ch.SendAsync(n, ct); results.Add((ch.Name, null)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { results.Add((ch.Name, ex.Message)); }
        }
        return results;
    }

    public async Task NotifyAsync(Notification n, CancellationToken ct = default, string? issueKey = null)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? holdUntil = null;
        var digest = false;
        if (config.Digest && n.Level == NotificationLevel.Info) { holdUntil = NextDigest(now); digest = true; }
        else if (QuietUntil(now) is { } q) holdUntil = q;

        if (holdUntil is { } until)
        {
            await JsonFile.UpdateAsync<NotificationData>(FilePath, d => d.Held.Add(new HeldNotification { Notification = n, ReleaseAfterUtc = until, Digest = digest, IssueKey = issueKey, QueuedUtc = now }), ct);
            log.LogInformation("Held notification until {Until}: {Title}", until, n.Title);
            return;
        }
        await SendNowAsync(n, ct);
    }

    /// <summary>Sends reminders for open action-needed issues and releases held notifications. Call on every tick.</summary>
    public async Task SendDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (QuietUntil(now) is not null) return;

        var interval = TimeSpan.FromHours(Math.Max(1, defaults.ReminderHours));
        var (reminders, released) = await JsonFile.UpdateAsync<NotificationData, (List<Notification>, List<HeldNotification>)>(FilePath, data =>
        {
            var due = new List<Notification>();
            foreach (var i in data.Issues.Where(i => i.IsOpen && i.Level == NotificationLevel.ActionNeeded && now - (i.LastSentUtc ?? i.OpenedUtc) >= interval))
            {
                i.LastSentUtc = now; i.Reminders++;
                var days = (int)(now - i.OpenedUtc).TotalDays;
                due.Add(new Notification { Level = i.Level, Title = "Reminder: " + i.Title, Message = i.Message + (days >= 1 ? $" (open for {days} day{(days == 1 ? "" : "s")})" : ""), AccountId = i.AccountId, Link = i.Link });
            }
            var rel = data.Held.Where(h => h.ReleaseAfterUtc <= now
                                           && (h.IssueKey is null || data.Issues.Any(i => i.Key == h.IssueKey && i.IsOpen))).ToList();
            data.Held.RemoveAll(h => h.ReleaseAfterUtc <= now);
            return (due, rel);
        }, ct);

        foreach (var r in reminders) await SendNowAsync(r, ct);
        foreach (var h in released.Where(h => !h.Digest)) await SendNowAsync(h.Notification, ct);
        var digestItems = released.Where(h => h.Digest).Select(h => h.Notification).ToList();
        if (digestItems.Count > 0)
            await SendNowAsync(new Notification
            {
                Level = NotificationLevel.Info,
                Title = $"Statement Downloader daily summary ({digestItems.Count})",
                Message = string.Join("\n", digestItems.Select(d => $"• {d.Title} — {d.Message}")),
            }, ct);
    }

    private async Task SendNowAsync(Notification n, CancellationToken ct)
    {
        var levelKey = n.Level switch { NotificationLevel.ActionNeeded => "actionNeeded", NotificationLevel.HeadsUp => "headsUp", _ => "info" };
        var names = config.Routes.TryGetValue(levelKey, out var r) ? r : [];
        var sentTo = new List<string>();
        string? error = null;
        log.Log(n.Level == NotificationLevel.ActionNeeded ? LogLevel.Warning : LogLevel.Information, "[{Level}] {Title}: {Message}", n.Level, n.Title, n.Message);
        foreach (var name in names)
        {
            if (!_channels.TryGetValue(name, out var ch)) { error = $"No channel named '{name}'."; continue; }
            try { await ch.SendAsync(n, ct); sentTo.Add(name); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"{name}: {ex.Message}";
                log.LogError(ex, "Notification channel {Channel} failed", name);
            }
        }
        await JsonFile.UpdateAsync<NotificationData>(FilePath, d =>
        {
            d.Log.Add(new NotificationLogEntry { AtUtc = clock.GetUtcNow(), Notification = n, Channels = sentTo, Error = error });
            if (d.Log.Count > MaxLog) d.Log.RemoveRange(0, d.Log.Count - MaxLog);
        }, ct);
    }

    /// <summary>If <paramref name="now"/> is in quiet hours, when they end.</summary>
    public DateTimeOffset? QuietUntil(DateTimeOffset now)
    {
        var m = Regex.Match(config.QuietHours ?? "", @"^\s*(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})\s*$");
        if (!m.Success) return null;
        var start = new TimeOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        var end = new TimeOnly(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var t = TimeOnly.FromDateTime(local.DateTime);
        var inQuiet = start <= end ? t >= start && t < end : t >= start || t < end;
        if (!inQuiet) return null;
        var endDate = DateOnly.FromDateTime(local.DateTime);
        if (start > end && t >= start) endDate = endDate.AddDays(1);
        var endLocal = endDate.ToDateTime(end);
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
    }

    private DateTimeOffset NextDigest(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var at = local.Date.AddHours(Math.Clamp(config.DigestHour, 0, 23));
        if (at <= local.DateTime) at = at.AddDays(1);
        return new DateTimeOffset(at, zone.GetUtcOffset(at));
    }
}
