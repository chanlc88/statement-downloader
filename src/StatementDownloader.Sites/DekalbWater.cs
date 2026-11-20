using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StatementDownloader.Core;

namespace StatementDownloader.Sites;

/// <summary>
/// DeKalb County, GA water &amp; sewer (myaccount.dekalbcountyga.gov).
/// The sign-in form is a plain username ("access code") and password with no CAPTCHA.
/// The pages after sign-in haven't been mapped yet: document discovery below is a best-effort
/// search for bill links and reports a layout problem (with diagnostics) if it finds none.
/// </summary>
public sealed class DekalbWaterFetcher : PlaywrightSiteFetcher, IHasDocumentParser
{
    public IDocumentParser Parser => BillParser;
    public override string SiteId => "dekalb-water";
    public override string DisplayName => "DeKalb County Water & Sewer (GA)";
    protected override string StartUrl => "https://myaccount.dekalbcountyga.gov/app/";

    private static readonly DekalbWaterBillParser BillParser = new();
    private const string SignOutLinks = "a:has-text('Log Out'), a:has-text('Logout'), a:has-text('Sign Out'), a:has-text('Log Off'), button:has-text('Log Out')";

    protected override async Task<bool> IsSignedInAsync(IPage page, CancellationToken ct) =>
        await page.Locator(SignOutLinks).CountAsync() > 0 && !await page.Locator("#password").IsVisibleAsync();

    protected override Task<bool> HasSignInFormAsync(IPage page, CancellationToken ct) =>
        page.Locator("#accessCode").IsVisibleAsync();

    protected override async Task SubmitSignInAsync(IPage page, SiteCredential credential, CancellationToken ct)
    {
        await page.Locator("#accessCode").FillAsync(credential.Username);
        await page.Locator("#password").FillAsync(credential.Password);
        await page.Locator("#login_btn").ClickAsync();
    }

    protected override async Task<IReadOnlyList<FoundDocument>> DownloadNewDocumentsAsync(IPage page, IBrowserContext browser, SiteContext ctx, CancellationToken ct)
    {
        // Go to the bill history / statements page if there's a link to one.
        var nav = page.Locator("a").Filter(new LocatorFilterOptions { HasTextRegex = new Regex(@"bill(ing)? history|view (my )?bills?|statements?|past bills", RegexOptions.IgnoreCase) }).First;
        if (await nav.CountAsync() > 0)
        {
            await nav.ClickAsync();
            await SettleAsync(page);
            if (ctx.Capture) await SaveDiagnosticsAsync(page, ctx, "capture-bill-list");
        }

        var known = ctx.Known.Where(k => k.StatementNumber is not null).Select(k => k.StatementNumber!).ToHashSet();
        var newest = ctx.Known.Count > 0 ? ctx.Known.Max(k => k.DocumentDate) : DateOnly.FromDateTime(DateTime.Today).AddDays(-400);

        // Candidate links: anything that looks like a bill PDF, with a date in its row.
        var candidates = new List<(DateOnly Date, string Href)>();
        foreach (var link in await page.Locator("a[href]").AllAsync())
        {
            var href = await link.GetAttributeAsync("href") ?? "";
            var text = (await link.InnerTextAsync()).Trim();
            if (!Regex.IsMatch(href + " " + text, @"pdf|bill|statement|view", RegexOptions.IgnoreCase)) continue;
            var rowText = await link.EvaluateAsync<string>("a => (a.closest('tr, li, .row, div') || a).innerText");
            if (ParseHelpers.Date(Regex.Match(rowText + " " + text, @"\d{1,2}/\d{1,2}/\d{2,4}").Value) is not { } date) continue;
            if (date <= newest) continue;
            candidates.Add((date, new Uri(new Uri(page.Url), href).ToString()));
        }

        if (candidates.Count == 0)
        {
            var anyBillLinks = await page.Locator("a[href*='pdf' i], a:has-text('bill')").CountAsync();
            if (anyBillLinks == 0) throw new SiteLayoutException("Couldn't find a list of bills after signing in. The site's pages need mapping; see the diagnostics screenshot.");
            ctx.Log.LogInformation("{Site}: no bills newer than {Newest}", SiteId, newest);
            return [];
        }

        var found = new List<FoundDocument>();
        foreach (var (date, href) in candidates.DistinctBy(c => c.Href).OrderBy(c => c.Date))
        {
            var path = await DownloadPdfAsync(browser, href, ctx.WorkDir, $"{date:yyyy-MM-dd}-{found.Count}.pdf");
            if (path is null) continue;
            var parsed = BillParser.Parse(PdfText.Extract(path));
            if (parsed is null)
            {
                ctx.Log.LogWarning("{Site}: downloaded {Href} but couldn't read it as a water bill", SiteId, href);
                continue;
            }
            if (parsed.StatementNumber is { } sn && known.Contains(sn)) { File.Delete(path); continue; }
            found.Add(new FoundDocument { TempPath = path, DocumentDate = parsed.DocumentDate, StatementNumber = parsed.StatementNumber, Amount = parsed.Amount, VendorAccountNumber = parsed.AccountNumber });
        }
        return found;
    }
}

/// <summary>Reads a DeKalb water &amp; sewer bill: bill date, statement number, current charges, account number.</summary>
public sealed class DekalbWaterBillParser : IDocumentParser
{
    public ParsedDocument? Parse(string text)
    {
        if (!Regex.IsMatch(text, @"Water\s*&\s*Sewer", RegexOptions.IgnoreCase)) return null;
        var date = ParseHelpers.Date(ParseHelpers.Group(text, @"Bill Date:?\s*(\d{1,2}/\d{1,2}/\d{2,4})"));
        if (date is null) return null;
        // The header is laid out as two labels followed by two numbers:
        // "Account Number Statement Number 1234567890 98765432". The payment stub has them paired.
        string? account, statement;
        var header = Regex.Match(text, @"Account Number\s+Statement Number\s+(\d{6,})\s+(\d{5,})", RegexOptions.IgnoreCase);
        if (header.Success) (account, statement) = (header.Groups[1].Value, header.Groups[2].Value);
        else
        {
            account = ParseHelpers.Group(text, @"Account Number\s*(\d{6,})");
            statement = ParseHelpers.Group(text, @"Statement Number\s*(\d{5,})");
        }
        if (statement == account) statement = null;

        return new ParsedDocument(
            date.Value,
            statement,
            // "Current Charges" is what the bill costs; "Total Amount Due" can include balances forward.
            ParseHelpers.Money(ParseHelpers.Group(text, @"Current Charges\s*\$\s*([\d,]+\.\d{2})")),
            account);
    }
}

public static class SiteRegistry
{
    public static IReadOnlyList<ISiteFetcher> All() => [new DekalbWaterFetcher()];
}
