using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StatementDownloader.Core;

namespace StatementDownloader.Sites.Recipes;

/// <summary>Runs a <see cref="SiteRecipe"/>: everything site-specific comes from the recipe.</summary>
public sealed class RecipeSiteFetcher(SiteRecipe recipe) : PlaywrightSiteFetcher, IHasDocumentParser
{
    public SiteRecipe Recipe => recipe;
    public IDocumentParser Parser { get; } = new RecipeParser(recipe.Parse);
    public override string SiteId => recipe.Id;
    public override string DisplayName => recipe.Name;
    protected override string StartUrl => recipe.SignIn.Url;
    protected override IReadOnlyCollection<string>? SignInCookieNames => recipe.SignIn.RememberCookies;
    protected override bool AttendedOnly => recipe.SignIn.Attended;
    protected override bool YourBrowser => recipe.SignIn.YourBrowser;
    protected override bool YourBrowserUnattended => recipe.SignIn.Unattended;
    public override bool EmailsCode => recipe.SignIn.EmailCode is not null;

    private SignInRecipe S => recipe.SignIn;

    protected override async Task<bool> IsSignedInAsync(IPage page, CancellationToken ct) =>
        await page.Locator(S.SignedIn).CountAsync() > 0 && !await page.Locator(S.Password).First.IsVisibleAsync();

    protected override async Task WaitForPageReadyAsync(IPage page, CancellationToken ct)
    {
        try { await page.Locator($"{S.SignedIn}, {S.Username}").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 }); }
        catch (TimeoutException) { /* classified after the sign-in attempt */ }
    }

    protected override Task<bool> HasSignInFormAsync(IPage page, CancellationToken ct) =>
        page.Locator(S.Username).First.IsVisibleAsync();

    protected override async Task SubmitSignInAsync(IPage page, SiteCredential credential, CancellationToken ct, bool submit = true)
    {
        await page.Locator(S.Username).First.FillAsync(credential.Username);
        if (!submit)
        {
            // Fill what's showing; the person goes on (a two-page form's password is filled when it appears).
            if (await page.Locator(S.Password).First.IsVisibleAsync())
                await page.Locator(S.Password).First.FillAsync(credential.Password);
            return;
        }
        if (S.Next is { } next)
        {
            await page.Locator(next).First.ClickAsync();
            await page.Locator(S.Password).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        }
        await page.Locator(S.Password).First.FillAsync(credential.Password);
        await page.Locator(S.Submit).First.ClickAsync();
    }

    private const string CodeFieldDefault =
        "input[autocomplete='one-time-code'], input[name*='otp' i], input[id*='otp' i], input[name*='passcode' i], input[id*='passcode' i], input[name*='code' i]:not([type=hidden])";

    protected override async Task<bool> EnterEmailedCodeAsync(IPage page, SiteContext ctx, DateTimeOffset since, TimeSpan appear, CancellationToken ct)
    {
        if (S.EmailCode is not { } ec || ctx.Mailbox is not { } mailbox) return false;
        var field = page.Locator(ec.Field ?? CodeFieldDefault).First;
        try
        {
            if (appear > TimeSpan.Zero) await field.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = (float)appear.TotalMilliseconds });
            else if (!await field.IsVisibleAsync()) return false;
        }
        catch (TimeoutException) { return false; }

        ctx.Log.LogInformation("{Site}: waiting up to {Wait} for the emailed code", SiteId, ec.Wait);
        string? code;
        // A little earlier than the sign-in, for the mail server's clock.
        try { code = await mailbox.WaitForCodeAsync(ec.From, ec.Pattern, since.AddSeconds(-30), Durations.Parse(ec.Wait), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Log.LogWarning("{Site}: couldn't read the mailbox for the code: {Error}", SiteId, ex.Message);
            return false;
        }
        if (code is null)
        {
            ctx.Log.LogWarning("{Site}: no code email arrived within {Wait}", SiteId, ec.Wait);
            return false;
        }
        await field.FillAsync(code);
        if (ec.Submit is { } submit) await page.Locator(submit).First.ClickAsync();
        else await field.PressAsync("Enter");
        ctx.Log.LogInformation("{Site}: entered the emailed code", SiteId);
        return true;
    }

    private static async Task<bool> ClickableAsync(ILocator element)
    {
        if (!await element.IsVisibleAsync()) return false;
        try { await element.ClickAsync(new LocatorClickOptions { Trial = true, Timeout = 2000 }); return true; }
        catch (TimeoutException) { return false; }
    }

    protected override async Task<IReadOnlyList<FoundDocument>> DownloadNewDocumentsAsync(IPage page, IBrowserContext browser, SiteContext ctx, CancellationToken ct)
    {
        var d = recipe.Documents;
        var options = ctx.Account.SiteOptions;
        foreach (var (step, i) in d.Steps.Select((s, i) => (s, i + 1)))
        try
        {
            if (step.IfOption is { } io && (!options.TryGetValue(io, out var v) || string.IsNullOrWhiteSpace(v))) continue;
            await RunStepAsync(page, new StepRecipe
            {
                Click = RecipeOptions.Fill(step.Click, options, ctx.Account.Id),
                Follow = RecipeOptions.Fill(step.Follow, options, ctx.Account.Id),
                Goto = RecipeOptions.Fill(step.Goto, options, ctx.Account.Id),
                WaitFor = RecipeOptions.Fill(step.WaitFor, options, ctx.Account.Id),
                Frame = RecipeOptions.Fill(step.Frame, options, ctx.Account.Id),
                Timeout = step.Timeout,
                Optional = step.Optional,
            }, i);
        }
        catch (InvalidDataException ex) { throw new SiteLayoutException(ex.Message); }

        var list = d.List;
        string rowsSelector;
        try { rowsSelector = RecipeOptions.Fill(list.Rows, options, ctx.Account.Id)!; }
        catch (InvalidDataException ex) { throw new SiteLayoutException(ex.Message); }
        var rows = In(page, list.Frame, rowsSelector);
        try { await rows.First.WaitForAsync(new LocatorWaitForOptions { Timeout = Ms(list.Wait) }); }
        catch (TimeoutException) { throw new SiteLayoutException($"The {recipe.Kind} list didn't appear (no '{rowsSelector}' after {list.Wait})."); }
        if (ctx.Capture) await SaveDiagnosticsAsync(page, ctx, "capture-list");

        var known = ctx.Known.Where(k => k.StatementNumber is not null).Select(k => k.StatementNumber!).ToHashSet();
        var newest = ctx.Known.Count > 0
            ? ctx.Known.Max(k => k.DocumentDate)
            : DateOnly.FromDateTime(DateTime.Today - Durations.Parse(d.FirstRunLookback));

        var byClick = d.Download is "click" or "popup";
        var candidates = new List<(ListedDocument Doc, ILocator Row, ILocator LinkElement)>();
        foreach (var row in await rows.AllAsync())
        {
            var cells = list.Date.Column is not null || list.Amount?.Column is not null ? await row.Locator("td, th").AllInnerTextsAsync() : [];
            var linkElement = list.Link.Selector is { } ls ? row.Locator(ls).First : row;
            var listed = RecipeRows.Read(list,
                await FieldTextAsync(row, list.Date, cells),
                list.Amount is { } a ? await FieldTextAsync(row, a, cells) : null,
                await Quietly(() => linkElement.GetAttributeAsync(list.Link.Attribute, new LocatorGetAttributeOptions { Timeout = 2000 })),
                recipe.Parse.DateFormats,
                clickToDownload: byClick);
            if (listed is not null && listed.Date > newest) candidates.Add((listed, row, linkElement));
        }
        if (candidates.Count == 0)
        {
            ctx.Log.LogInformation("{Site}: nothing newer than {Newest}", SiteId, newest);
            return [];
        }

        var baseUrl = await FrameUrlAsync(page, list.Frame) ?? page.Url;
        var found = new List<FoundDocument>();
        foreach (var (doc, row, linkElement) in candidates.OrderBy(c => c.Doc.Date))
        {
            var name = $"{doc.Date:yyyy-MM-dd}-{found.Count}.pdf";
            // Expand when the link is hidden, or shown but covered (e.g. a card whose links appear once it's opened).
            if (list.Expand is { } expand && !await ClickableAsync(linkElement))
            {
                await row.Locator(expand).First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
                await linkElement.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
            }
            var path = d.Download switch
            {
                "click" => await DownloadByClickAsync(page, linkElement, ctx.WorkDir, name),
                "popup" => await DownloadByPopupAsync(page, browser, linkElement, ctx.WorkDir, name),
                _ => await DownloadPdfAsync(browser, new Uri(new Uri(baseUrl), doc.Link).ToString(), ctx.WorkDir, name),
            };
            if (path is null)
            {
                ctx.Log.LogWarning("{Site}: the {Kind} dated {Date} didn't download as a PDF", SiteId, recipe.Kind, doc.Date);
                continue;
            }
            var parsed = Parser.Parse(PdfText.Extract(path));
            if (parsed is null)
            {
                ctx.Log.LogWarning("{Site}: the {Kind} dated {Date} downloaded but its text didn't match the recipe", SiteId, recipe.Kind, doc.Date);
                continue;
            }
            if (parsed.StatementNumber is { } sn && known.Contains(sn)) { File.Delete(path); continue; }
            // Without a number, the date printed on the document identifies it (the list's date can
            // differ from it, e.g. bill date vs. end of the billing period).
            if (parsed.StatementNumber is null && ctx.Known.Any(k => k.DocumentDate == parsed.DocumentDate)) { File.Delete(path); continue; }
            found.Add(new FoundDocument
            {
                TempPath = path,
                DocumentDate = parsed.DocumentDate,
                StatementNumber = parsed.StatementNumber,
                Amount = parsed.Amount ?? doc.Amount,
                VendorAccountNumber = parsed.AccountNumber,
            });
        }
        return found;
    }

    private static async Task RunStepAsync(IPage page, StepRecipe step, int number)
    {
        var timeout = Ms(step.Timeout);
        var what = step.Click is { } c ? $"click {c}" : step.Follow is { } f ? $"follow {f}" : step.Goto is { } g ? $"go to {g}" : $"wait for {step.WaitFor}";
        try
        {
            if (step.Goto is { } url)
            {
                await page.GotoAsync(new Uri(new Uri(page.Url), url).ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = timeout });
                await SettleAsync(page);
            }
            else if (step.Click is { } click)
            {
                await In(page, step.Frame, click).First.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                await SettleAsync(page);
            }
            else if (step.Follow is { } follow)
            {
                // Present in the page but maybe hidden in a collapsed menu: send the click event directly.
                await In(page, step.Frame, follow).First.DispatchEventAsync("click", null, new LocatorDispatchEventOptions { Timeout = timeout });
                await SettleAsync(page);
            }
            else
            {
                var target = In(page, step.Frame, step.WaitFor!).First;
                try { await target.WaitForAsync(new LocatorWaitForOptions { Timeout = timeout }); }
                catch (TimeoutException) when (!step.Optional)
                {
                    // Some sites show a passing "something went wrong, refresh" page: refresh and look
                    // again, a few times, pausing in between.
                    for (var attempt = 1; ; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.Load, Timeout = timeout });
                        await SettleAsync(page);
                        try { await target.WaitForAsync(new LocatorWaitForOptions { Timeout = timeout }); break; }
                        catch (TimeoutException) when (attempt < 3) { }
                    }
                }
            }
        }
        catch (TimeoutException) when (step.Optional) { }
        catch (TimeoutException) { throw new SiteLayoutException($"Step {number} ({what}) failed: not found within {step.Timeout}."); }
    }

    private static ILocator In(IPage page, string? frame, string selector) =>
        frame is null ? page.Locator(selector) : page.FrameLocator(frame).Locator(selector);

    private static async Task<string?> FieldTextAsync(ILocator row, FieldRecipe f, IReadOnlyList<string> cells) =>
        f.Column is not null
            ? RecipeRows.Cell(f, cells)
            : await Quietly(() => row.Locator(f.Selector!).First.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }));

    private static async Task<string?> FrameUrlAsync(IPage page, string? frame)
    {
        if (frame is null) return null;
        var handle = await Quietly(() => page.Locator(frame).First.ElementHandleAsync(new LocatorElementHandleOptions { Timeout = 2000 }));
        var content = handle is null ? null : await handle.ContentFrameAsync();
        return content?.Url is { Length: > 0 } u && u != "about:blank" ? u : null;
    }

    private static async Task<T?> Quietly<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return default; }
    }

    private static float Ms(string duration) => (float)Durations.Parse(duration).TotalMilliseconds;
}
