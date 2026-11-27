using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StatementDownloader.Core;

public enum ReviewState { None, Pending, Approved, Rejected }

/// <summary>A filed document and everything known about it.</summary>
public sealed class DocumentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public DocumentKind Kind { get; set; }
    public DateOnly DocumentDate { get; set; }
    public string? StatementNumber { get; set; }
    public decimal? Amount { get; set; }
    public string? VendorAccountNumber { get; set; }
    public string Path { get; set; } = "";
    public string? Sha256 { get; set; }
    /// <summary>When the app first found it online. Null for documents imported from disk.</summary>
    public DateTimeOffset? FirstSeenUtc { get; set; }
    public bool Imported { get; set; }
    /// <summary>
    /// Downloaded when filling a gap in the history. Actions don't run on it (it's an old document)
    /// unless the person asks (<see cref="ActionsRequested"/>).
    /// </summary>
    public bool Backfilled { get; set; }
    /// <summary>The person asked for the actions to run on this backfilled document.</summary>
    public bool ActionsRequested { get; set; }
    public ReviewState Review { get; set; }
    public List<ActionRecord> Actions { get; set; } = [];
}

public sealed class ActionRecord
{
    /// <summary>Stable identity of the configured action (<see cref="ActionKeys"/>); null in records from older versions.</summary>
    public string? Key { get; set; }
    /// <summary>Position of the action in the account's action list when it last ran (informational).</summary>
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? Summary { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset LastAttemptUtc { get; set; }
}

/// <summary>A document a site fetcher downloaded into the work folder, not yet filed.</summary>
public sealed record FoundDocument
{
    public required string TempPath { get; init; }
    public required DateOnly DocumentDate { get; init; }
    public string? StatementNumber { get; init; }
    public decimal? Amount { get; init; }
    public string? VendorAccountNumber { get; init; }
}

/// <summary>What a site fetcher is told about documents already on record.</summary>
public sealed record KnownDocument(DateOnly DocumentDate, string? StatementNumber);

public interface IDocumentStore
{
    Task<IReadOnlyList<DocumentRecord>> GetAsync(string? accountId = null, CancellationToken ct = default);
    Task AddAsync(DocumentRecord record, CancellationToken ct = default);
    Task UpdateAsync(DocumentRecord record, CancellationToken ct = default);
}

public sealed class JsonDocumentStore(AppPaths paths) : IDocumentStore
{
    private string FilePath => paths.File("documents.json");

    public async Task<IReadOnlyList<DocumentRecord>> GetAsync(string? accountId = null, CancellationToken ct = default)
    {
        var all = await JsonFile.ReadAsync<List<DocumentRecord>>(FilePath, ct) ?? [];
        return accountId is null ? all : all.Where(d => d.AccountId == accountId).OrderBy(d => d.DocumentDate).ToList();
    }

    public Task AddAsync(DocumentRecord record, CancellationToken ct = default) =>
        JsonFile.UpdateAsync<List<DocumentRecord>>(FilePath, all => all.Add(record), ct);

    public Task UpdateAsync(DocumentRecord record, CancellationToken ct = default) =>
        JsonFile.UpdateAsync<List<DocumentRecord>>(FilePath, all =>
        {
            var i = all.FindIndex(d => d.Id == record.Id);
            if (i >= 0) all[i] = record; else all.Add(record);
        }, ct);
}

/// <summary>Renders folder / file-name templates and reads dates back out of existing file names.</summary>
public static partial class FileNameTemplate
{
    [GeneratedRegex(@"\{date:([^}]+)\}")]
    private static partial Regex DateToken();

    public static bool HasDateToken(string? template) => template is not null && DateToken().IsMatch(template);

    public static string Render(string template, AccountConfig account, DateOnly date, string? statement)
    {
        var s = DateToken().Replace(template, m => date.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return s.Replace("{year}", date.Year.ToString("D4"))
                .Replace("{month}", date.Month.ToString("D2"))
                .Replace("{statement}", Sanitize(statement ?? "unknown"))
                .Replace("{kind}", account.KindWord)
                .Replace("{account}", account.Id);
    }

    /// <summary>Returns the document date encoded in <paramref name="fileName"/>, or null if it doesn't match the template.</summary>
    public static DateOnly? TryParseDate(string template, string fileName)
    {
        // With several date parts (e.g. "{date:MMMM}_Statement_{date:yyyy-MM-dd}"), the date is
        // read from the most complete one; each part matches its own format.
        var dates = DateToken().Matches(template);
        if (dates.Count == 0) return null;
        var main = dates.MaxBy(d => "yMd".Count(d.Groups[1].Value.Contains))!;
        var format = main.Groups[1].Value;
        var pattern = new StringBuilder("^");
        var pos = 0;
        foreach (Match token in Regex.Matches(template, @"\{[^}]+\}"))
        {
            pattern.Append(Regex.Escape(template[pos..token.Index]));
            pattern.Append(!token.Value.StartsWith("{date:") ? ".*?"
                : token.Index == main.Index ? "(?<date>" + FormatToRegex(format) + ")"
                : "(?:" + FormatToRegex(token.Value[6..^1]) + ")");
            pos = token.Index + token.Length;
        }
        pattern.Append(Regex.Escape(template[pos..])).Append('$');
        var match = Regex.Match(fileName, pattern.ToString(), RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return DateOnly.TryParseExact(match.Groups["date"].Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string FormatToRegex(string format)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            var run = 1;
            while (i + run < format.Length && format[i + run] == c) run++;
            sb.Append(c switch
            {
                'y' => $@"\d{{{run}}}",
                'M' when run >= 3 => "[A-Za-z]+",
                'M' or 'd' => run == 1 ? @"\d{1,2}" : @"\d{2}",
                _ => Regex.Escape(new string(c, run)),
            });
            i += run;
        }
        return sb.ToString();
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

public enum FileOutcome { Filed, AlreadyOnDisk, NameClash }

/// <summary>What a document says about itself: the date, number and amount printed on it.</summary>
public sealed record DocumentIdentity(DateOnly DocumentDate, string? StatementNumber, decimal? Amount)
{
    /// <summary>The same statement: same date, and the same number and amount where both show one.</summary>
    public bool SameAs(FoundDocument doc) =>
        DocumentDate == doc.DocumentDate
        && (StatementNumber is null || doc.StatementNumber is null || StatementNumber == doc.StatementNumber)
        && (Amount is null || doc.Amount is null || Amount == doc.Amount);
}

public sealed record FiledDocument(FileOutcome Outcome, string Path, string Sha256);

/// <summary>Moves a downloaded document into the account's folder.</summary>
public sealed class DocumentFiler(string documentsRoot)
{
    public string TargetPath(AccountConfig account, FoundDocument doc) =>
        AccountLocation.Resolve(documentsRoot,
            FileNameTemplate.Render(account.Folder, account, doc.DocumentDate, doc.StatementNumber),
            FileNameTemplate.Render(account.FileName, account, doc.DocumentDate, doc.StatementNumber));

    /// <param name="identify">Reads a document on disk (its date, number and amount), so a file that
    /// already has the name and is the same statement (e.g. one the site regenerated, which differs
    /// by a few bytes) counts as already on disk instead of a clash.</param>
    public async Task<FiledDocument> FileAsync(AccountConfig account, FoundDocument doc, CancellationToken ct = default, Func<string, DocumentIdentity?>? identify = null)
    {
        var target = TargetPath(account, doc);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var hash = await HashAsync(doc.TempPath, ct);

        if (File.Exists(target))
        {
            if (await HashAsync(target, ct) == hash)
            {
                File.Delete(doc.TempPath);
                return new(FileOutcome.AlreadyOnDisk, target, hash);
            }
            if (identify?.Invoke(target) is { } onDisk && onDisk.SameAs(doc))
            {
                File.Delete(doc.TempPath);
                return new(FileOutcome.AlreadyOnDisk, target, await HashAsync(target, ct));
            }
            var stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, System.IO.Path.GetFileNameWithoutExtension(target));
            var ext = System.IO.Path.GetExtension(target);
            var n = 2;
            while (File.Exists($"{stem} ({n}){ext}")) n++;
            target = $"{stem} ({n}){ext}";
            File.Move(doc.TempPath, target);
            return new(FileOutcome.NameClash, target, hash);
        }

        File.Move(doc.TempPath, target);
        return new(FileOutcome.Filed, target, hash);
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }
}

/// <summary>Seeds document history from files already in an account's folder.</summary>
public static class HistoryImporter
{
    public static IEnumerable<(string Path, DateOnly Date)> Scan(string documentsRoot, AccountConfig account)
    {
        foreach (var dir in ExpandFolders(documentsRoot, account.Folder))
            foreach (var file in Directory.EnumerateFiles(dir))
                if (FileNameTemplate.TryParseDate(account.FileName, System.IO.Path.GetFileName(file)) is { } date)
                    yield return (file, date);
    }

    /// <summary>Expands {year}/{month} path segments by listing matching sub-folders.</summary>
    private static IEnumerable<string> ExpandFolders(string root, string folder)
    {
        var current = new List<string> { root };
        foreach (var segment in folder.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (!segment.Contains('{')) { next.Add(System.IO.Path.Combine(dir, segment)); continue; }
                if (!Directory.Exists(dir)) continue;
                var rx = new Regex("^" + Regex.Escape(segment).Replace(@"\{year}", @"\d{4}").Replace(@"\{month}", @"\d{2}") + "$");
                next.AddRange(Directory.EnumerateDirectories(dir).Where(d => rx.IsMatch(System.IO.Path.GetFileName(d))));
            }
            current = next;
        }
        return current.Where(Directory.Exists);
    }
}
