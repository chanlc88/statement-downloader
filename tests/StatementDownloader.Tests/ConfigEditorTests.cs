using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public class ConfigEditorTests
{
    // Raw string literals keep the source file's line endings, and a Windows checkout turns them
    // into CRLF. The tests below edit this text with "\n" patterns, so pin it to LF.
    private static readonly string Yaml = """
        # My config — comments must survive edits
        documentsRoot: "G:/My Drive"   # Google Drive on this PC

        ui:
          port: 5187

        accounts:
          - id: water
            name: Water
            site: dekalb-water
            credential: env:X
            # where bills go
            folder: "Bills/Water/{year}"
            fileName: "{date:yyyy-MM-dd}-Water.pdf"
            actions:
              - type: webhook
                url: https://example.com/hook
                folder: not-the-account-folder
          - id: gas
            site: scana-energy
            credential: env:Y
            folder: Bills/Gas
            fileName: "{date:yyyy-MM-dd}-Gas.pdf"
        """.ReplaceLineEndings("\n");

    [Fact]
    public void Changes_one_account_and_keeps_everything_else()
    {
        var edited = ConfigEditor.SetAccountValue(Yaml, "gas", "folder", "Utilities/Gas/{year}");
        var config = ConfigEditor.Check(edited);
        Assert.Equal("Utilities/Gas/{year}", config.Accounts.Single(a => a.Id == "gas").Folder);
        Assert.Equal("Bills/Water/{year}", config.Accounts.Single(a => a.Id == "water").Folder);
        Assert.Contains("# My config — comments must survive edits", edited);
        Assert.Contains("# where bills go", edited);
        Assert.Contains("folder: not-the-account-folder", edited);   // nested action key untouched
        Assert.Equal(Yaml.Split('\n').Length, edited.Split('\n').Length);
    }

    [Fact]
    public void Does_not_touch_a_nested_key_with_the_same_name()
    {
        var edited = ConfigEditor.SetAccountValue(Yaml, "water", "folder", "New/{year}");
        Assert.Contains("    folder: \"New/{year}\"", edited);
        Assert.Contains("folder: not-the-account-folder", edited);
    }

    [Fact]
    public void Adds_a_missing_key_to_the_right_account()
    {
        var withoutName = ConfigEditor.SetAccountValue(Yaml, "gas", "name", "SCANA \"Gas\"");
        Assert.Equal("SCANA \"Gas\"", ConfigEditor.Check(withoutName).Accounts.Single(a => a.Id == "gas").Name);
    }

    [Fact]
    public void Changes_documents_root_and_keeps_its_comment()
    {
        var edited = ConfigEditor.SetDocumentsRoot(Yaml, @"D:\Docs");
        Assert.Equal(@"D:\Docs", ConfigEditor.Check(edited).DocumentsRoot);
        Assert.Contains("# Google Drive on this PC", edited);
    }

    [Fact]
    public void Keeps_windows_line_endings()
    {
        var crlf = Yaml.Replace("\n", "\r\n");
        var edited = ConfigEditor.SetAccountValue(crlf, "water", "fileName", "{date:yyyy-MM}-W.pdf");
        Assert.DoesNotContain("\n", edited.Replace("\r\n", ""));
        Assert.Equal("{date:yyyy-MM}-W.pdf", ConfigEditor.Check(edited).Accounts[0].FileName);
    }

    [Fact]
    public void Adds_group_after_name_and_tags_after_group()
    {
        var edited = ConfigEditor.SetAccountGroup(Yaml, "water", "Maple Street Rentals");
        edited = ConfigEditor.SetAccountTags(edited, "water", ["Utility", " water ", "utility"]);
        var water = ConfigEditor.Check(edited).Accounts.Single(a => a.Id == "water");
        Assert.Equal("Maple Street Rentals", water.Group);
        Assert.Equal(["utility", "water"], water.Tags);
        var lines = edited.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Equal(lines.IndexOf("name: Water") + 1, lines.IndexOf("group: \"Maple Street Rentals\""));
        Assert.Equal(lines.IndexOf("group: \"Maple Street Rentals\"") + 1, lines.IndexOf("tags: [\"utility\", \"water\"]"));
        // gas has no name: new keys go right after its id
        var gas = ConfigEditor.SetAccountGroup(Yaml, "gas", "Personal");
        Assert.Contains("  - id: gas\n    group: \"Personal\"", gas.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Replaces_a_block_list_and_removes_empty_values()
    {
        var block = Yaml.Replace("    name: Water\n", "    name: Water\n    group: Old\n    tags:\n      - a\n      - b\n    # where bills go\n", StringComparison.Ordinal)
                        .Replace("    # where bills go\n    # where bills go\n", "    # where bills go\n");
        Assert.Equal(["a", "b"], ConfigEditor.Check(block).Accounts[0].Tags);

        var edited = ConfigEditor.SetAccountTags(block, "water", ["c"]);
        Assert.Equal(["c"], ConfigEditor.Check(edited).Accounts[0].Tags);
        Assert.DoesNotContain("- a", edited);
        Assert.Contains("# where bills go", edited);

        var cleared = ConfigEditor.SetAccountGroup(ConfigEditor.SetAccountTags(edited, "water", []), "water", "  ");
        var water = ConfigEditor.Check(cleared).Accounts[0];
        Assert.Null(water.Group);
        Assert.Empty(water.Tags);
        Assert.DoesNotContain("group:", cleared);
        Assert.DoesNotContain("tags:", cleared);
        Assert.Equal("Bills/Water/{year}", water.Folder);
    }

    [Fact]
    public void Unknown_account_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => ConfigEditor.SetAccountValue(Yaml, "nope", "folder", "x"));

    [Fact]
    public void An_account_not_starting_with_its_id_gets_a_clear_message()
    {
        var idSecond = Yaml.Replace("  - id: gas\n    site: scana-energy\n", "  - site: scana-energy\n    id: gas\n");
        Assert.Equal("gas", ConfigEditor.Check(idSecond).Accounts[1].Id);   // still valid YAML
        var ex = Assert.Throws<InvalidDataException>(() => ConfigEditor.SetAccountValue(idSecond, "gas", "folder", "x"));
        Assert.Contains("move the line 'id: gas' to the top", ex.Message);
    }

    [Fact]
    public void Save_keeps_a_backup_and_refuses_an_invalid_config()
    {
        var dir = Directory.CreateTempSubdirectory("sd-cfg-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.yaml");
            File.WriteAllText(path, Yaml);
            ConfigEditor.Save(path, ConfigEditor.SetAccountValue(Yaml, "gas", "folder", "G2"));
            Assert.Equal(Yaml, File.ReadAllText(path + ".bak"));
            Assert.Contains("folder: \"G2\"", File.ReadAllText(path));

            var broken = ConfigEditor.SetAccountValue(Yaml, "gas", "fileName", "no-date.pdf");
            Assert.Throws<InvalidDataException>(() => ConfigEditor.Save(path, broken));
            Assert.Contains("folder: \"G2\"", File.ReadAllText(path));   // unchanged
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("Bills/{year}", null)]
    [InlineData("LLC - MAPLE/WATER Bills/{year}/{month}", null)]
    [InlineData("C:/Bills", "relative")]
    [InlineData("/Bills", "relative")]
    [InlineData("Bills/../Other", "..")]
    [InlineData("Bills/{date:yyyy}", "tokens")]
    [InlineData("", "required")]
    public void Folder_rules(string folder, string? problemContains)
    {
        var p = AccountLocation.FolderProblem(folder);
        if (problemContains is null) Assert.Null(p); else Assert.Contains(problemContains, p);
    }

    [Theory]
    [InlineData("{date:yyyy-MM-dd}-Bill.pdf", null)]
    [InlineData("{date:yyyy-MM-dd}-{statement}.pdf", null)]
    [InlineData("Bill.pdf", "date token")]
    [InlineData("{date:yyyy}/Bill.pdf", "subfolders")]
    [InlineData("{date:yyyy-MM-dd}-Bill.txt", ".pdf")]
    [InlineData("{date:yyyy-MM-dd}-Bill?.pdf", "characters")]
    public void File_name_rules(string fileName, string? problemContains)
    {
        var p = AccountLocation.FileNameProblem(fileName);
        if (problemContains is null) Assert.Null(p); else Assert.Contains(problemContains, p);
    }
}

public class AccountLocationTests
{
    private static string Config(string folder, string fileName) => $$"""
        documentsRoot: "C:/Docs"
        accounts:
          - id: water
            site: dekalb-water
            credential: env:X
            folder: "{{folder}}"
            fileName: "{{fileName}}"
        """;

    [Theory]
    [InlineData("C:/Elsewhere", "{date:yyyy-MM-dd}-W.pdf", "relative")]
    [InlineData("Bills/../../Windows", "{date:yyyy-MM-dd}-W.pdf", "..")]
    [InlineData("Bills", "../{date:yyyy-MM-dd}-W.pdf", "subfolders")]
    [InlineData("Bills", "{date:yyyy-MM-dd}-W.txt", ".pdf")]
    public void Hand_edited_config_gets_the_same_rules_as_Settings(string folder, string fileName, string problem)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse(Config(folder, fileName)));
        Assert.Contains(problem, ex.Message);
        Assert.Contains("water:", ex.Message);
    }

    [Fact]
    public void Folder_may_use_account_and_kind_tokens() =>
        Assert.Null(AccountLocation.FolderProblem("Bills/{account}/{kind}/{year}"));

    [Fact]
    public void Resolve_keeps_files_inside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-root");
        Assert.Equal(Path.Combine(root, "Bills", "2026", "a.pdf"), AccountLocation.Resolve(root, "Bills/2026", "a.pdf"));
        Assert.Throws<InvalidDataException>(() => AccountLocation.Resolve(root, "../outside", "a.pdf"));
        Assert.Throws<InvalidDataException>(() => AccountLocation.Resolve(root + "-sibling", "..", Path.Combine("sd-root", "a.pdf")));
        Assert.Throws<InvalidDataException>(() => AccountLocation.Resolve(root, Path.GetTempPath(), "a.pdf"));
    }

    [Fact]
    public void Adds_an_account_at_the_end_of_the_list_in_its_indentation()
    {
        const string yaml = "documentsRoot: \"~/Docs\"\naccounts:\n    - id: water\n      site: dekalb-water\n      credential: env:X\n      folder: Bills\n      fileName: \"{date:yyyy-MM-dd}.pdf\"\n\n# Where to send notices\nnotifications:\n  quietHours: \"22:00-07:00\"\n";
        var result = ConfigEditor.AddAccount(yaml, [("id", "gas"), ("name", "\"Gas Co\""), ("site", "scana-energy"), ("credential", "env:Y"), ("folder", "\"Gas\""), ("fileName", "\"{date:yyyy-MM-dd}.pdf\"")]);
        Assert.Contains("      fileName: \"{date:yyyy-MM-dd}.pdf\"\n\n    - id: gas\n      name: \"Gas Co\"\n", result);
        // The comment and the next section stay after the list.
        Assert.Contains("      fileName: \"{date:yyyy-MM-dd}.pdf\"\n\n# Where to send notices\nnotifications:", result);
        var config = ConfigEditor.Check(result);
        Assert.Equal(["water", "gas"], config.Accounts.Select(a => a.Id));
        Assert.Equal("Gas Co", config.Accounts[1].Name);
    }

    [Fact]
    public void Adds_the_first_account()
    {
        var result = ConfigEditor.AddAccount("documentsRoot: \"~/Docs\"\naccounts: []\n", [("id", "gas"), ("site", "scana-energy"), ("credential", "env:Y"), ("folder", "\"Gas\""), ("fileName", "\"{date:yyyy-MM-dd}.pdf\"")]);
        Assert.Equal("gas", Assert.Single(ConfigEditor.Check(result).Accounts).Id);
        Assert.Contains("accounts:\n  - id: gas\n    site: scana-energy\n", result);
    }
}
