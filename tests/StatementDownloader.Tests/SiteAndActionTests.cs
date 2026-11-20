using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.Actions;
using StatementDownloader.Core;
using StatementDownloader.Secrets;
using StatementDownloader.Sites;

namespace StatementDownloader.Tests;

public class DekalbWaterParserTests
{
    // Synthetic text in the same order PdfPig extracts a real bill (numbers are made up).
    private const string Sample =
        "Usage = Gals x 100 Page 1 of 2 Account Number Statement Number 1234567890 98765432 Water & Sewer Bill for JANE DOE " +
        "Service Address: 100 MAIN ST Bill Date: 11/18/26 Bill Summary Balance Forward Current Charges Total Amount Due " +
        "$6.79 $73.21 $80.00 Due 12/09/26 Historical Usage Billing Details Previous Balance $106.50 Payment(s) Applied -$99.71 " +
        "Balance Forward $6.79 Current Charges $73.21 Adjustments $0.00 Total Due On or Before 12/09/26 $80.00 " +
        "Account Number 1234567890 Statement Number 98765432";

    // Older layout where each label is followed by its own number.
    private const string PairedSample =
        "Account Number 1234567890 Page 1 of 2 Statement Number 98765432 Water & Sewer Bill for JANE DOE " +
        "Bill Date: 11/18/26 Current Charges $73.21 Total Amount Due $80.00";

    [Theory]
    [InlineData(Sample)]
    [InlineData(PairedSample)]
    public void Reads_bill_fields(string text)
    {
        var p = new DekalbWaterBillParser().Parse(text)!;
        Assert.Equal(new DateOnly(2026, 11, 18), p.DocumentDate);
        Assert.Equal("98765432", p.StatementNumber);
        Assert.Equal(73.21m, p.Amount);              // current charges, not total due
        Assert.Equal("1234567890", p.AccountNumber);
    }

    [Fact]
    public void Ignores_other_documents() => Assert.Null(new DekalbWaterBillParser().Parse("Georgia Power statement Bill Date: 11/18/26"));

    /// <summary>
    /// Optional check against real bills on this machine: set STATEMENT_DOWNLOADER_DEKALB_SAMPLES to a
    /// folder of DeKalb bill PDFs named yyyy-MM-dd-*.pdf. Skipped (passes) when not set.
    /// </summary>
    [Fact]
    public void Reads_real_bills_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("STATEMENT_DOWNLOADER_DEKALB_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*Water-Bill*.pdf", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var f in files)
        {
            var p = new DekalbWaterBillParser().Parse(PdfText.Extract(f));
            if (p?.StatementNumber is null || p.Amount is null || p.AccountNumber is null || p.StatementNumber == p.AccountNumber)
                failures.Add($"{Path.GetFileName(f)}: {p}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Length} not parsed:\n" + string.Join("\n", failures));
    }
}

public class SecretParsingTests
{
    [Fact]
    public void Parses_json_login()
    {
        var c = SecretProvider.TryParseJsonLogin("""{"username":"me","password":"pw"}""")!;
        Assert.Equal(("me", "pw"), (c.Username, c.Password));
        Assert.DoesNotContain("pw", c.ToString());
    }

    [Fact]
    public void Plain_value_is_not_json_login() => Assert.Null(SecretProvider.TryParseJsonLogin("hunter2"));

    [Fact]
    public async Task Env_reference_resolves()
    {
        Environment.SetEnvironmentVariable("SD_TEST_LOGIN", """{"username":"u","password":"p"}""");
        var provider = new SecretProvider(new BitwardenConfig(), new AppPaths("x", Path.GetTempPath()), new EnvironmentTokenStore());
        var c = await provider.GetCredentialAsync("env:SD_TEST_LOGIN");
        Assert.Equal("u", c.Username);
    }
}

public class EzpropmgrActionTests
{
    private sealed class FakeSecrets : ISecretProvider
    {
        public Task<SiteCredential> GetCredentialAsync(string reference, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetValueAsync(string reference, CancellationToken ct = default) => Task.FromResult("token-123");
    }

    private sealed class FakeApi(string listJson) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? IdempotencyKey)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is StringContent ? await request.Content.ReadAsStringAsync(ct) : null;
            Calls.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.TryGetValues("Idempotency-Key", out var v) ? v.First() : null));
            Assert.Equal("Bearer token-123", request.Headers.Authorization?.ToString());
            var json = request.Method == HttpMethod.Get ? listJson
                : request.RequestUri!.AbsolutePath.EndsWith("/files") ? """{"success":true}""" : """{"success":true,"id":1001}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (ActionContext, string) Context(decimal? amount = 73.21m)
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"sd-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 test");
        var account = new AccountConfig { Id = "water", Kind = DocumentKind.Bill };
        var doc = new DocumentRecord { AccountId = "water", DocumentDate = new(2026, 11, 18), StatementNumber = "98765432", Amount = amount, Path = pdf };
        var settings = new Dictionary<string, string>
        {
            ["type"] = "ezpropmgr-expense", ["baseUrl"] = "https://example.test", ["tokenSecret"] = "env:X", ["buildingId"] = "5",
            ["category"] = "UTILITIES", ["paidTo"] = "DeKalb County", ["description"] = "Water & Sewer Bill - {date:yyyy-MM}", ["referencePrefix"] = "DKWTR-",
        };
        return (new ActionContext { Account = account, Document = doc, Settings = settings, Secrets = new FakeSecrets(), Log = NullLogger.Instance }, pdf);
    }

    [Fact]
    public async Task Records_and_attaches_new_expense()
    {
        var api = new FakeApi("""{"data":[]}""");
        var (ctx, pdf) = Context();
        var result = await new EzpropmgrExpenseAction(new HttpClient(api)).RunAsync(ctx);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Contains("1001", result.Summary);
        Assert.Equal(3, api.Calls.Count);
        Assert.Contains("buildingId=5", api.Calls[0].Url);
        var post = api.Calls[1];
        Assert.StartsWith("statement-downloader-", post.IdempotencyKey);
        Assert.Contains("\"checkNumber\":\"DKWTR-98765432\"", post.Body!);
        Assert.Contains("\"description\":\"Water & Sewer Bill - 2026-11\"", post.Body!.Replace("\\u0026", "&"));
        Assert.EndsWith("/api/Expense/1001/files", api.Calls[2].Url);
        File.Delete(pdf);
    }

    [Theory]
    [InlineData("""{"data":[{"id":1000,"checkNumber":"DKWTR-98765432","expenseAmount":1,"entryDate":"2026-01-01T00:00:00"}]}""")]
    [InlineData("""{"data":[{"id":1000,"checkNumber":null,"expenseAmount":73.21,"entryDate":"2026-11-18T00:00:00"}]}""")]
    public async Task Skips_when_already_recorded(string existing)
    {
        var api = new FakeApi(existing);
        var (ctx, pdf) = Context();
        var result = await new EzpropmgrExpenseAction(new HttpClient(api)).RunAsync(ctx);
        Assert.True(result.Succeeded);
        Assert.Contains("Already recorded as ezpropmgr expense 1000", result.Summary);
        Assert.Single(api.Calls);
        File.Delete(pdf);
    }

    [Fact]
    public async Task Fails_without_amount()
    {
        var (ctx, pdf) = Context(amount: null);
        var result = await new EzpropmgrExpenseAction(new HttpClient(new FakeApi("{}"))).RunAsync(ctx);
        Assert.False(result.Succeeded);
        File.Delete(pdf);
    }
}

public class DekalbBillListTests
{
    [Fact]
    public void Parses_bill_table_row()
    {
        var row = BillListRow.TryParse(["View Bill", "Dec 09, 2026", "$1,106.50", "Dec 30, 2026"],
            "MM_openBrWindow('/app/eBillViewerServlet?key=abc123','bill','menubar=no,width=800')")!;
        Assert.Equal(new DateOnly(2026, 12, 9), row.Date);
        Assert.Equal(1106.50m, row.Amount);
        Assert.Equal("/app/eBillViewerServlet?key=abc123", row.PdfPath);
    }

    [Theory]
    [InlineData("View Bill|not a date|$1.00", "MM_openBrWindow('/x','b','c')")]
    [InlineData("View Bill|Nov 18, 2026|$1.00", "somethingElse()")]
    [InlineData("View Bill|Nov 18, 2026|$1.00", null)]
    public void Rejects_unexpected_rows(string cells, string? onclick) =>
        Assert.Null(BillListRow.TryParse(cells.Split('|'), onclick));
}
