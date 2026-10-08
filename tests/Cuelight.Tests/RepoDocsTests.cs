using System.Text.RegularExpressions;

namespace Cuelight.Tests;

/// <summary>What a stranger sees on the repository's front page: no broken links or pictures, the community files
/// are there, and nothing that looks like a secret has been committed.</summary>
public class RepoDocsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuelight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(params string[] extensions)
    {
        var root = Root();
        var skip = new[] { "bin", "obj", ".git", "TestResults", "screenshots", "publish" };
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(part => skip.Contains(part)));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("SECURITY.md")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    public void Every_relative_link_and_picture_points_at_a_file_that_exists(string document)
    {
        var path = Path.Combine(Root(), document);
        var text = File.ReadAllText(path);
        var targets = Regex.Matches(text, @"\]\(([^)\s]+)\)").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(text, @"src=""([^""]+)""").Select(m => m.Groups[1].Value))
            .Where(t => !Regex.IsMatch(t, @"^([a-z][a-z0-9+.-]*:|#|\.\./\.\./)", RegexOptions.IgnoreCase))   // web addresses, anchors, GitHub-relative (../../releases)
            .Select(t => t.Split('#')[0]).Where(t => t.Length > 0).Distinct().ToList();
        if (document == "README.md") Assert.True(targets.Count >= 8, "the README should show its pictures and point at its files");
        foreach (var target in targets)
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, target)) || Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, target)),
                $"{document} points at {target}, which doesn't exist");
    }

    [Fact]
    public void Every_in_page_link_in_the_readme_matches_a_heading()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "README.md"));
        string Slug(string heading) => Regex.Replace(heading.Trim().ToLowerInvariant().Replace(' ', '-'), @"[^a-z0-9_-]", "");
        var headings = Regex.Matches(text, @"^#{1,6}\s+(.+)$", RegexOptions.Multiline).Select(m => Slug(m.Groups[1].Value)).ToHashSet();
        foreach (var anchor in Regex.Matches(text, @"\]\(#([^)\s]+)\)").Select(m => m.Groups[1].Value).Distinct())
            Assert.True(headings.Contains(anchor), $"the README links to #{anchor}, but no heading has that name");
    }

    [Fact]
    public void The_community_and_legal_files_are_present()
    {
        foreach (var file in new[] { "LICENSE", "README.md", "SECURITY.md", "CONTRIBUTING.md", "THIRD-PARTY-NOTICES.md", ".gitignore",
                                     Path.Combine(".github", "ISSUE_TEMPLATE", "bug_report.yml"), Path.Combine(".github", "workflows", "build.yml") })
            Assert.True(File.Exists(Path.Combine(Root(), file)), $"{file} is missing");
    }

    [Fact]
    public void Every_package_the_app_uses_is_covered_by_the_third_party_notices()
    {
        var notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        var project = File.ReadAllText(Path.Combine(Root(), "src", "Cuelight.App", "Cuelight.App.csproj")) + File.ReadAllText(Path.Combine(Root(), "src", "Cuelight.Core", "Cuelight.Core.csproj"));
        foreach (var package in Regex.Matches(project, @"PackageReference Include=""([^""]+)""").Select(m => m.Groups[1].Value).Distinct())
        {
            var family = package.Split('.')[0];   // Avalonia.Desktop is covered by Avalonia, NAudio.Wasapi by NAudio, ...
            Assert.True(notices.Contains(package, StringComparison.OrdinalIgnoreCase) || notices.Contains(family, StringComparison.OrdinalIgnoreCase),
                $"{package} isn't mentioned in THIRD-PARTY-NOTICES.md");
        }
    }

    [Fact]
    public void Nothing_that_looks_like_an_API_key_or_a_personal_address_is_committed()
    {
        // The tests hold made-up keys on purpose, so they are not scanned; everything a visitor reads is.
        var tests = Path.Combine(Root(), "tests") + Path.DirectorySeparatorChar;
        var patterns = new (string Name, Regex Pattern)[]
        {
            ("an Anthropic key", new(@"sk-ant-[A-Za-z0-9_\-]{20,}")),
            ("an OpenAI key", new(@"\bsk-(proj-)?[A-Za-z0-9_\-]{32,}")),
            ("a Google key", new(@"AIza[0-9A-Za-z_\-]{30,}")),
            ("an NVIDIA key", new(@"nvapi-[A-Za-z0-9_\-]{20,}")),
            ("an xAI key", new(@"xai-[A-Za-z0-9]{20,}")),
            ("a GitHub token", new(@"\b(ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}")),
            ("a private key", new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
            ("a personal e-mail address", new(@"[A-Za-z0-9._%+-]+@(gmail|outlook|hotmail|yahoo|icloud|proton|protonmail)\.[a-z]{2,}", RegexOptions.IgnoreCase)),
            ("a path on someone's PC", new(@"[A-Za-z]:\\Users\\[A-Za-z0-9._-]+\\|/home/[a-z][a-z0-9_-]*/|/Users/[A-Za-z0-9._-]+/")),
        };
        foreach (var file in SourceFiles(".cs", ".axaml", ".md", ".yml", ".csproj", ".json", ".txt", ".py", ".manifest").Where(f => !f.StartsWith(tests)))
        {
            var text = File.ReadAllText(file);
            foreach (var (name, pattern) in patterns)
                Assert.False(pattern.IsMatch(text), $"{Path.GetRelativePath(Root(), file)} looks like it contains {name}");
        }
    }
}
