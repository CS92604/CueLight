using Cuelight.Core;

namespace Cuelight.Tests;

public sealed class LegacyFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cuelight-rename-" + Guid.NewGuid().ToString("N"));

    public LegacyFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string Old => Path.Combine(_root, AppPaths.LegacyAppName);
    private string New => Path.Combine(_root, AppPaths.AppName);

    [Fact]
    public void The_public_name_is_not_Claudes()
    {
        Assert.Equal("Cuelight", AppPaths.AppName);
        Assert.DoesNotContain("Claude", AppPaths.AppName);
        Assert.Equal("Claude Live Assistant", AppPaths.LegacyAppName);
    }

    [Fact]
    public void The_old_folder_with_its_files_becomes_the_new_one()
    {
        Directory.CreateDirectory(Path.Combine(Old, "models"));
        File.WriteAllText(Path.Combine(Old, "settings.json"), "{\"model\":\"claude-sonnet-5-5\"}");
        File.WriteAllText(Path.Combine(Old, "models", "ggml-base.en.bin"), "model bytes");

        AppPaths.MigrateLegacyFolder(_root);

        Assert.False(Directory.Exists(Old));
        Assert.Equal("{\"model\":\"claude-sonnet-5-5\"}", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("model bytes", File.ReadAllText(Path.Combine(New, "models", "ggml-base.en.bin")));
    }

    [Fact]
    public void A_saved_key_and_settings_are_still_found_afterwards()
    {
        var protector = new UserOnlyFileProtector();
        new ApiKeyStore(Old, protector).Save("sk-ant-test-key");
        new SettingsStore(Old).Save(new Settings { Model = "claude-haiku-5-5" });

        AppPaths.MigrateLegacyFolder(_root);

        Assert.Equal("sk-ant-test-key", new ApiKeyStore(New, protector).Load());
        Assert.Equal("claude-haiku-5-5", new SettingsStore(New).Load().Model);
    }

    [Fact]
    public void Nothing_happens_when_there_is_no_old_folder()
    {
        AppPaths.MigrateLegacyFolder(_root);
        Assert.False(Directory.Exists(Old));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void A_new_folder_that_already_exists_is_never_replaced()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "settings.json"), "old");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "settings.json"), "new");

        AppPaths.MigrateLegacyFolder(_root);

        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Old, "settings.json")));   // left alone, not lost
    }
}

/// <summary>The two notices the README must keep: where the code came from, and that this is not Anthropic's.</summary>
public class ReadmeNoticeTests
{
    private static string Readme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuelight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
    }

    [Fact]
    public void The_top_of_the_readme_says_it_was_made_with_Claude_Code()
    {
        var top = string.Join("\n", Readme().Split('\n').Take(10));
        Assert.Contains("Built with Claude Code", top);
        Assert.Contains("very large majority", top);
    }

    [Fact]
    public void The_end_of_the_readme_says_it_has_no_affiliation_with_Claude_or_Anthropic()
    {
        var readme = Readme();
        var at = readme.LastIndexOf("## Disclaimer", StringComparison.Ordinal);
        Assert.True(at >= 0, "the README needs a Disclaimer section");
        var disclaimer = readme[at..];
        Assert.Contains("not affiliated with", disclaimer);
        Assert.Contains("Anthropic", disclaimer);
        Assert.Contains("Claude", disclaimer);
        Assert.DoesNotContain("\n## ", disclaimer[3..]);   // and it is the last section
    }
}
