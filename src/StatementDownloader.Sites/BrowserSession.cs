using System.Diagnostics;
using Microsoft.Playwright;

namespace StatementDownloader.Sites;

/// <summary>A browser for one run: the app's own automated one, or a regular browser window the person signs in to.</summary>
public sealed class BrowserSession : IAsyncDisposable
{
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; init; }
    private Func<ValueTask> _close = () => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _close();

    /// <summary>The app's browser with a persistent profile (headless unless someone is assisting).</summary>
    public static async Task<BrowserSession> AutomatedAsync(IPlaywright playwright, string profileDir, bool headless)
    {
        var context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            AcceptDownloads = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        return new BrowserSession { Context = context, Page = page, _close = () => context.DisposeAsync() };
    }

    /// <summary>
    /// Opens a regular Chrome (or Edge) window on <paramref name="url"/> with its own profile, for the
    /// person to sign in to, and connects to it over the browser's local debugging port. Nothing is
    /// done to hide the connection; the window is simply a normal browser the person uses.
    /// </summary>
    public static async Task<BrowserSession> YoursAsync(IPlaywright playwright, string profileDir, string url, bool keepOpen, CancellationToken ct)
    {
        // One debugging port per profile. If that browser is still open (another account on the same
        // login just used it, or a run was interrupted), reuse it rather than start a second copy,
        // which the browser would hand over to the running one without a debugging port.
        var port = PortFor(profileDir);
        if (OpenWindows.TryRemove(profileDir, out var idle)) idle.Cancel();
        if (!await IsListeningAsync(port))
        {
            var exe = FindBrowser() ?? throw new InvalidOperationException("No Google Chrome or Microsoft Edge was found to open for signing in.");
            // Its own console output (sync, extension and update chatter) isn't ours to log.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // Each browser gets its own profile folder: Chrome and Edge can't share one.
            var dataDir = profileDir + (Path.GetFileNameWithoutExtension(exe).Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "-chrome" : "-browser");
            foreach (var arg in new[] { $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={dataDir}",
                                        "--no-first-run", "--no-default-browser-check",
                                        // A new profile would otherwise sign in to the browser account and sync
                                        // its extensions, each opening a welcome tab; the sign-in needs neither.
                                        "--disable-sync", "--disable-extensions", "--new-window", url })
                psi.ArgumentList.Add(arg);
            // The browser may relaunch itself, so the started process isn't tracked: it's closed through its port.
            var started = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
            started.OutputDataReceived += (_, _) => { };
            started.ErrorDataReceived += (_, _) => { };
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
        }

        IBrowser? browser = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (browser is null)
        {
            try { browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}"); }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline) { await Task.Delay(500, ct); }
        }
        var context = browser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("The browser opened without a window.");
        var page = context.Pages.FirstOrDefault(p => p.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                   ?? context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        return new BrowserSession
        {
            Context = context,
            Page = page,
            _close = async () =>
            {
                // Disconnect, and close the window — or keep it briefly for the other accounts on this login.
                try { await browser.CloseAsync(); } catch (PlaywrightException) { }
                if (!keepOpen) { await CloseBrowserAsync(port); return; }
                var timer = new CancellationTokenSource();
                OpenWindows[profileDir] = timer;
                _ = Task.Delay(KeepOpen, timer.Token).ContinueWith(async t =>
                {
                    if (t.IsCanceled || !OpenWindows.TryRemove(new KeyValuePair<string, CancellationTokenSource>(profileDir, timer))) return;
                    await CloseBrowserAsync(port);
                }, TaskScheduler.Default);
            },
        };
    }

    /// <summary>How long a signed-in window stays open after a run, for the other accounts on the login.</summary>
    private static readonly TimeSpan KeepOpen = TimeSpan.FromMinutes(2);
    /// <summary>Windows kept open for reuse: profile → its close timer.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Closes any windows kept open for reuse (before the app exits).</summary>
    public static void CloseKeptWindows()
    {
        foreach (var (profileDir, timer) in OpenWindows)
            if (OpenWindows.TryRemove(profileDir, out _))
            {
                timer.Cancel();
                CloseBrowserAsync(PortFor(profileDir)).GetAwaiter().GetResult();
            }
    }

    /// <summary>A port in 40000–49999 chosen by the profile path, so a running window can be found again.</summary>
    private static int PortFor(string profileDir) =>
        40000 + (int)(BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profileDir.ToLowerInvariant()))) % 10000);

    private static readonly HttpClient Local = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static async Task<bool> IsListeningAsync(int port)
    {
        try { return (await Local.GetAsync($"http://127.0.0.1:{port}/json/version")).IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>Asks the browser on <paramref name="port"/> to close (the DevTools "Browser.close" command).</summary>
    private static async Task CloseBrowserAsync(int port)
    {
        try
        {
            using var version = System.Text.Json.JsonDocument.Parse(await Local.GetStringAsync($"http://127.0.0.1:{port}/json/version"));
            var ws = version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!;
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(ws), timeout.Token);
            await socket.SendAsync("""{"id":1,"method":"Browser.close"}"""u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, true, timeout.Token);
            // Wait for the browser to take the command: dropping the connection straight away can lose it.
            await socket.ReceiveAsync(new byte[4096], timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                   or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or KeyNotFoundException) { /* already closed */ }
    }

    /// <summary>Google Chrome, or Microsoft Edge when Chrome isn't installed.</summary>
    private static string? FindBrowser()
    {
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                .Select(Environment.GetFolderPath).Where(r => r.Length > 0);
            candidates = [.. roots.Select(r => Path.Combine(r, "Google", "Chrome", "Application", "chrome.exe")),
                          .. roots.Select(r => Path.Combine(r, "Microsoft", "Edge", "Application", "msedge.exe"))];
        }
        else if (OperatingSystem.IsMacOS())
            candidates = ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"];
        else
            candidates = ["/usr/bin/google-chrome", "/usr/bin/microsoft-edge", "/usr/bin/chromium", "/usr/bin/chromium-browser"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
