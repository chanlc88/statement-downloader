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

public class RunCommandActionTests
{
    private static readonly AccountConfig Account = new() { Id = "gas", Kind = DocumentKind.Bill };

    private static DocumentRecord Doc(decimal? amount = 73.21m, string? statement = "111") => new()
    {
        AccountId = "gas", DocumentDate = new DateOnly(2026, 3, 10), StatementNumber = statement, Amount = amount,
        VendorAccountNumber = "1234567", Path = Path.Combine(Path.GetTempPath(), "2026-03-10-Gas.pdf"),
    };

    [Fact]
    public void Renders_document_tokens_and_shifted_dates()
    {
        Assert.Equal("Natural Gas - February 2026", RunCommandAction.Render("Natural Gas - {date-1M:MMMM yyyy}", Account, Doc()));
        Assert.Equal("2026-03-17 | Invoice 111 | Billing account 1234567 | 2026-03-10-Gas.pdf | 73.21 | 2026-03-10 | 2026/03 | gas bill",
            RunCommandAction.Render("{date+7d:yyyy-MM-dd} | Invoice {statement} | Billing account {vendorAccount} | {fileName} | {amount} | {date} | {year}/{month} | {account} {kind}", Account, Doc()));
        Assert.Equal("{unknown} {other:x}", RunCommandAction.Render("{unknown} {other:x}", Account, Doc()));
    }

    [Theory]
    [InlineData("{amount}")]
    [InlineData("REF-{statement}")]
    public void Missing_amount_or_statement_is_an_error(string template) =>
        Assert.Throws<InvalidDataException>(() => RunCommandAction.Render(template, Account, Doc(amount: null, statement: null)));

    private static ActionContext Context(DocumentRecord doc, string args) => new()
    {
        Account = Account, Document = doc, Secrets = null!, Log = NullLogger.Instance,
        Settings = OperatingSystem.IsWindows()
            ? new Dictionary<string, string> { ["type"] = "run-command", ["command"] = "cmd", ["args"] = "/c echo " + args }
            : new Dictionary<string, string> { ["type"] = "run-command", ["command"] = "sh", ["args"] = "-c \"echo " + args + "\"" },
    };

    [Fact]
    public async Task Runs_the_command_with_rendered_args()
    {
        var result = await new RunCommandAction().RunAsync(Context(Doc(), "Gas {date-1M:MMMM} {amount}"));
        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal("Gas February 73.21", result.Summary);
    }

    [Fact]
    public async Task Does_not_run_without_the_amount()
    {
        var result = await new RunCommandAction().RunAsync(Context(Doc(amount: null), "{amount}"));
        Assert.False(result.Succeeded);
        Assert.Contains("No amount", result.Summary);
    }
}
