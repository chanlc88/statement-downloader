using System.Text.RegularExpressions;

namespace StatementDownloader.Core;

/// <summary>
/// The one set of rules for where an account's documents are saved. The config loader, the
/// Settings page and the filer all use it, so a hand-edited config can't save files anywhere
/// Settings wouldn't allow.
/// </summary>
public static class AccountLocation
{
    /// <summary>Tokens a folder may use. Dates go in the file name, so the folder stays stable within a year or month.</summary>
    private static readonly string[] FolderTokens = ["{year}", "{month}", "{account}", "{kind}"];

    /// <summary>Problems with a folder value (relative to documentsRoot), or null when fine.</summary>
    public static string? FolderProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Folder is required.";
        if (Path.IsPathRooted(folder) || Regex.IsMatch(folder, @"^[A-Za-z]:")) return "Folder must be relative to the base folder (no drive letter or leading slash).";
        if (folder.Split('/', '\\').Any(p => p.Trim() == "..")) return "Folder can't contain '..'.";
        var plain = FolderTokens.Aggregate(folder, (f, t) => f.Replace(t, ""));
        if (plain.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '{', '}']) >= 0)
            return "Folder can only use the {year}, {month}, {account} and {kind} tokens, and no < > : \" | ? * characters.";
        return null;
    }

    /// <summary>Problems with a file-name template, or null when fine.</summary>
    public static string? FileNameProblem(string? fileName)
    {
        if (!FileNameTemplate.HasDateToken(fileName)) return "File name must contain a date token such as {date:yyyy-MM-dd}.";
        if (fileName!.IndexOfAny(['/', '\\']) >= 0) return "File name can't contain / or \\ — put subfolders in Folder.";
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "File name should end in .pdf.";
        var stripped = Regex.Replace(fileName, @"\{[^}]*\}", "");
        if (stripped.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0) return "File name can't contain < > : \" | ? * characters.";
        return null;
    }

    /// <summary>
    /// The full path a rendered folder and file name resolve to under <paramref name="root"/>.
    /// Throws if it would land outside the root — a last line of defence behind the rules above.
    /// </summary>
    public static string Resolve(string root, string folder, string fileName)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, folder, fileName));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!path.StartsWith(prefix, comparison))
            throw new InvalidDataException($"'{Path.Combine(folder, fileName)}' would be saved outside the documents folder {fullRoot}.");
        return path;
    }
}
