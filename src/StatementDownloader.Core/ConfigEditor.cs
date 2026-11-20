using System.Text;
using System.Text.RegularExpressions;

namespace StatementDownloader.Core;

/// <summary>
/// Changes single settings in the YAML config text while keeping everything else — comments,
/// order, formatting — exactly as the person wrote it. Every result is re-parsed and validated
/// before it is saved, so a bad edit can't leave a config the app won't start with.
/// </summary>
public static class ConfigEditor
{
    public static string SetDocumentsRoot(string yaml, string value)
    {
        var lines = Split(yaml, out var nl);
        var i = lines.FindIndex(l => Regex.IsMatch(l, @"^documentsRoot\s*:"));
        var line = $"documentsRoot: {Quote(value)}";
        if (i >= 0) lines[i] = line + TrailingComment(lines[i]);
        else lines.Insert(FirstContentLine(lines), line);
        return string.Join(nl, lines);
    }

    /// <summary>Sets a scalar <paramref name="key"/> (e.g. folder, fileName) on the account with <paramref name="accountId"/>.</summary>
    public static string SetAccountValue(string yaml, string accountId, string key, string value) =>
        SetAccountEntry(yaml, accountId, key, Quote(value));

    /// <summary>Sets the account's group; an empty value removes it.</summary>
    public static string SetAccountGroup(string yaml, string accountId, string? group) =>
        SetAccountEntry(yaml, accountId, "group", string.IsNullOrWhiteSpace(group) ? null : Quote(group.Trim()));

    /// <summary>Sets the account's tags as a one-line list; no tags removes the key.</summary>
    public static string SetAccountTags(string yaml, string accountId, IEnumerable<string> tags)
    {
        var list = NormalizeTags(tags);
        return SetAccountEntry(yaml, accountId, "tags", list.Count == 0 ? null : "[" + string.Join(", ", list.Select(Quote)) + "]");
    }

    /// <summary>Trimmed, lower-case, de-duplicated tags in the order given.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>
    /// Replaces (or adds, or with null removes) one key of an account. The key's old value may span
    /// several lines (a block list), and all of it is replaced. New keys go after <c>name</c>, else after <c>id</c>.
    /// </summary>
    private static string SetAccountEntry(string yaml, string accountId, string key, string? rawValue)
    {
        var lines = Split(yaml, out var nl);
        var start = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*-\s+id\s*:\s*[""']?{Regex.Escape(accountId)}[""']?\s*(#.*)?$"));
        if (start < 0) throw new InvalidDataException($"Account '{accountId}' wasn't found in the config file (looked for a line '- id: {accountId}').");

        var dashIndent = lines[start].Length - lines[start].TrimStart().Length;
        var keyIndent = lines[start].IndexOf("id", dashIndent, StringComparison.Ordinal);
        var end = start + 1;
        while (end < lines.Count)
        {
            var l = lines[end];
            var t = l.TrimStart();
            if (t.Length > 0 && !t.StartsWith('#') && l.Length - t.Length <= dashIndent) break;
            end++;
        }

        int Find(string k)
        {
            var re = new Regex($@"^ {{{keyIndent}}}{Regex.Escape(k)}\s*:");
            return lines.FindIndex(start + 1, end - start - 1, l => re.IsMatch(l));
        }
        // Lines after a key line that belong to its value: deeper-indented lines, or "- item"
        // lines at the key's own indent (a block list).
        int SpanEnd(int at)
        {
            var i = at + 1;
            while (i < end)
            {
                var l = lines[i];
                var t = l.TrimStart();
                var indent = l.Length - t.Length;
                if (t.Length == 0 || indent > keyIndent || (indent == keyIndent && t.StartsWith("- "))) { i++; continue; }
                break;
            }
            // Don't swallow trailing blank lines or comments that lead into the next key.
            while (i > at + 1 && lines[i - 1].Trim() is "" or ['#', ..]) i--;
            return i;
        }

        var newLine = rawValue is null ? null : new string(' ', keyIndent) + $"{key}: {rawValue}";
        var existing = Find(key);
        if (existing >= 0)
        {
            var spanEnd = SpanEnd(existing);
            var comment = spanEnd == existing + 1 ? TrailingComment(lines[existing]) : "";
            lines.RemoveRange(existing, spanEnd - existing);
            if (newLine is not null) lines.Insert(existing, newLine + comment);
        }
        else if (newLine is not null)
        {
            var name = key == "name" ? -1 : Find("name");
            var after = name >= 0 ? SpanEnd(name) : start + 1;
            if (key == "tags" && Find("group") is var g and >= 0) after = SpanEnd(g);
            lines.Insert(after, newLine);
        }
        return string.Join(nl, lines);
    }

    /// <summary>Checks the edited text parses and validates; returns the parsed config.</summary>
    public static AppConfig Check(string yaml) => ConfigLoader.Parse(yaml);

    /// <summary>Writes atomically, keeping the previous version as <c>config.yaml.bak</c>.</summary>
    public static void Save(string path, string yaml)
    {
        Check(yaml);
        var temp = path + ".tmp";
        File.WriteAllText(temp, yaml, new UTF8Encoding(false));
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Problems with a folder value (relative to documentsRoot), or null when fine.</summary>
    public static string? FolderProblem(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Folder is required.";
        if (Path.IsPathRooted(folder) || Regex.IsMatch(folder, @"^[A-Za-z]:")) return "Folder must be relative to the base folder (no drive letter or leading slash).";
        if (folder.Split('/', '\\').Any(p => p == "..")) return "Folder can't contain '..'.";
        if (folder.Replace("{year}", "").Replace("{month}", "").IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '{', '}']) >= 0)
            return "Folder can only use the {year} and {month} tokens, and no < > : \" | ? * characters.";
        return null;
    }

    /// <summary>Problems with a file-name template, or null when fine.</summary>
    public static string? FileNameProblem(string fileName)
    {
        if (!FileNameTemplate.HasDateToken(fileName)) return "File name must contain a date token such as {date:yyyy-MM-dd}.";
        if (fileName.IndexOfAny(['/', '\\']) >= 0) return "File name can't contain / or \\ — put subfolders in Folder.";
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "File name should end in .pdf.";
        var stripped = Regex.Replace(fileName, @"\{[^}]*\}", "");
        if (stripped.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0) return "File name can't contain < > : \" | ? * characters.";
        return null;
    }

    private static List<string> Split(string yaml, out string newline)
    {
        newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
        return [.. yaml.Replace("\r\n", "\n").Split('\n')];
    }

    private static int FirstContentLine(List<string> lines)
    {
        var i = lines.FindIndex(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        return i < 0 ? lines.Count : i;
    }

    /// <summary>A trailing "  # comment" on a simple scalar line, kept when the value changes.</summary>
    private static string TrailingComment(string line)
    {
        var m = Regex.Match(line, @"^[^#""']*:\s*(""[^""]*""|'[^']*'|[^#""']*?)(\s+#.*)$");
        return m.Success ? m.Groups[2].Value : "";
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
