using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StatementDownloader.Core;

namespace StatementDownloader.Sites;

/// <summary>One row of DeKalb's bill table: [View Bill, "Nov 18, 2026", "$73.21", due date].</summary>
public sealed record BillListRow(DateOnly Date, decimal? Amount, string PdfPath)
{
    public static BillListRow? TryParse(IReadOnlyList<string> cells, string? onclick)
    {
        if (cells.Count < 3 || onclick is null) return null;
        var url = Regex.Match(onclick, @"MM_openBrWindow\('([^']+)'");
        if (!url.Success || ParseHelpers.Date(cells[1]) is not { } date) return null;
        return new(date, ParseHelpers.Money(cells[2]), url.Groups[1].Value);
    }
}

/// <summary>
/// DeKalb County, GA water &amp; sewer (myaccount.dekalbcountyga.gov).
/// Sign-in is a plain username ("access code") and password form with no CAPTCHA; once signed
/// in the page shows a Logout link. Session cookies only, so every run signs in again.
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

    /// <summary>
    /// Bills &amp; Payment → My Bills is a table (<c>#billTable</c>) inside <c>iframe#iframe-BILLINQ</c>:
    /// [View Bill button, bill date "Nov 18, 2026", amount, due date]. The button's onclick is
    /// <c>MM_openBrWindow('/app/eBillViewerServlet?key=…', …)</c>, which returns the PDF directly
    /// with the session cookies. About two years of bills are listed, newest first.
    /// </summary>
    protected override async Task<IReadOnlyList<FoundDocument>> DownloadNewDocumentsAsync(IPage page, IBrowserContext browser, SiteContext ctx, CancellationToken ct)
    {
        var billsTab = page.Locator("a[href='#tabs-BILLINQ']").First;
        if (await billsTab.CountAsync() == 0) throw new SiteLayoutException("The Bills & Payment tab wasn't found after signing in.");
        await billsTab.ClickAsync();

        var rows = page.FrameLocator("#iframe-BILLINQ").Locator("#billTable tbody tr");
        try { await rows.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 45_000 }); }
        catch (TimeoutException) { throw new SiteLayoutException("The bill list (Bills & Payment → My Bills) didn't load."); }
        if (ctx.Capture) await SaveDiagnosticsAsync(page, ctx, "capture-bill-list");

        var known = ctx.Known.Where(k => k.StatementNumber is not null).Select(k => k.StatementNumber!).ToHashSet();
        var newest = ctx.Known.Count > 0 ? ctx.Known.Max(k => k.DocumentDate) : DateOnly.FromDateTime(DateTime.Today).AddDays(-400);

        var candidates = new List<BillListRow>();
        foreach (var row in await rows.AllAsync())
        {
            var cells = await row.Locator("td").AllInnerTextsAsync();
            var onclick = await row.Locator("a[onclick*='eBillViewerServlet']").First.GetAttributeAsync("onclick", new LocatorGetAttributeOptions { Timeout = 2000 }).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            if (BillListRow.TryParse(cells, onclick) is { } parsed && parsed.Date > newest) candidates.Add(parsed);
        }
        if (candidates.Count == 0)
        {
            ctx.Log.LogInformation("{Site}: no bills newer than {Newest}", SiteId, newest);
            return [];
        }

        var found = new List<FoundDocument>();
        foreach (var (date, _, relative) in candidates.OrderBy(c => c.Date))
        {
            var href = new Uri(new Uri(StartUrl), relative).ToString();
            var path = await DownloadPdfAsync(browser, href, ctx.WorkDir, $"{date:yyyy-MM-dd}-{found.Count}.pdf");
            if (path is null) continue;
            var parsed = BillParser.Parse(PdfText.Extract(path));
            if (parsed is null)
            {
                ctx.Log.LogWarning("{Site}: the bill dated {Date} downloaded but couldn't be read as a water bill", SiteId, date);
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
