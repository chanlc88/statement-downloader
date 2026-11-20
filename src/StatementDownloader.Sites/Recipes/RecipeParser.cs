using System.Globalization;
using System.Text.RegularExpressions;

namespace StatementDownloader.Sites.Recipes;

/// <summary>Reads a document's text with a recipe's <c>parse</c> patterns.</summary>
public sealed class RecipeParser(ParseRecipe recipe) : IDocumentParser
{
    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public ParsedDocument? Parse(string text)
    {
        if (recipe.Require.Any(p => !Regex.IsMatch(text, p, Options))) return null;
        if (ParseDate(First(text, recipe.Date)) is not { } date) return null;

        var account = First(text, recipe.AccountNumber);
        var statement = First(text, recipe.StatementNumber);
        // A pattern that picked up the account number by mistake is worse than no statement number.
        if (statement is not null && statement == account) statement = null;
        return new ParsedDocument(date, statement, ParseHelpers.Money(First(text, recipe.Amount)), account);
    }

    private DateOnly? ParseDate(string? s)
    {
        if (s is null) return null;
        if (ParseHelpers.Date(s) is { } d) return d;
        return recipe.DateFormats.Count > 0 && DateOnly.TryParseExact(s.Trim(), [.. recipe.DateFormats], CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : null;
    }

    private static string? First(string text, IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
            if (Regex.Match(text, p, Options) is { Success: true } m && m.Groups[1].Value.Trim() is { Length: > 0 } v) return v;
        return null;
    }
}

/// <summary>One document in a site's list, before it is downloaded.</summary>
public sealed record ListedDocument(DateOnly Date, decimal? Amount, string Link);

public static class RecipeRows
{
    /// <summary>
    /// Turns a row's raw values into a listed document: the date and amount texts (a cell or a
    /// selector's text) and the link element's attribute. Null when the row isn't a document.
    /// </summary>
    public static ListedDocument? Read(ListRecipe list, string? dateText, string? amountText, string? linkAttribute, IReadOnlyList<string>? dateFormats = null)
    {
        var date = Narrow(dateText, list.Date.Pattern) is { } ds ? ParseHelpers.Date(ds) ?? TryFormats(ds, dateFormats) : null;
        if (date is null || string.IsNullOrWhiteSpace(linkAttribute)) return null;
        var link = list.Link.Pattern is { } p
            ? Regex.Match(linkAttribute, p) is { Success: true } m ? m.Groups[1].Value : null
            : linkAttribute.Trim();
        if (string.IsNullOrWhiteSpace(link) || link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
        var amount = list.Amount is { } a ? ParseHelpers.Money(Narrow(amountText, a.Pattern)) : null;
        return new ListedDocument(date.Value, amount, link);
    }

    /// <summary>The text of a column field from a row's cells (1-based); null for selector fields.</summary>
    public static string? Cell(FieldRecipe f, IReadOnlyList<string> cells) =>
        f.Column is { } c && c <= cells.Count ? cells[c - 1] : null;

    private static string? Narrow(string? text, string? pattern)
    {
        if (text is null) return null;
        if (pattern is null) return text.Trim();
        return Regex.Match(text, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
    }

    private static DateOnly? TryFormats(string s, IReadOnlyList<string>? formats) =>
        formats is { Count: > 0 } && DateOnly.TryParseExact(s, [.. formats], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
