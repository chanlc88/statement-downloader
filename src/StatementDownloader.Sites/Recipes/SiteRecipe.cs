using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StatementDownloader.Sites.Recipes;

/// <summary>
/// A site described as data: how to sign in, where the document list is, how to download each
/// document and how to read its date, number and amount. See docs/site-recipes.md.
/// </summary>
public sealed class SiteRecipe
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>bill, statement or invoice (the default word for accounts using this site).</summary>
    public string Kind { get; set; } = "bill";
    public string? Region { get; set; }
    public string? Website { get; set; }
    /// <summary>Date (yyyy-MM-dd) the recipe was last confirmed against the live site.</summary>
    public string? Verified { get; set; }
    public string? Notes { get; set; }
    /// <summary>
    /// Settings an account can give in its <c>siteOptions</c> (name → what it is), used in steps
    /// as <c>{name}</c> — e.g. which billing account to open when one login has several.
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = [];
    public SignInRecipe SignIn { get; set; } = new();
    public DocumentsRecipe Documents { get; set; } = new();
    public ParseRecipe Parse { get; set; } = new();
    public RecipeTests Tests { get; set; } = new();

    /// <summary>Where the recipe came from ("built-in" or a file path), for display.</summary>
    [YamlIgnore] public string Source { get; set; } = "built-in";
}

public sealed class SignInRecipe
{
    /// <summary>
    /// The site refuses unattended (background) browsers. It is then only visited in a visible window
    /// with the person present, and a due check becomes a "Sign in now" request instead.
    /// </summary>
    public bool Attended { get; set; }
    /// <summary>
    /// Sign in in a regular Chrome/Edge window (its own profile) instead of the app's browser, for
    /// sites whose bot check refuses automated browsers even with you present. The app connects
    /// to that window once you're signed in and does the rest. Implies attended.
    /// </summary>
    public bool YourBrowser { get; set; }
    /// <summary>
    /// With <see cref="Attended"/> or <see cref="YourBrowser"/>: scheduled checks open the visible window
    /// by themselves and sign in once, for sites that let a remembered device or a self-passing human
    /// check through there. If they don't, the check stops and asks you.
    /// </summary>
    public bool Unattended { get; set; }
    /// <summary>Page to open first; shows the sign-in form or, when remembered, the signed-in page.</summary>
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Submit { get; set; } = "";
    /// <summary>Optional button between the username and password steps (two-page sign-in).</summary>
    public string? Next { get; set; }
    /// <summary>Selector present only when signed in (a Log out link, the account menu…).</summary>
    public string SignedIn { get; set; } = "";
    /// <summary>Names of the cookies that hold the sign-in, for "remembered until". Default: all persistent cookies.</summary>
    public List<string>? RememberCookies { get; set; }
    /// <summary>A one-time code the site emails; read from the configured mailbox and entered.</summary>
    public EmailCodeRecipe? EmailCode { get; set; }
}

public sealed class EmailCodeRecipe
{
    /// <summary>Regex matched against the sender, e.g. <c>'pingidentity\.com'</c>.</summary>
    public string From { get; set; } = "";
    /// <summary>Regex whose group 1 is the code, matched against the subject and text.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>The code field. Default: a field marked as a one-time code, or named otp/passcode/code.</summary>
    public string? Field { get; set; }
    /// <summary>Button that sends the code. Default: press Enter in the field.</summary>
    public string? Submit { get; set; }
    /// <summary>How long to wait for the email.</summary>
    public string Wait { get; set; } = "2m";
}

public sealed class DocumentsRecipe
{
    /// <summary>What to do after signing in to reach the document list.</summary>
    public List<StepRecipe> Steps { get; set; } = [];
    public ListRecipe List { get; set; } = new();
    /// <summary>
    /// <c>fetch</c>: GET the link with the browser's cookies. <c>click</c>: click it and save the download.
    /// <c>popup</c>: click it; the site opens the PDF in a new window (often a <c>blob:</c> URL), which is saved and closed.
    /// </summary>
    public string Download { get; set; } = "fetch";
    /// <summary>On the first run (nothing on record), how far back to download.</summary>
    public string FirstRunLookback { get; set; } = "400d";
    /// <summary>
    /// For sites that list one year at a time: steps that switch the list to <c>{year}</c>. When
    /// filling gaps, they're run once for each missing year the first list doesn't show, and that
    /// year's list is read the same way.
    /// </summary>
    public List<StepRecipe>? Years { get; set; }
}

public sealed class StepRecipe
{
    public string? Click { get; set; }
    /// <summary>
    /// Activates a link that is in the page but not shown (in a collapsed menu) by sending it a
    /// click event, as the site's own menu would. Use for in-app (router) links.
    /// </summary>
    public string? Follow { get; set; }
    public string? Goto { get; set; }
    public string? WaitFor { get; set; }
    /// <summary>A drop-down list (<c>&lt;select&gt;</c>) to choose <see cref="Option"/> in.</summary>
    public string? Select { get; set; }
    /// <summary>
    /// With <see cref="Select"/>: the choice, matched against each option's value and text (exactly,
    /// then as part of the text). May use <c>{year}</c> in <see cref="DocumentsRecipe.Years"/>.
    /// </summary>
    public string? Option { get; set; }
    /// <summary>Selector of the iframe the step's element is in.</summary>
    public string? Frame { get; set; }
    /// <summary>Only run this step when the account sets this option.</summary>
    public string? IfOption { get; set; }
    public string Timeout { get; set; } = "30s";
    /// <summary>Skip the step when its element doesn't turn up (e.g. expand a panel only if it's collapsed).</summary>
    public bool Optional { get; set; }
}

public sealed class ListRecipe
{
    public string? Frame { get; set; }
    /// <summary>One element per document, usually table rows.</summary>
    public string Rows { get; set; } = "";
    /// <summary>How long to wait for the first row.</summary>
    public string Wait { get; set; } = "30s";
    public FieldRecipe Date { get; set; } = new();
    public FieldRecipe? Amount { get; set; }
    public LinkRecipe Link { get; set; } = new();
    /// <summary>
    /// Optional element in the row to click when the link isn't visible yet — for lists whose rows
    /// open a details panel holding the link. The link selector may then use
    /// <c>xpath=following-sibling::tr[1]//a</c> to reach into that panel.
    /// </summary>
    public string? Expand { get; set; }
    /// <summary>
    /// With <c>download: click</c>: an element on the page to click after the link, when the link
    /// opens a dialog (e.g. choosing the file type) whose button starts the download.
    /// </summary>
    public string? Confirm { get; set; }
    /// <summary>
    /// An element on the page to click when a row's link isn't visible — for a list shown in a
    /// picker that closes once a document is chosen, so it's opened again for the next one.
    /// </summary>
    public string? Open { get; set; }
    /// <summary>With <c>confirm</c>: how long to let the chosen document load before clicking it.</summary>
    public string? Settle { get; set; }
}

/// <summary>
/// A value in a row: a 1-based table cell, the text of a selector, or an attribute (of a selector,
/// or of the row itself), optionally narrowed by a regex.
/// </summary>
public sealed class FieldRecipe
{
    public int? Column { get; set; }
    public string? Selector { get; set; }
    public string? Attribute { get; set; }
    public string? Pattern { get; set; }
}

public sealed class LinkRecipe
{
    /// <summary>Element inside the row; default the row itself.</summary>
    public string? Selector { get; set; }
    public string Attribute { get; set; } = "href";
    /// <summary>Regex whose first group is the URL inside the attribute (e.g. a javascript: call).</summary>
    public string? Pattern { get; set; }
}

public sealed class ParseRecipe
{
    /// <summary>Every pattern must match, so other documents in the same place are skipped.</summary>
    public PatternList Require { get; set; } = [];
    /// <summary>Patterns are tried in order; the first one that matches wins. Group 1 is the value.</summary>
    public PatternList Date { get; set; } = [];
    public PatternList StatementNumber { get; set; } = [];
    public PatternList Amount { get; set; } = [];
    public PatternList AccountNumber { get; set; } = [];
    /// <summary>Extra date formats beyond the common US ones.</summary>
    public List<string> DateFormats { get; set; } = [];
    /// <summary>
    /// A site option the document's account number must end with (e.g. <c>account</c>). A
    /// downloaded document for another account on the same login is then skipped, not filed.
    /// </summary>
    public string? AccountOption { get; set; }
}

/// <summary>One regex or a list of them.</summary>
public sealed class PatternList : List<string>;

public sealed class RecipeTests
{
    public List<DocumentTest> Documents { get; set; } = [];
    public List<RowTest> Rows { get; set; } = [];
}

/// <summary>PDF text (as <c>pdf-text</c> prints it, with made-up numbers) and what the parser should read.</summary>
public sealed class DocumentTest
{
    public string Text { get; set; } = "";
    /// <summary>Null means "not this site's document".</summary>
    public ExpectedDocument? Expect { get; set; }
}

public sealed class ExpectedDocument
{
    public string Date { get; set; } = "";
    public string? StatementNumber { get; set; }
    public decimal? Amount { get; set; }
    public string? AccountNumber { get; set; }
}

/// <summary>A list row's cell texts and link attribute, and what should be read from them.</summary>
public sealed class RowTest
{
    public List<string> Cells { get; set; } = [];
    public string? Link { get; set; }
    public ExpectedRow Expect { get; set; } = new();
}

public sealed class ExpectedRow
{
    public string Date { get; set; } = "";
    public decimal? Amount { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>Choosing an option in a drop-down list.</summary>
public static class RecipeSelect
{
    /// <summary>
    /// The index of the option matching <paramref name="want"/>: by value or text exactly (ignoring
    /// case and surrounding spaces), else the only one whose text contains it. Null when none does.
    /// </summary>
    public static int? Pick(IReadOnlyList<(string Value, string Text)> options, string want)
    {
        want = want.Trim();
        bool Same(string a) => string.Equals(a.Trim(), want, StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < options.Count; i++)
            if (Same(options[i].Value) || Same(options[i].Text)) return i;
        var partial = Enumerable.Range(0, options.Count).Where(i => options[i].Text.Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    /// <summary>
    /// The years a gap fill should switch the list to: those in the missing ranges that the first
    /// list showed nothing from, newest first.
    /// </summary>
    public static IReadOnlyList<int> YearsToVisit(IEnumerable<Core.DateRange> missing, IEnumerable<DateOnly> listed)
    {
        var shown = listed.Select(d => d.Year).ToHashSet();
        return missing.SelectMany(r => Enumerable.Range(r.From.Year, r.To.Year - r.From.Year + 1))
            .Distinct().Where(y => !shown.Contains(y)).OrderDescending().ToList();
    }
}

/// <summary><c>{name}</c> placeholders in steps and list rows, filled from the account's <c>siteOptions</c>.</summary>
public static class RecipeOptions
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z][A-Za-z0-9]*)\}");

    /// <summary>The year being switched to, in <see cref="DocumentsRecipe.Years"/> steps.</summary>
    public const string Year = "year";

    public static IEnumerable<string> Names(params string?[] texts) =>
        texts.Where(t => t is not null).SelectMany(t => Placeholder.Matches(t!).Select(m => m.Groups[1].Value)).Distinct();

    /// <summary>Fills placeholders; throws naming the missing option so the config can be fixed.</summary>
    public static string? Fill(string? text, IReadOnlyDictionary<string, string> values, string accountId) =>
        text is null ? null : Placeholder.Replace(text, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : throw new InvalidDataException($"Account '{accountId}' needs siteOptions: {{ {m.Groups[1].Value}: ... }} for this site."));
}

public static class RecipeLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new PatternListConverter())
        .Build();

    public static SiteRecipe Parse(string yaml, string source)
    {
        SiteRecipe recipe;
        try { recipe = Yaml.Deserialize<SiteRecipe>(yaml) ?? throw new InvalidDataException("The file is empty."); }
        catch (YamlException ex) { throw new InvalidDataException($"Site recipe {source}: {ex.Message}", ex); }
        recipe.Source = source;
        var errors = Validate(recipe);
        if (errors.Count > 0) throw new InvalidDataException($"Site recipe {source} is not valid:\n  - " + string.Join("\n  - ", errors));
        return recipe;
    }

    /// <summary>The recipes shipped with the app (the repo's <c>sites/</c> folder, embedded at build).</summary>
    public static IReadOnlyList<SiteRecipe> BuiltIn()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("sites/", StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                return Parse(new StreamReader(s).ReadToEnd(), "built-in");
            })
            .ToList();
    }

    /// <summary>Your own recipes: every <c>*.yaml</c> in <paramref name="dir"/> (missing folder = none).</summary>
    public static IReadOnlyList<SiteRecipe> FromDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal).Select(f => Parse(File.ReadAllText(f), f)).ToList()
            : [];

    public static List<string> Validate(SiteRecipe r)
    {
        var e = new List<string>();
        void Need(string? v, string name) { if (string.IsNullOrWhiteSpace(v)) e.Add($"{name} is required."); }

        if (string.IsNullOrWhiteSpace(r.Id) || !Regex.IsMatch(r.Id, "^[a-z0-9][a-z0-9-]*$")) e.Add("id is required and may contain only lowercase letters, digits and dashes.");
        Need(r.Name, "name");
        if (r.Verified is not null && !DateOnly.TryParseExact(r.Verified, "yyyy-MM-dd", out _)) e.Add("verified must be a date like 2026-11-20.");

        Need(r.SignIn.Url, "signIn.url");
        if (!Uri.TryCreate(r.SignIn.Url, UriKind.Absolute, out var start) || start.Scheme != Uri.UriSchemeHttps) e.Add("signIn.url must be an https:// URL.");
        Need(r.SignIn.Username, "signIn.username");
        Need(r.SignIn.Password, "signIn.password");
        Need(r.SignIn.Submit, "signIn.submit");
        Need(r.SignIn.SignedIn, "signIn.signedIn");
        if (r.SignIn.Unattended && !r.SignIn.YourBrowser && !r.SignIn.Attended) e.Add("signIn.unattended needs signIn.attended or signIn.yourBrowser.");
        if (r.SignIn.EmailCode is { } ec)
        {
            Compiles(ec.From, "signIn.emailCode.from");
            Regex1(ec.Pattern, "signIn.emailCode.pattern");
            Duration(ec.Wait, "signIn.emailCode.wait");
        }
        Need(r.Documents.List.Rows, "documents.list.rows");
        Field(r.Documents.List.Date, "documents.list.date", required: true);

        Steps(r.Documents.Steps, "documents.steps", year: false);
        if (r.Documents.Years is { } years)
        {
            if (years.Count == 0) e.Add("documents.years needs at least one step.");
            else if (!years.Any(s => RecipeOptions.Names(s.Click, s.Follow, s.Goto, s.WaitFor, s.Select, s.Option).Contains(RecipeOptions.Year)))
                e.Add("documents.years needs a step that uses {year}, e.g. select: ... with option: '{year}'.");
            Steps(years, "documents.years", year: true);
        }
        foreach (var name in RecipeOptions.Names(r.Documents.List.Rows))
            if (!r.Options.ContainsKey(name)) e.Add($"documents.list.rows uses {{{name}}}, which isn't listed under options.");
        Duration(r.Documents.List.Wait, "documents.list.wait");
        if (r.Documents.List.Settle is { } settle) Duration(settle, "documents.list.settle");
        Duration(r.Documents.FirstRunLookback, "documents.firstRunLookback");
        if (r.Documents.List.Amount is { } amount) Field(amount, "documents.list.amount", required: false);
        Regex1(r.Documents.List.Link.Pattern, "documents.list.link.pattern");
        if (r.Documents.Download is not ("fetch" or "click" or "popup")) e.Add("documents.download must be fetch, click or popup.");

        if (r.Parse.Date.Count == 0) e.Add("parse.date needs at least one pattern.");
        foreach (var (name, list) in new[] { ("require", r.Parse.Require), ("date", r.Parse.Date), ("statementNumber", r.Parse.StatementNumber), ("amount", r.Parse.Amount), ("accountNumber", r.Parse.AccountNumber) })
            foreach (var p in list)
                if (name == "require") Compiles(p, $"parse.{name}"); else Regex1(p, $"parse.{name}");
        return e;

        void Steps(List<StepRecipe> steps, string where, bool year)
        {
            foreach (var (s, i) in steps.Select((s, i) => (s, i + 1)))
            {
                var actions = new[] { s.Click, s.Follow, s.Goto, s.WaitFor, s.Select }.Count(x => !string.IsNullOrWhiteSpace(x));
                if (actions != 1) e.Add($"{where}[{i}] needs exactly one of click, follow, goto, waitFor or select.");
                if (!string.IsNullOrWhiteSpace(s.Select) && string.IsNullOrWhiteSpace(s.Option)) e.Add($"{where}[{i}] select needs an option.");
                if (string.IsNullOrWhiteSpace(s.Select) && s.Option is not null) e.Add($"{where}[{i}] option goes with select.");
                Duration(s.Timeout, $"{where}[{i}].timeout");
                if (s.IfOption is { } io && !r.Options.ContainsKey(io)) e.Add($"{where}[{i}].ifOption '{io}' isn't listed under options.");
                foreach (var name in RecipeOptions.Names(s.Click, s.Follow, s.Goto, s.WaitFor, s.Select, s.Option, s.Frame))
                    if (!r.Options.ContainsKey(name) && !(year && name == RecipeOptions.Year))
                        e.Add($"{where}[{i}] uses {{{name}}}, which isn't listed under options{(name == RecipeOptions.Year ? " ({year} works only in documents.years)" : "")}.");
            }
        }
        void Field(FieldRecipe f, string name, bool required)
        {
            if (f.Column is null && string.IsNullOrWhiteSpace(f.Selector) && string.IsNullOrWhiteSpace(f.Attribute)) { if (required) e.Add($"{name} needs a column, a selector or an attribute."); }
            else if (f.Column is < 1) e.Add($"{name}.column counts from 1.");
            Regex1(f.Pattern, name + ".pattern");
        }
        void Duration(string v, string name)
        {
            try { _ = Core.Durations.Parse(v); } catch (FormatException) { e.Add($"{name} must be a duration like 30s, 2m or 400d."); }
        }
        void Compiles(string p, string name)
        {
            try { _ = new Regex(p); } catch (ArgumentException ex) { e.Add($"{name}: bad regex '{p}': {ex.Message}"); }
        }
        void Regex1(string? p, string name)
        {
            if (p is null) return;
            try { if (new Regex(p).GetGroupNumbers().Length < 2) e.Add($"{name}: '{p}' needs a capture group ( ) around the value."); }
            catch (ArgumentException ex) { e.Add($"{name}: bad regex '{p}': {ex.Message}"); }
        }
    }

    private sealed class PatternListConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(PatternList);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var list = new PatternList();
            if (parser.TryConsume<Scalar>(out var one)) { list.Add(one.Value); return list; }
            parser.Consume<SequenceStart>();
            while (!parser.TryConsume<SequenceEnd>(out _)) list.Add(parser.Consume<Scalar>().Value);
            return list;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) => throw new NotSupportedException();
    }
}
