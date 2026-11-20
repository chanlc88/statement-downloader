using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.Actions;
using StatementDownloader.Core;
using StatementDownloader.Secrets;
using StatementDownloader.Sites;

namespace StatementDownloader.Tests;

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
    [Fact]
    public void Description_can_use_shifted_dates_and_vendor_account()
    {
        var account = new AccountConfig { Id = "gas", Kind = DocumentKind.Bill };
        var doc = new DocumentRecord { DocumentDate = new DateOnly(2026, 3, 10), StatementNumber = "111", VendorAccountNumber = "1234567", Path = "/x/2026-03-10-Gas.pdf" };
        Assert.Equal("Natural Gas - February 2026", EzpropmgrExpenseAction.Render("Natural Gas - {date-1M:MMMM yyyy}", account, doc));
        Assert.Equal("2026-03-17 | Invoice 111 | Billing account 1234567 | 2026-03-10-Gas.pdf",
            EzpropmgrExpenseAction.Render("{date+7d:yyyy-MM-dd} | Invoice {statement} | Billing account {vendorAccount} | {fileName}", account, doc));
    }

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
