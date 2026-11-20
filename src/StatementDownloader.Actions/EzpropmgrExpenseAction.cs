using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StatementDownloader.Core;

namespace StatementDownloader.Actions;

/// <summary>
/// Records a document as an expense in ezpropmgr (rtrack2025) and attaches the PDF.
/// Settings: baseUrl, tokenSecret, one of buildingId/unitId/portfolioId, category, paidTo,
/// description, referencePrefix; optional type, subType, notes, attachAs (Receipt|Attachment1|Attachment2).
/// description and notes take the file-name tokens plus {fileName}, {vendorAccount} and
/// shifted dates such as {date-1M:MMMM yyyy} (a bill issued in March for February's usage).
/// </summary>
public sealed class EzpropmgrExpenseAction(HttpClient http) : IDocumentAction
{
    public string Type => "ezpropmgr-expense";

    public async Task<ActionResult> RunAsync(ActionContext ctx, CancellationToken ct = default)
    {
        var doc = ctx.Document;
        if (doc.Amount is not { } amount) return ActionResult.Fail("The amount couldn't be read from the document, so no expense was recorded.");

        var baseUrl = ctx.Require("baseUrl").TrimEnd('/');
        var token = await ctx.Secrets.GetValueAsync(ctx.Require("tokenSecret"), ct);
        int? Int(string key) => ctx.Optional(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
        var unitId = Int("unitId");
        var buildingId = Int("buildingId");
        var portfolioId = Int("portfolioId");
        if (unitId is null && buildingId is null && portfolioId is null) return ActionResult.Fail("Set unitId, buildingId or portfolioId.");

        var reference = ctx.Optional("referencePrefix") is { } prefix && doc.StatementNumber is { } sn ? prefix + sn : null;
        var attachAs = ctx.Optional("attachAs") ?? "Receipt";
        if (attachAs is not ("Receipt" or "Attachment1" or "Attachment2")) return ActionResult.Fail("attachAs must be Receipt, Attachment1 or Attachment2.");

        // 1. Already recorded? Same reference, or same amount on the same date (catches manual entries).
        var scope = unitId is { } u ? $"unitId={u}" : buildingId is { } b ? $"buildingId={b}" : $"search={Uri.EscapeDataString(reference ?? ctx.Require("paidTo"))}";
        using (var list = Request(HttpMethod.Get, $"{baseUrl}/api/Expense?{scope}&fromDate={doc.DocumentDate.AddDays(-45):yyyy-MM-dd}&pageSize=200", token))
        using (var resp = await http.SendAsync(list, ct))
        {
            if (!resp.IsSuccessStatusCode) return ActionResult.Fail($"Checking for an existing expense failed: {(int)resp.StatusCode} {await Short(resp, ct)}");
            var body = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            foreach (var e in body?["data"]?.AsArray() ?? [])
            {
                var sameRef = reference is not null && string.Equals(e?["checkNumber"]?.GetValue<string>()?.Trim(), reference, StringComparison.OrdinalIgnoreCase);
                var sameAmountDate = e?["expenseAmount"]?.GetValue<decimal>() is { } a && Math.Abs(a - amount) < 0.005m
                    && DateTime.TryParse(e?["entryDate"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var d) && DateOnly.FromDateTime(d) == doc.DocumentDate;
                if (sameRef || sameAmountDate) return ActionResult.Ok($"Already recorded as ezpropmgr expense {e?["id"]}.");
            }
        }

        // 2. Create the expense.
        var payload = new JsonObject
        {
            ["entryDate"] = doc.DocumentDate.ToString("yyyy-MM-dd"),
            ["category"] = ctx.Require("category"),
            ["type"] = ctx.Optional("type"),
            ["subType"] = ctx.Optional("subType"),
            ["expenseAmount"] = amount,
            ["buildingId"] = buildingId,
            ["unitId"] = unitId,
            ["portfolioId"] = portfolioId,
            ["isBuildingExpense"] = buildingId is not null && unitId is null,
            ["isUnitExpense"] = unitId is not null,
            ["paidTo"] = ctx.Require("paidTo"),
            ["description"] = Render(ctx.Require("description"), ctx.Account, doc),
            ["checkNumber"] = reference,
            ["notes"] = Render(ctx.Optional("notes") ?? "Source file {fileName}", ctx.Account, doc),
            ["isReportExclusion"] = false,
            ["periodStartDate"] = null,
            ["periodEndDate"] = null,
        };
        var json = payload.ToJsonString();
        int expenseId;
        using (var post = Request(HttpMethod.Post, $"{baseUrl}/api/Expense", token))
        {
            // Deterministic key: a retry of the same expense replays the original result instead of double-posting.
            post.Headers.Add("Idempotency-Key", "statement-downloader-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("POST /api/Expense\n" + json))));
            post.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(post, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return ActionResult.Fail($"Creating the expense failed: {(int)resp.StatusCode} {Trim(body)}");
            var node = JsonNode.Parse(body);
            if (node?["success"]?.GetValue<bool>() != true || node["id"] is null) return ActionResult.Fail($"Creating the expense failed: {Trim(body)}");
            expenseId = node["id"]!.GetValue<int>();
        }

        // 3. Attach the PDF.
        using (var upload = Request(HttpMethod.Post, $"{baseUrl}/api/Expense/{expenseId}/files", token))
        {
            await using var file = File.OpenRead(doc.Path);
            var form = new MultipartFormDataContent();
            var part = new StreamContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, attachAs + "File", Path.GetFileName(doc.Path));
            upload.Content = form;
            using var resp = await http.SendAsync(upload, ct);
            if (!resp.IsSuccessStatusCode)
                return ActionResult.Ok($"Recorded as ezpropmgr expense {expenseId} ({amount:C}), but attaching the PDF failed: {(int)resp.StatusCode} {await Short(resp, ct)}. Attach it by hand.");
        }

        return ActionResult.Ok($"Recorded as ezpropmgr expense {expenseId} ({amount.ToString("C", CultureInfo.GetCultureInfo("en-US"))}), PDF attached{(reference is null ? "" : $", reference {reference}")}.");
    }

    public static string Render(string template, AccountConfig account, DocumentRecord doc)
    {
        // {date-1M:FMT} / {date+7d:FMT}: the document date shifted by months or days.
        var shifted = System.Text.RegularExpressions.Regex.Replace(template, @"\{date([+-]\d+)([Md]):([^}]+)\}", m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var d = m.Groups[2].Value == "M" ? doc.DocumentDate.AddMonths(n) : doc.DocumentDate.AddDays(n);
            return d.ToString(m.Groups[3].Value, CultureInfo.InvariantCulture);
        });
        return FileNameTemplate.Render(shifted, account, doc.DocumentDate, doc.StatementNumber)
            .Replace("{fileName}", Path.GetFileName(doc.Path))
            .Replace("{vendorAccount}", doc.VendorAccountNumber ?? "");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    private static async Task<string> Short(HttpResponseMessage r, CancellationToken ct) => Trim(await r.Content.ReadAsStringAsync(ct));
    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
