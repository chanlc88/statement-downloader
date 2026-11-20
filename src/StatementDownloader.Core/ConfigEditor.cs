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
    public static string SetAccountValue(string yaml, string accountId, string key, string value)
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

        var keyLine = new Regex($@"^\s{{{keyIndent}}}{Regex.Escape(key)}\s*:");
        var newLine = new string(' ', keyIndent) + $"{key}: {Quote(value)}";
        var at = lines.FindIndex(start + 1, end - start - 1, l => keyLine.IsMatch(l));
        if (at >= 0) lines[at] = newLine + TrailingComment(lines[at]);
        else lines.Insert(start + 1, newLine);
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
