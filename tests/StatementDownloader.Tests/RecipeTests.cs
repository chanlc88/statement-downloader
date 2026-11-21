using StatementDownloader.Sites;
using StatementDownloader.Sites.Recipes;

namespace StatementDownloader.Tests;

/// <summary>Runs the samples inside every built-in recipe (sites/*.yaml), plus the recipe engine's own rules.</summary>
public class RecipeTests
{
    public static TheoryData<string, int> DocumentSamples()
    {
        var data = new TheoryData<string, int>();
        foreach (var r in RecipeLoader.BuiltIn())
            for (var i = 0; i < r.Tests.Documents.Count; i++) data.Add(r.Id, i);
        return data;
    }

    public static TheoryData<string, int> RowSamples()
    {
        var data = new TheoryData<string, int>();
        foreach (var r in RecipeLoader.BuiltIn())
            for (var i = 0; i < r.Tests.Rows.Count; i++) data.Add(r.Id, i);
        return data;
    }

    private static SiteRecipe Recipe(string id) => RecipeLoader.BuiltIn().Single(r => r.Id == id);

    [Fact]
    public void Built_in_recipes_load_and_each_has_samples()
    {
        var recipes = RecipeLoader.BuiltIn();
        Assert.Contains(recipes, r => r.Id == "dekalb-water");
        foreach (var r in recipes)
        {
            Assert.True(r.Tests.Documents.Count(t => t.Expect is not null) > 0, $"{r.Id}: add at least one document sample under tests.documents");
            Assert.True(r.Tests.Rows.Count > 0, $"{r.Id}: add at least one list row sample under tests.rows");
            Assert.NotNull(r.Verified);
        }
    }

    [Theory]
    [MemberData(nameof(DocumentSamples))]
    public void Recipe_reads_its_document_samples(string id, int index)
    {
        var recipe = Recipe(id);
        var sample = recipe.Tests.Documents[index];
        var parsed = new RecipeParser(recipe.Parse).Parse(sample.Text);
        if (sample.Expect is null) { Assert.Null(parsed); return; }
        Assert.NotNull(parsed);
        Assert.Equal(DateOnly.Parse(sample.Expect.Date), parsed.DocumentDate);
        Assert.Equal(sample.Expect.StatementNumber, parsed.StatementNumber);
        Assert.Equal(sample.Expect.Amount, parsed.Amount);
        Assert.Equal(sample.Expect.AccountNumber, parsed.AccountNumber);
    }

    [Theory]
    [MemberData(nameof(RowSamples))]
    public void Recipe_reads_its_list_row_samples(string id, int index)
    {
        var recipe = Recipe(id);
        var sample = recipe.Tests.Rows[index];
        var list = recipe.Documents.List;
        var row = RecipeRows.Read(list, RecipeRows.Cell(list.Date, sample.Cells),
            list.Amount is { } a ? RecipeRows.Cell(a, sample.Cells) : null, sample.Link, recipe.Parse.DateFormats,
            clickToDownload: recipe.Documents.Download is "click" or "popup");
        Assert.NotNull(row);
        Assert.Equal(DateOnly.Parse(sample.Expect.Date), row.Date);
        Assert.Equal(sample.Expect.Amount, row.Amount);
        Assert.Equal(sample.Expect.Url, row.Link);
    }

    [Theory]
    [InlineData("View Bill|not a date|$1.00", "MM_openBrWindow('/x','b','c')")]
    [InlineData("View Bill|Nov 18, 2026|$1.00", "somethingElse()")]
    [InlineData("View Bill|Nov 18, 2026|$1.00", null)]
    [InlineData("View Bill", "MM_openBrWindow('/x','b','c')")]
    public void Skips_rows_that_are_not_documents(string cells, string? link)
    {
        var list = Recipe("dekalb-water").Documents.List;
        var c = cells.Split('|');
        Assert.Null(RecipeRows.Read(list, RecipeRows.Cell(list.Date, c), list.Amount is { } a ? RecipeRows.Cell(a, c) : null, link));
    }

    [Fact]
    public void Plain_href_links_are_used_as_is_and_javascript_links_are_skipped()
    {
        var list = new ListRecipe { Rows = "tr", Date = new FieldRecipe { Column = 1 } };
        Assert.Equal("/bills/1.pdf", RecipeRows.Read(list, "2026-01-05", null, "/bills/1.pdf")!.Link);
        Assert.Null(RecipeRows.Read(list, "2026-01-05", null, "javascript:void(0)"));
    }

    [Fact]
    public void Field_pattern_narrows_cell_text()
    {
        var list = new ListRecipe { Rows = "tr", Date = new FieldRecipe { Column = 1, Pattern = @"Statement (\S+)" }, Amount = new FieldRecipe { Column = 2, Pattern = @"Due:\s*(\S+)" } };
        var row = RecipeRows.Read(list, "Statement 01/05/2026 (PDF)", "Due: $12.00", "/s.pdf")!;
        Assert.Equal(new DateOnly(2026, 1, 5), row.Date);
        Assert.Equal(12m, row.Amount);
    }

    [Fact]
    public void Step_options_fill_placeholders_and_must_be_declared()
    {
        Assert.Equal("[role=option]:has-text('42')", RecipeOptions.Fill("[role=option]:has-text('{billingAccount}')", new Dictionary<string, string> { ["billingAccount"] = "42" }, "acct"));
        var missing = Assert.Throws<InvalidDataException>(() => RecipeOptions.Fill("{billingAccount}", new Dictionary<string, string>(), "acct"));
        Assert.Contains("siteOptions", missing.Message);

        var undeclared = MinimalRecipe("opt").Replace("documents:", "documents:\n  steps: [ { click: \"#a-{other}\" } ]");
        var ex = Assert.Throws<InvalidDataException>(() => RecipeLoader.Parse(undeclared, "x.yaml"));
        Assert.Contains("{other}", ex.Message);
    }

    [Fact]
    public void Click_downloads_need_no_url()
    {
        var list = new ListRecipe { Rows = "tr", Date = new FieldRecipe { Column = 1 } };
        Assert.NotNull(RecipeRows.Read(list, "11/4/2026", null, null, clickToDownload: true));
        Assert.Null(RecipeRows.Read(list, "11/4/2026", null, null));
    }

    [Fact]
    public void Statement_number_equal_to_account_number_is_dropped()
    {
        var parse = new ParseRecipe { Date = ["Date (\\S+)"], StatementNumber = ["Number (\\d+)"], AccountNumber = ["Number (\\d+)"] };
        var p = new RecipeParser(parse).Parse("Date 01/05/2026 Number 12345")!;
        Assert.Equal("12345", p.AccountNumber);
        Assert.Null(p.StatementNumber);
    }

    [Fact]
    public void Extra_date_formats_are_used()
    {
        var parse = new ParseRecipe { Date = ["Issued (\\S+)"], DateFormats = ["dd.MM.yyyy"] };
        Assert.Equal(new DateOnly(2026, 3, 4), new RecipeParser(parse).Parse("Issued 04.03.2026")!.DocumentDate);
    }

    [Fact]
    public void Invalid_recipe_lists_every_problem()
    {
        const string yaml = """
            id: Bad Id
            name: ""
            signIn: { url: "http://example.com", username: "#u", password: "#p", submit: "#s", signedIn: "#out" }
            documents:
              steps: [ { click: "#a", goto: "/b" } ]
              list: { rows: "tr", date: { column: 0 }, link: { pattern: "no group" } }
              download: email
            parse: { date: "Date \\S+" }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => RecipeLoader.Parse(yaml, "bad.yaml"));
        foreach (var expected in new[] { "id is required", "name is required", "https://", "exactly one of click", "counts from 1", "link.pattern", "fetch, click or popup", "parse.date" })
            Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Patterns_can_be_one_string_or_a_list()
    {
        var r = RecipeLoader.Parse(MinimalRecipe("only-one").Replace("date: 'Date (\\S+)'", "date: ['Bill (\\S+)', 'Date (\\S+)']"), "x.yaml");
        Assert.Equal(2, r.Parse.Date.Count);
        Assert.Single(RecipeLoader.Parse(MinimalRecipe("only-one"), "x.yaml").Parse.Date);
    }

    [Fact]
    public void Local_recipe_adds_a_site_and_can_replace_a_built_in_one()
    {
        var dir = Directory.CreateTempSubdirectory("sd-recipes-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "my-site.yaml"), MinimalRecipe("my-site"));
            File.WriteAllText(Path.Combine(dir, "dekalb.yaml"), MinimalRecipe("dekalb-water"));
            var sites = SiteRegistry.All(dir).ToDictionary(s => s.SiteId);
            Assert.True(sites.ContainsKey("my-site"));
            Assert.Equal("Minimal dekalb-water", sites["dekalb-water"].DisplayName);
            Assert.Equal(Path.Combine(dir, "dekalb.yaml"), ((RecipeSiteFetcher)sites["dekalb-water"]).Recipe.Source);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Built_in_sites_without_local_folder() =>
        Assert.Contains(SiteRegistry.All(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())), s => s.SiteId == "dekalb-water");

    /// <summary>
    /// Optional check against real bills on this machine: set STATEMENT_DOWNLOADER_DEKALB_SAMPLES to a
    /// folder of DeKalb bill PDFs. Skipped (passes) when not set.
    /// </summary>
    [Fact]
    public void DeKalb_recipe_reads_real_bills_when_available()
    {
        var dir = Environment.GetEnvironmentVariable("STATEMENT_DOWNLOADER_DEKALB_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var parser = new RecipeParser(Recipe("dekalb-water").Parse);
        var files = Directory.GetFiles(dir, "*Water-Bill*.pdf", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var f in files)
        {
            var p = parser.Parse(PdfText.Extract(f));
            if (p?.StatementNumber is null || p.Amount is null || p.AccountNumber is null)
                failures.Add($"{Path.GetFileName(f)}: {p}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Length} not parsed:\n" + string.Join("\n", failures));
    }

    private static string MinimalRecipe(string id) => $$"""
        id: {{id}}
        name: Minimal {{id}}
        verified: 2026-01-01
        signIn: { url: "https://example.com/login", username: "#u", password: "#p", submit: "#s", signedIn: "#out" }
        documents:
          list: { rows: "tr", date: { column: 1 }, link: { selector: a } }
        parse:
          date: 'Date (\S+)'
        """;
}
