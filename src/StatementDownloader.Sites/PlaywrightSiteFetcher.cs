using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using StatementDownloader.Core;

namespace StatementDownloader.Sites;

/// <summary>Thrown by a site when signed in but its pages don't look as expected.</summary>
public sealed class SiteLayoutException(string message) : Exception(message);

/// <summary>
/// Shared plumbing for browser-driven sites: a persistent profile per account (so "remember
/// this device" sticks), sign-in with failure classification, assisted sign-in that waits for
/// the person, remembered-until from cookies, and diagnostics on failure.
/// </summary>
public abstract class PlaywrightSiteFetcher : ISiteFetcher
{
    public abstract string SiteId { get; }
    public abstract string DisplayName { get; }
    protected abstract string StartUrl { get; }
    /// <summary>URLs whose cookies represent the sign-in (for remembered-until).</summary>
    protected virtual IReadOnlyList<string> CookieUrls => [StartUrl];
    /// <summary>Names of the sign-in cookies; null = all persistent cookies for <see cref="CookieUrls"/>.</summary>
    protected virtual IReadOnlyCollection<string>? SignInCookieNames => null;

    protected abstract Task<bool> IsSignedInAsync(IPage page, CancellationToken ct);
    /// <summary>Waits until the page has rendered either the sign-in form or the signed-in page (script-built pages).</summary>
    protected virtual Task WaitForPageReadyAsync(IPage page, CancellationToken ct) => Task.CompletedTask;
    /// <summary>True when a username/password form is showing.</summary>
    protected abstract Task<bool> HasSignInFormAsync(IPage page, CancellationToken ct);
    protected abstract Task SubmitSignInAsync(IPage page, SiteCredential credential, CancellationToken ct);
    protected abstract Task<IReadOnlyList<FoundDocument>> DownloadNewDocumentsAsync(IPage page, IBrowserContext browser, SiteContext ctx, CancellationToken ct);

    /// <summary>The site refuses unattended browsers; only visit it in a visible window with the person present.</summary>
    protected virtual bool AttendedOnly => false;
    /// <summary>
    /// Sign-in happens in a regular browser window (Edge or Chrome, with its own profile) that the
    /// person signs in to; the app connects to it afterwards. For sites whose bot check refuses
    /// automated browsers even with the person present. Implies <see cref="AttendedOnly"/>.
    /// </summary>
    protected virtual bool YourBrowser => false;
    public bool NeedsPerson => AttendedOnly || YourBrowser;

    public async Task<SiteResult> RunAsync(SiteContext ctx, CancellationToken ct = default)
    {
        // Don't keep knocking on a site that blocks unattended browsers: ask the person instead.
        if ((AttendedOnly || YourBrowser) && !ctx.Interactive)
            return SiteResult.AuthFailed(AuthFailureReason.AttendedOnly, null);

        using var playwright = await Playwright.CreateAsync();
        Directory.CreateDirectory(ctx.ProfileDir);
        BrowserSession session;
        try
        {
            session = YourBrowser
                ? await BrowserSession.YoursAsync(playwright, ctx.ProfileDir, StartUrl, ct)
                : await BrowserSession.AutomatedAsync(playwright, ctx.ProfileDir, headless: !ctx.Interactive);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            return SiteResult.AuthFailed(AuthFailureReason.SiteUnavailable, ex.Message);
        }
        await using var _ = session;
        var browser = session.Context;
        var page = session.Page;

        try
        {
            // "Load", not "NetworkIdle": chat widgets and analytics keep many sites from ever going idle.
            // Your own browser was opened on the start page already; reloading would restart its bot check.
            if (!YourBrowser) await page.GotoAsync(StartUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });
            await SettleAsync(page);
            await WaitForPageReadyAsync(page, ct);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            if (ctx.Interactive && page.IsClosed) return SiteResult.AuthFailed(AuthFailureReason.AssistAbandoned, "The browser window was closed.");
            return SiteResult.AuthFailed(AuthFailureReason.SiteUnavailable, ex.Message, await SaveDiagnosticsAsync(page, ctx, "unreachable"));
        }

        var signIn = await SignInAsync(page, ctx, ct);
        if (signIn is not null) return signIn;

        var remembered = await RememberedUntilAsync(browser);
        if (ctx.Capture) await SaveDiagnosticsAsync(page, ctx, "capture-signed-in");
        if (ctx.SignInOnly) return new SiteResult { SignedIn = true, RememberedUntilUtc = remembered };

        try
        {
            var docs = await DownloadNewDocumentsAsync(page, browser, ctx, ct);
            return new SiteResult { SignedIn = true, RememberedUntilUtc = remembered, Documents = docs };
        }
        catch (Exception ex) when (ex is SiteLayoutException or PlaywrightException or TimeoutException)
        {
            // The window went away (closed by the person, or the browser restarted itself): nothing
            // is wrong with the site, the check just didn't finish.
            if (ctx.Interactive && page.IsClosed)
                return SiteResult.AuthFailed(AuthFailureReason.AssistAbandoned, "The browser window was closed before the check finished.");
            ctx.Log.LogWarning(ex, "{Site}: signed in but getting documents failed", SiteId);
            return new SiteResult { SignedIn = true, RememberedUntilUtc = remembered, DocumentError = ex.Message, DiagnosticsPath = await SaveDiagnosticsAsync(page, ctx, "documents") };
        }
    }

    /// <summary>Returns null when signed in, otherwise the failure.</summary>
    private async Task<SiteResult?> SignInAsync(IPage page, SiteContext ctx, CancellationToken ct)
    {
        if (await IsSignedInAsync(page, ct)) return null;

        if (await HasSignInFormAsync(page, ct))
        {
            await SubmitSignInAsync(page, ctx.Credential, ct);
            await SettleAsync(page);
            await WaitForSignedInAsync(page, TimeSpan.FromSeconds(20), ct);
            if (await IsSignedInAsync(page, ct)) return null;
        }

        if (!ctx.Interactive)
        {
            var reason = await ClassifyAsync(page, ct);
            return SiteResult.AuthFailed(reason, null, await SaveDiagnosticsAsync(page, ctx, "sign-in"));
        }

        // Assisted: the person finishes whatever the site asks (code, CAPTCHA, remember me).
        ctx.Log.LogInformation("{Site}: waiting up to {Minutes} minutes for the person to finish signing in", SiteId, ctx.AssistTimeout.TotalMinutes);
        var deadline = DateTime.UtcNow + ctx.AssistTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed) return SiteResult.AuthFailed(AuthFailureReason.AssistAbandoned, "The browser window was closed.");
            try
            {
                if (await IsSignedInAsync(page, ct)) return null;
                // If the site returned to a clean sign-in form, fill it again for the person.
                if (await HasSignInFormAsync(page, ct) && await PasswordFieldEmptyAsync(page)) await SubmitSignInAsync(page, ctx.Credential, ct);
            }
            catch (PlaywrightException) when (page.IsClosed)
            {
                return SiteResult.AuthFailed(AuthFailureReason.AssistAbandoned, "The browser window was closed.");
            }
            catch (PlaywrightException) { /* page navigating; try again */ }
            await Task.Delay(2000, ct);
        }
        return SiteResult.AuthFailed(AuthFailureReason.AssistAbandoned, $"Not signed in after {ctx.AssistTimeout.TotalMinutes:0} minutes.", await SaveDiagnosticsAsync(page, ctx, "assist-timeout"));
    }

    /// <summary>Polls the signed-in check for a while (sites that redirect a few times after sign-in).</summary>
    private async Task WaitForSignedInAsync(IPage page, TimeSpan max, CancellationToken ct)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            try { if (await IsSignedInAsync(page, ct)) return; }
            catch (PlaywrightException) { /* navigating */ }
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>Works out why an unattended sign-in didn't succeed.</summary>
    protected virtual async Task<AuthFailureReason> ClassifyAsync(IPage page, CancellationToken ct)
    {
        var captcha = await page.Locator("iframe[src*='recaptcha'], iframe[src*='hcaptcha'], iframe[src*='turnstile'], .g-recaptcha, [data-sitekey]").CountAsync();
        if (captcha > 0) return AuthFailureReason.Captcha;
        // Bot walls (Imperva/Incapsula, Cloudflare, Akamai…) often put the CAPTCHA in a nested frame.
        if (page.Frames.Any(f => IsBotWallUrl(f.Url))) return AuthFailureReason.Captcha;

        var text = await BodyTextAsync(page);
        if (IsBotWallText(text)) return AuthFailureReason.Captcha;
        if (Regex.IsMatch(text, @"verification code|security code|one[- ]time (pass)?code|passcode|we (sent|texted)|enter the code|two[- ]step|2fa|multi[- ]factor", RegexOptions.IgnoreCase))
            return AuthFailureReason.VerificationCode;
        if (await HasSignInFormAsync(page, ct) &&
            Regex.IsMatch(text, @"invalid|incorrect|not (be )?recogni[sz]ed|does not match|unable to (log|sign) ?in|failed", RegexOptions.IgnoreCase))
            return AuthFailureReason.PasswordRejected;
        if (await HasSignInFormAsync(page, ct)) return AuthFailureReason.SessionExpired;
        return AuthFailureReason.SiteChanged;
    }

    /// <summary>A frame URL that belongs to a bot-check service rather than the site.</summary>
    public static bool IsBotWallUrl(string url) =>
        Regex.IsMatch(url, @"_Incapsula_Resource|hcaptcha\.com|recaptcha|challenges\.cloudflare\.com|/cdn-cgi/challenge-platform|captcha-delivery\.com|perimeterx|px-captcha", RegexOptions.IgnoreCase);

    /// <summary>Page text of a bot-check interstitial ("Additional security check is required", "Just a moment…").</summary>
    public static bool IsBotWallText(string text) =>
        Regex.IsMatch(text, @"Incapsula incident|Additional security check is required|verify (that )?you are (a )?human|I am human|Checking (if the site connection is secure|your browser)|Just a moment\.\.\.|Press (and|&) hold|Access Denied.{0,80}Reference #", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    protected static async Task<string> BodyTextAsync(IPage page)
    {
        try { return await page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 5000 }); }
        catch (PlaywrightException) { return ""; }
    }

    protected static async Task SettleAsync(IPage page)
    {
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10_000 }); }
        catch (TimeoutException) { }
    }

    private static async Task<bool> PasswordFieldEmptyAsync(IPage page)
    {
        var field = page.Locator("input[type=password]").First;
        return await field.CountAsync() > 0 && string.IsNullOrEmpty(await field.InputValueAsync(new LocatorInputValueOptions { Timeout = 2000 }));
    }

    private async Task<DateTimeOffset?> RememberedUntilAsync(IBrowserContext browser)
    {
        var cookies = await browser.CookiesAsync(CookieUrls);
        var expiries = cookies
            .Where(c => c.Expires > 0 && (SignInCookieNames is null || SignInCookieNames.Contains(c.Name)))
            .Select(c => DateTimeOffset.FromUnixTimeSeconds((long)c.Expires))
            .ToList();
        // No persistent sign-in cookie means the session ends with the browser: remembered for ~0.
        return expiries.Count == 0 ? null : expiries.Max();
    }

    /// <summary>Downloads a URL with the browser's cookies. Returns null if it isn't a PDF.</summary>
    protected static async Task<string?> DownloadPdfAsync(IBrowserContext browser, string url, string workDir, string name)
    {
        var resp = await browser.APIRequest.GetAsync(url);
        if (!resp.Ok) return null;
        var bytes = await resp.BodyAsync();
        if (!PdfText.LooksLikePdf(bytes)) return null;
        var path = Path.Combine(workDir, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>Clicks something that triggers a file download and saves it.</summary>
    protected static async Task<string?> DownloadByClickAsync(IPage page, ILocator target, string workDir, string name)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => target.ClickAsync(), new PageRunAndWaitForDownloadOptions { Timeout = 60_000 });
        var path = Path.Combine(workDir, name);
        await download.SaveAsAsync(path);
        return PdfText.LooksLikePdf(await File.ReadAllBytesAsync(path)) ? path : null;
    }

    /// <summary>
    /// Clicks something that opens the PDF in a new window and saves it. Handles a window that
    /// downloads, one pointed at a normal URL, and one pointed at a <c>blob:</c> URL made by the page.
    /// </summary>
    protected static async Task<string?> DownloadByPopupAsync(IPage page, IBrowserContext browser, ILocator target, string workDir, string name)
    {
        var path = Path.Combine(workDir, name);
        var popup = await page.RunAndWaitForPopupAsync(() => target.ClickAsync(), new PageRunAndWaitForPopupOptions { Timeout = 60_000 });
        var downloaded = new TaskCompletionSource<IDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Download += (_, dl) => downloaded.TrySetResult(dl);
        try
        {
            // The page usually opens an empty window first and points it at the PDF once it's fetched.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !downloaded.Task.IsCompleted && popup.Url is "" or "about:blank")
                await Task.Delay(500);
            if (downloaded.Task.IsCompleted) await (await downloaded.Task).SaveAsAsync(path);
            else if (popup.Url.StartsWith("blob:", StringComparison.Ordinal))
            {
                // A blob URL can be read from the page that made it.
                var base64 = await page.EvaluateAsync<string>(
                    """async url => { const b = await (await fetch(url)).blob(); return await new Promise(r => { const f = new FileReader(); f.onload = () => r(String(f.result).split(',')[1] ?? ''); f.readAsDataURL(b); }); }""",
                    popup.Url);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64));
            }
            else if (popup.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return await DownloadPdfAsync(browser, popup.Url, workDir, name);
            else return null;
        }
        finally { await popup.CloseAsync(); }
        return PdfText.LooksLikePdf(await File.ReadAllBytesAsync(path)) ? path : null;
    }

    /// <summary>Saves a screenshot and the page HTML (no passwords: typed values aren't in the HTML).</summary>
    protected async Task<string?> SaveDiagnosticsAsync(IPage page, SiteContext ctx, string label)
    {
        try
        {
            var dir = Path.Combine(ctx.DiagnosticsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + label);
            Directory.CreateDirectory(dir);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, "page.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(dir, "page.html"), await page.ContentAsync());
            await File.WriteAllTextAsync(Path.Combine(dir, "url.txt"), page.Url);
            return dir;
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException)
        {
            ctx.Log.LogDebug(ex, "Could not save diagnostics");
            return null;
        }
    }
}
