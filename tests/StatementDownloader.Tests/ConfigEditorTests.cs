using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public class ConfigEditorTests
{
    private const string Yaml = """
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
        """;

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
    public void Unknown_account_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => ConfigEditor.SetAccountValue(Yaml, "nope", "folder", "x"));

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
    [InlineData("LLC - DAUNTLESS/WATER Bills/{year}/{month}", null)]
    [InlineData("C:/Bills", "relative")]
    [InlineData("/Bills", "relative")]
    [InlineData("Bills/../Other", "..")]
    [InlineData("Bills/{date:yyyy}", "tokens")]
    [InlineData("", "required")]
    public void Folder_rules(string folder, string? problemContains)
    {
        var p = ConfigEditor.FolderProblem(folder);
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
        var p = ConfigEditor.FileNameProblem(fileName);
        if (problemContains is null) Assert.Null(p); else Assert.Contains(problemContains, p);
    }
}
