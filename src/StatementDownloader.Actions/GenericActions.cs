using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
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
/// Runs a command. Settings: command; optional args (tokens {path} {date} {statement} {amount} {account}),
/// timeoutMinutes. Details are also in SD_* environment variables.
/// </summary>
public sealed class RunCommandAction : IDocumentAction
{
    public string Type => "run-command";

    public async Task<ActionResult> RunAsync(ActionContext ctx, CancellationToken ct = default)
    {
        var d = DocumentPayload.From(ctx);
        var amount = d.Amount?.ToString(CultureInfo.InvariantCulture) ?? "";
        var psi = new ProcessStartInfo(ctx.Require("command")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (ctx.Optional("args") is { } args)
            foreach (var a in SplitArgs(args))
                psi.ArgumentList.Add(a.Replace("{path}", d.Path).Replace("{date}", d.DocumentDate).Replace("{statement}", d.StatementNumber ?? "")
                    .Replace("{amount}", amount).Replace("{account}", d.AccountId));
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
