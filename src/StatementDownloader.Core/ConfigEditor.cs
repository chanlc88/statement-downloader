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

    /// <summary>Turns the account's notifications off (<c>notify: false</c>) or back on (removes the key).</summary>
    public static string SetAccountNotify(string yaml, string accountId, bool notify) =>
        SetAccountEntry(yaml, accountId, "notify", notify ? null : "false");

    /// <summary>Sets <c>notifications.enabled</c>; on removes the key (it's the default).</summary>
    public static string SetNotificationsEnabled(string yaml, bool enabled) =>
        SetNotificationsEntry(yaml, "enabled", enabled ? null : ["false"], atTop: true);

    /// <summary>
    /// Replaces <c>notifications.groups</c> with <paramref name="groups"/> (no groups removes the key).
    /// The groups block is rewritten as a whole; the rest of the file is kept as written.
    /// </summary>
    public static string SetNotificationGroups(string yaml, IEnumerable<(string Name, IReadOnlyList<string> Email, bool Enabled)> groups)
    {
        var block = new List<string>();
        foreach (var (name, email, enabled) in groups)
        {
            block.Add($"  {Quote(name.Trim())}:");
            block.Add($"    email: [{string.Join(", ", email.Select(e => Quote(e.Trim())))}]");
            if (!enabled) block.Add("    enabled: false");
        }
        return SetNotificationsEntry(yaml, "groups", block.Count == 0 ? null : ["", .. block], atTop: false);
    }

    /// <summary>
    /// Replaces (or adds, or with null removes) one key of the top-level <c>notifications:</c> map.
    /// <paramref name="value"/>'s first item goes on the key's line; the rest are lines below it,
    /// indented relative to the key.
    /// </summary>
    private static string SetNotificationsEntry(string yaml, string key, IReadOnlyList<string>? value, bool atTop)
    {
        var lines = Split(yaml, out var nl);
        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^notifications\s*:"));
        if (start >= 0 && !Regex.IsMatch(lines[start], @"^notifications\s*:\s*(\{\s*\})?\s*(#.*)?$"))
            throw new InvalidDataException("Settings can only change notifications written as a block ('notifications:' on its own line). Edit config.yaml by hand.");
        if (start < 0)
        {
            if (value is null) return yaml;
            var accounts = lines.FindIndex(l => Regex.IsMatch(l, @"^accounts\s*:"));
            var at = accounts >= 0 ? accounts : lines.Count;
            lines.InsertRange(at, accounts >= 0 ? ["notifications:", ""] : ["", "notifications:"]);
            start = accounts >= 0 ? at : at + 1;
        }
        else lines[start] = Regex.Replace(lines[start], @"\s*\{\s*\}", "");

        var end = start + 1;
        while (end < lines.Count && !(lines[end].Length > 0 && !char.IsWhiteSpace(lines[end][0]) && !lines[end].StartsWith('#'))) end++;
        var firstChild = lines.Skip(start + 1).Take(end - start - 1).FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        var indent = firstChild is null ? 2 : firstChild.Length - firstChild.TrimStart().Length;
        var pad = new string(' ', indent);

        var keyRe = new Regex($@"^ {{{indent}}}{Regex.Escape(key)}\s*:");
        var existing = lines.FindIndex(start + 1, end - start - 1, l => keyRe.IsMatch(l));
        var newLines = value is null ? [] : new List<string> { $"{pad}{key}:{(value[0].Length > 0 ? " " + value[0] : "")}" }
            .Concat(value.Skip(1).Select(l => pad + l)).ToList();
        if (existing >= 0)
        {
            var spanEnd = existing + 1;
            while (spanEnd < end && (lines[spanEnd].Trim().Length == 0 || lines[spanEnd].Length - lines[spanEnd].TrimStart().Length > indent)) spanEnd++;
            while (spanEnd > existing + 1 && lines[spanEnd - 1].Trim() is "" or ['#', ..]) spanEnd--;
            var comment = spanEnd == existing + 1 && newLines.Count == 1 ? TrailingComment(lines[existing]) : "";
            lines.RemoveRange(existing, spanEnd - existing);
            if (newLines.Count > 0) newLines[0] += comment;
            lines.InsertRange(existing, newLines);
        }
        else if (newLines.Count > 0)
        {
            var insert = start + 1;
            if (!atTop)
            {
                insert = end;
                while (insert > start + 1 && lines[insert - 1].Trim() is "" or ['#', ..]) insert--;
            }
            lines.InsertRange(insert, newLines);
        }
        return string.Join(nl, lines);
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
        if (start < 0)
        {
            // Valid YAML can list keys in any order, but this editor finds an account by its first line.
            var elsewhere = lines.Any(l => Regex.IsMatch(l, $@"^\s*(-\s+)?id\s*:\s*[""']?{Regex.Escape(accountId)}[""']?\s*(#.*)?$"));
            throw new InvalidDataException(elsewhere
                ? $"Settings can only change an account whose entry starts with its id. In config.yaml, move the line 'id: {accountId}' to the top of that account, as '- id: {accountId}', then try again."
                : $"Account '{accountId}' wasn't found in the config file (looked for a line '- id: {accountId}').");
        }

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

    /// <summary>
    /// Adds an account at the end of the <c>accounts:</c> list, in the list's own indentation.
    /// <paramref name="entries"/> are key and raw YAML value pairs; the first one starts the entry
    /// (it should be <c>id</c>, so the other edits can find the account).
    /// </summary>
    public static string AddAccount(string yaml, IReadOnlyList<(string Key, string RawValue)> entries)
    {
        if (entries.Count == 0 || entries[0].Key != "id") throw new ArgumentException("The first entry must be the id.", nameof(entries));
        var lines = Split(yaml, out var nl);
        var at = lines.FindIndex(l => Regex.IsMatch(l, @"^accounts\s*:\s*(\[\s*\])?\s*(#.*)?$"));
        if (at < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add("accounts:");
            at = lines.Count - 1;
        }
        else lines[at] = Regex.Replace(lines[at], @"\s*\[\s*\]", "");

        // The list runs to the next top-level key; comments just before that key belong to it.
        var end = at + 1;
        while (end < lines.Count)
        {
            var l = lines[end];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith('#') && !l.StartsWith("- ")) break;
            end++;
        }
        var insert = end;
        while (insert > at + 1 && (lines[insert - 1].Trim().Length == 0 || lines[insert - 1].StartsWith('#'))) insert--;

        var first = lines.Skip(at + 1).Take(end - at - 1).Select(l => Regex.Match(l, @"^(\s*)-\s+\S")).FirstOrDefault(m => m.Success);
        var dash = first?.Groups[1].Value ?? "  ";
        var block = new List<string>();
        if (insert > at + 1) block.Add("");
        block.Add($"{dash}- {entries[0].Key}: {entries[0].RawValue}");
        block.AddRange(entries.Skip(1).Select(e => $"{dash}  {e.Key}: {e.RawValue}"));
        lines.InsertRange(insert, block);
        return string.Join(nl, lines);
    }

    /// <summary>A YAML double-quoted string.</summary>
    public static string QuoteValue(string value) => Quote(value);

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
