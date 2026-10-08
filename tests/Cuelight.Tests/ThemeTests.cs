using System.Globalization;
using System.Xml.Linq;

namespace Cuelight.Tests;

/// <summary>
/// The look: a violet-tinted white in light mode, a very dark purple in dark mode, blue for buttons and
/// selections, violet and pink for the suggestion chips. These guard the palette and that its text stays readable.
/// </summary>
public class ThemeTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuelight.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static Dictionary<string, (int R, int G, int B)> Palette(string variant)
    {
        var doc = XDocument.Load(RepoFile("src", "Cuelight.App", "Styles", "Theme.axaml"));
        var dictionary = doc.Descendants(Avalonia + "ResourceDictionary").Single(e => (string?)e.Attribute(X + "Key") == variant);
        return dictionary.Elements(Avalonia + "SolidColorBrush").ToDictionary(
            e => (string)e.Attribute(X + "Key")!,
            e =>
            {
                var hex = ((string)e.Attribute("Color")!).TrimStart('#');
                return (int.Parse(hex[0..2], NumberStyles.HexNumber), int.Parse(hex[2..4], NumberStyles.HexNumber), int.Parse(hex[4..6], NumberStyles.HexNumber));
            });
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Lin(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void Light_and_dark_define_exactly_the_same_colours()
    {
        Assert.Equal(Palette("Light").Keys.OrderBy(k => k), Palette("Dark").Keys.OrderBy(k => k));
    }

    [Fact]
    public void The_light_neutrals_are_a_cool_violet_white_rather_than_a_warm_cream()
    {
        var light = Palette("Light");
        foreach (var name in new[] { "Bg", "SurfaceAlt", "Border", "BorderStrong" })
        {
            var c = light[name];
            Assert.True(c.B > c.R && c.B > c.G, $"{name} should lean blue-violet, not warm: {c}");
            Assert.True(c.R >= c.G, $"{name} should lean violet rather than blue-green: {c}");
        }
        Assert.True(Luminance(light["Bg"]) > 0.9, "the page is still white-ish");
        Assert.Equal((255, 255, 255), light["Surface"]);
    }

    [Fact]
    public void The_dark_background_is_a_very_dark_purple()
    {
        var dark = Palette("Dark");
        foreach (var name in new[] { "Bg", "Surface", "SurfaceAlt", "Border" })
        {
            var c = dark[name];
            Assert.True(c.B > c.G && c.R > c.G, $"{name} should be purple (red and blue above green): {c}");
        }
        Assert.True(Luminance(dark["Bg"]) < 0.012, "the page is nearly black");
        Assert.True(Luminance(dark["Bg"]) < Luminance(dark["Surface"]) && Luminance(dark["Surface"]) < Luminance(dark["SurfaceAlt"]), "dark layers step up in lightness");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Buttons_and_selections_are_blue_not_orange(string variant)
    {
        var p = Palette(variant);
        foreach (var name in new[] { "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentSoftHover", "AccentText" })
        {
            var c = p[name];
            Assert.True(c.B > c.R && c.B > c.G, $"{name} should be blue: {c}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_is_readable_on_every_surface_it_sits_on(string variant)
    {
        var p = Palette(variant);
        void AtLeast(double ratio, string fg, string bg) =>
            Assert.True(Contrast(p[fg], p[bg]) >= ratio, $"{variant}: {fg} on {bg} is {Contrast(p[fg], p[bg]):0.00}:1, needs {ratio}:1");

        AtLeast(7, "Text", "Bg");
        AtLeast(7, "Text", "Surface");
        AtLeast(7, "Text", "SurfaceAlt");
        AtLeast(4.5, "TextMuted", "Bg");
        AtLeast(4.5, "TextMuted", "Surface");
        AtLeast(4.5, "TextMuted", "SurfaceAlt");
        AtLeast(4.5, "OnAccent", "Accent");
        AtLeast(4.5, "OnAccent", "AccentPressed");
        AtLeast(3, "Accent", "Bg");
        AtLeast(4.5, "AccentText", "AccentSoft");
        AtLeast(4.5, "AccentText", "AccentSoftHover");
        AtLeast(4.5, "AccentText", "Surface");
        AtLeast(4.5, "VioletText", "VioletSoft");
        AtLeast(4.5, "PinkText", "PinkSoft");
        AtLeast(4.5, "SuccessText", "SuccessSoft");
        AtLeast(4.5, "Danger", "Surface");
        AtLeast(7, "Text", "AccentSoftHover");   // selected text in a box
    }

    [Fact]
    public void The_suggestion_chips_are_blue_violet_and_pink()
    {
        var theme = File.ReadAllText(RepoFile("src", "Cuelight.App", "Styles", "Theme.axaml"));
        Assert.Contains("Border.chip.say\"><Setter Property=\"Background\" Value=\"{DynamicResource AccentSoft}\"", theme);
        Assert.Contains("Border.chip.type\"><Setter Property=\"Background\" Value=\"{DynamicResource VioletSoft}\"", theme);
        Assert.Contains("Border.chip.answer\"><Setter Property=\"Background\" Value=\"{DynamicResource PinkSoft}\"", theme);
        foreach (var variant in new[] { "Light", "Dark" })
        {
            var p = Palette(variant);
            Assert.True(p["VioletSoft"].B > p["VioletSoft"].G && p["VioletSoft"].R > p["VioletSoft"].G, "violet is red + blue");
            Assert.True(p["PinkSoft"].R > p["PinkSoft"].G && p["PinkText"].R > p["PinkText"].G, "pink leans red");
        }
    }

    [Fact]
    public void No_orange_is_left_in_the_app()
    {
        var root = RepoFile("src", "Cuelight.App");
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (var old in new[] { "#D97757", "#CC6B4B", "#BE5F40", "#FAF9F5", "#262624" })
                Assert.False(text.Contains(old, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} still uses the old colour {old}");
        }
    }

    [Fact]
    public void The_bundled_fonts_are_open_ones_with_their_licence_and_no_Anthropic_typeface_remains()
    {
        var fonts = RepoFile("src", "Cuelight.App", "Assets", "Fonts");
        Assert.True(File.Exists(Path.Combine(fonts, "YoungSerif-Regular.ttf")));
        Assert.Contains("SIL OPEN FONT LICENSE", File.ReadAllText(Path.Combine(fonts, "LICENSE-YoungSerif.txt")));
        Assert.Contains("Assets/Fonts#Young Serif", File.ReadAllText(RepoFile("src", "Cuelight.App", "Styles", "Theme.axaml")));
        // Every font file in the folder has its licence next to it.
        foreach (var ttf in Directory.EnumerateFiles(fonts, "*.ttf"))
        {
            var name = Path.GetFileNameWithoutExtension(ttf).Split('-')[0];
            Assert.True(Directory.EnumerateFiles(fonts, $"LICENSE-{name}.*").Any(), $"{name} has no licence file beside it");
        }
    }
}
