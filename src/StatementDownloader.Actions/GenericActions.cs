using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using StatementDownloader.Core;

namespace StatementDownloader.Actions;

/// <summary>Document details passed to webhooks and commands.</summary>
public sealed record DocumentPayload(
    string AccountId, string AccountName, string Kind, string DocumentDate,
    string? StatementNumber, decimal? Amount, string Path, string? Sha256)
{
    public static DocumentPayload From(ActionContext ctx) => new(
        ctx.Account.Id, ctx.Account.DisplayName, ctx.Account.KindWord, ctx.Document.DocumentDate.ToString("yyyy-MM-dd"),
        ctx.Document.StatementNumber, ctx.Document.Amount, ctx.Document.Path, ctx.Document.Sha256);
}

/// <summary>POSTs the document details as JSON. Settings: url; optional authorizationSecret (sent as the Authorization header).</summary>
public sealed class WebhookAction(HttpClient http) : IDocumentAction
{
    public string Type => "webhook";

    public async Task<ActionResult> RunAsync(ActionContext ctx, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ctx.Require("url")) { Content = JsonContent.Create(DocumentPayload.From(ctx)) };
        if (ctx.Optional("authorizationSecret") is { } secret)
            req.Headers.TryAddWithoutValidation("Authorization", await ctx.Secrets.GetValueAsync(secret, ct));
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode
            ? ActionResult.Ok($"Webhook accepted ({(int)resp.StatusCode}).")
            : ActionResult.Fail($"Webhook returned {(int)resp.StatusCode}.");
    }
}

/// <summary>
/// Runs a command. Settings: command; optional args, workingDirectory, timeoutMinutes.
/// Args take the tokens in <see cref="Render"/>. Details are also in SD_* environment variables.
/// The first line the command prints is shown as the result; a non-zero exit code is a failure.
/// </summary>
public sealed class RunCommandAction : IDocumentAction
{
    public string Type => "run-command";

    /// <summary>
    /// Fills in {path} {fileName} {date} {date:FMT} {date-1M:FMT} {date+7d:FMT} {year} {month}
    /// {statement} {amount} {vendorAccount} {account} {kind}. Shifted dates are for bills issued
    /// after the period they cover, e.g. {date-1M:MMMM yyyy}. Unknown tokens are left as they are.
    /// </summary>
    /// <exception cref="InvalidDataException">The template uses {statement} or {amount} and the document has none.</exception>
    public static string Render(string template, AccountConfig account, DocumentRecord doc) =>
        Regex.Replace(template, @"\{(\w+)(?:([+-]\d+)([Md]))?(?::([^}]+))?\}", m =>
        {
            var name = m.Groups[1].Value;
            var date = doc.DocumentDate;
            if (m.Groups[2].Success)
            {
                if (name != "date" || !m.Groups[4].Success) return m.Value;
                var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                date = m.Groups[3].Value == "M" ? date.AddMonths(n) : date.AddDays(n);
            }
            if (m.Groups[4].Success) return name == "date" ? date.ToString(m.Groups[4].Value, CultureInfo.InvariantCulture) : m.Value;
            return name switch
            {
                "path" => doc.Path,
                "fileName" => Path.GetFileName(doc.Path),
                "date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "year" => date.Year.ToString("D4", CultureInfo.InvariantCulture),
                "month" => date.Month.ToString("D2", CultureInfo.InvariantCulture),
                "statement" => doc.StatementNumber ?? throw new InvalidDataException("No statement number was read from the document, so the command wasn't run."),
                "amount" => doc.Amount?.ToString(CultureInfo.InvariantCulture) ?? throw new InvalidDataException("No amount was read from the document, so the command wasn't run."),
                "vendorAccount" => doc.VendorAccountNumber ?? "",
                "account" => account.Id,
                "kind" => account.KindWord,
                _ => m.Value,
            };
        });

    public async Task<ActionResult> RunAsync(ActionContext ctx, CancellationToken ct = default)
    {
        var d = DocumentPayload.From(ctx);
        var amount = d.Amount?.ToString(CultureInfo.InvariantCulture) ?? "";
        var psi = new ProcessStartInfo(ctx.Require("command")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (ctx.Optional("workingDirectory") is { } cwd) psi.WorkingDirectory = cwd;
        try
        {
            if (ctx.Optional("args") is { } args)
                foreach (var a in SplitArgs(args))
                    psi.ArgumentList.Add(Render(a, ctx.Account, ctx.Document));
        }
        catch (InvalidDataException ex) { return ActionResult.Fail(ex.Message); }
        psi.Environment["SD_FILE_NAME"] = Path.GetFileName(d.Path);
        psi.Environment["SD_VENDOR_ACCOUNT"] = ctx.Document.VendorAccountNumber ?? "";
        psi.Environment["SD_ACCOUNT_ID"] = d.AccountId;
        psi.Environment["SD_ACCOUNT_NAME"] = d.AccountName;
        psi.Environment["SD_KIND"] = d.Kind;
        psi.Environment["SD_PATH"] = d.Path;
        psi.Environment["SD_DATE"] = d.DocumentDate;
        psi.Environment["SD_STATEMENT"] = d.StatementNumber ?? "";
        psi.Environment["SD_AMOUNT"] = amount;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the command.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(double.Parse(ctx.Optional("timeoutMinutes") ?? "5", CultureInfo.InvariantCulture)));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            p.Kill(entireProcessTree: true);
            return ActionResult.Fail("The command timed out.");
        }
        var firstLine = (p.ExitCode == 0 ? await stdout : await stderr).Split('\n', 2)[0].Trim();
        return p.ExitCode == 0
            ? ActionResult.Ok(string.IsNullOrEmpty(firstLine) ? "Command finished." : firstLine)
            : ActionResult.Fail($"Command exited with {p.ExitCode}. {firstLine}");
    }

    internal static IEnumerable<string> SplitArgs(string s)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
