using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Cuelight.App.Tests;

public class FontTests
{
    /// <summary>If the font file went missing or its name changed, Avalonia would quietly draw everything in a
    /// system font instead; this catches that.</summary>
    [AvaloniaTheory]
    [InlineData("SerifFont")]
    [InlineData("SerifDisplayFont")]
    public void The_headings_and_suggestions_use_the_bundled_Young_Serif(string resource)
    {
        Assert.True(Application.Current!.TryGetResource(resource, null, out var found), $"{resource} is not defined");
        var family = Assert.IsType<FontFamily>(found);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphs), "the font did not load");
        Assert.Equal("Young Serif", glyphs!.FamilyName);
    }

    [AvaloniaFact]
    public void The_serif_has_the_characters_a_reply_can_contain()
    {
        Assert.True(Application.Current!.TryGetResource("SerifFont", null, out var found));
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface((FontFamily)found!), out var glyphs));
        foreach (var c in "Aa0?!.,;:'\"’“”—–…éñüß¿¡$%&@()")
            Assert.True(glyphs!.TryGetGlyph(c, out var g) && g != 0, $"the serif has no '{c}'");
    }
}
