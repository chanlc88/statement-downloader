using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace StatementDownloader.Sites;

public static class PdfText
{
    public static bool LooksLikePdf(byte[] bytes) => bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    /// <summary>All page text in reading order, whitespace collapsed to single spaces.</summary>
    public static string Extract(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var text = string.Join(" ", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

/// <summary>Values read from a document's text.</summary>
public sealed record ParsedDocument(DateOnly DocumentDate, string? StatementNumber, decimal? Amount, string? AccountNumber);

public interface IDocumentParser
{
    ParsedDocument? Parse(string text);
}

public static class ParseHelpers
{
    private static readonly string[] DateFormats = ["M/d/yy", "M/d/yyyy", "MM/dd/yy", "MM/dd/yyyy", "MMMM d, yyyy", "MMM d, yyyy", "MMM dd, yyyy", "yyyy-MM-dd"];

    public static DateOnly? Date(string? s) =>
        s is not null && DateOnly.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static decimal? Money(string? s) =>
        s is not null && decimal.TryParse(s.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var m) ? m : null;

    public static string? Group(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
}

/// <summary>Sites that can read their own documents (used to seed history from existing files).</summary>
public interface IHasDocumentParser
{
    IDocumentParser Parser { get; }
}
