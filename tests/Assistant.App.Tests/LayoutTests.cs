using System.Reflection;
using Assistant.App.ViewModels;
using Assistant.App.Views;
using Assistant.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Assistant.App.Tests;

/// <summary>
/// Measures the windows instead of eyeballing them: left and right gaps match, nothing runs off the
/// edge at the smallest window size, and the screens look right at every display scale Windows offers
/// (100%, 125%, 150%, 200%, 250%). Also saves pictures of each, for a person to look at.
/// </summary>
public class LayoutTests
{
    static readonly string ShotDir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    static readonly double[] Scales = { 1.0, 1.25, 1.5, 2.0, 2.5 };

    // -- helpers ---------------------------------------------------------------------------------

    /// <summary>Pretends the window is on a screen with this display scaling (the headless screen is always 100%).</summary>
    static void SetScale(TopLevel window, double scale)
    {
        var impl = window.PlatformImpl!;
        impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(impl, scale);
        ((Action<double>?)impl.GetType().GetProperty("ScalingChanged")!.GetValue(impl))?.Invoke(scale);
        UiTests.Settle();
    }

    static Rect Box(Visual v, Visual root)
    {
        var p = v.TranslatePoint(new Point(0, 0), root) ?? default;
        return new Rect(p, v.Bounds.Size);
    }

    static IEnumerable<T> Shown<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible && v.Bounds.Width > 0 && v.Bounds.Height > 0
            && !v.GetVisualAncestors().OfType<ScrollBar>().Any()); // a scroll bar's own parts live at the edge by design

    static bool Has(StyledElement e, string cls) => e.Classes.Contains(cls);

    /// <summary>Where the visible part of a control ends on the right: a button with no outline (a text button)
    /// shows only its text, so its padding doesn't count.</summary>
    static double InkRight(Control c, Visual root)
    {
        var box = Box(c, root);
        if (c is ToggleSwitch { OnContent: "" }) return box.Right - 12; // Fluent keeps a 12 px gap after the switch for a label
        return c is Button b && Has(b, "ghost") ? box.Right - b.Padding.Right : box.Right;
    }

    static void Near(double expected, double actual, string what, double tolerance = 0.75) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:0.#}, got {actual:0.#}");

    /// <summary>The space between a card's edge and its content must be the same on both sides.</summary>
    static void CardInsetsMatch(Border card, Visual root, string what)
    {
        var box = Box(card, root);
        var leaves = Shown<Control>(card).Where(c => c is TextBlock or Button or ToggleSwitch or Border { Width: > 0 }).ToList();
        if (leaves.Count == 0) return;
        double left = leaves.Min(c => Box(c, root).Left) - box.Left;
        double right = box.Right - leaves.Where(c => c is Button or ToggleSwitch).Select(c => InkRight(c, root)).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(right)) return; // text only: it wraps to the card's width
        Assert.True(Math.Abs(left - right) <= 3, $"{what}: content is {left:0.#} from the left edge but {right:0.#} from the right");
    }

    static void AllInside(Window window, string what)
    {
        double w = window.ClientSize.Width;
        foreach (var v in Shown<Control>(window))
        {
            var box = Box(v, window);
            if (v is ScrollViewer sv) Assert.True(sv.Extent.Width <= sv.Viewport.Width + 0.5, $"{what}: a scroll area is wider than it is shown ({sv.Extent.Width:0} > {sv.Viewport.Width:0})");
            if (v.GetType().Name is "ScrollContentPresenter" or "OverlayPopupHost") continue;
            Assert.True(box.Left >= -0.5 && box.Right <= w + 0.5, $"{what}: {v.GetType().Name} {v.Name} spans {box.Left:0}–{box.Right:0} in a window {w:0} wide");
        }
    }

    static UiTests.Rig FullMain(bool type = true)
    {
        var rig = UiTests.Rig.Make(typeEnabled: type);
        rig.Vm.SetListening();
        if (type) rig.Engine.SetRegion(new Region(700, 120, 420, 160));
        rig.Engine.AddTurn(Speaker.Them, "Hey, can you start on Monday morning at nine?");
        rig.Engine.AddTurn(Speaker.Me, "I think so, let me check my calendar.");
        rig.Engine.AddTurn(Speaker.Them, "Great. Also, did you see my message about the schedule?");
        UiTests.Pump(() => rig.Vm.Sections.Count >= 1 && rig.Vm.Sections[^1].Options.Count >= 1 && rig.Vm.Sections[^1].Options[^1].IsComplete && rig.Vm.IsListening);
        return rig;
    }

    static SettingsWindow MakeSettings(string? key = "sk-ant-api03-test1234")
    {
        var vm = new SettingsViewModel(new Settings(), () => { }, new KeyEntryViewModel(), key, _ => { });
        var w = new SettingsWindow { DataContext = vm };
        w.Show();
        UiTests.Settle();
        return w;
    }

    static void Save(TopLevel w, string name)
    {
        UiTests.Settle();
        Directory.CreateDirectory(Path.Combine(ShotDir, "layout"));
        w.CaptureRenderedFrame()!.Save(Path.Combine(ShotDir, "layout", name + ".png"));
    }

    // -- the main window -------------------------------------------------------------------------

    [AvaloniaFact]
    public void Main_window_has_equal_gaps_left_and_right()
    {
        using var rig = FullMain();
        var w = rig.Window;
        double width = w.ClientSize.Width;

        foreach (var card in Shown<Border>(w).Where(b => Has(b, "card") || Has(b, "option") || Has(b, "composer")))
        {
            var box = Box(card, w);
            Near(18, box.Left, "a card's left gap");
            Near(18, width - box.Right, "a card's right gap");
            // The chat box is a pill whose round send button sits closer to the end than the text does, to follow the curve.
            if (!Has(card, "composer")) CardInsetsMatch(card, w, "a card in the main window");
        }

        // Text and small icons sit 4 px inside the cards' edges, on both sides.
        var suggestions = Shown<TextBlock>(w).First(t => t.Text == "SUGGESTIONS");
        Near(22, Box(suggestions, w).Left, "the SUGGESTIONS label's left gap");
        var regenerate = Shown<Button>(w).First(b => Has(b, "ghost") && ToolTip.GetTip(b) is string s && s.Contains("Regenerate", StringComparison.OrdinalIgnoreCase) || (b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text == "Regenerate")));
        Near(22, width - InkRight(regenerate, w), "Regenerate's right gap");
        var clear = Shown<Button>(w).First(b => b.Content as string == "Clear chat");
        Near(22, width - InkRight(clear, w), "Clear chat's right gap");

        // The pills line up with the cards.
        var pills = Shown<ToggleButton>(w).Where(b => Has(b, "pill")).ToList();
        Assert.Equal(2, pills.Count);
        Near(18, Box(pills[0], w).Left, "the first pill's left gap");
    }

    [AvaloniaFact]
    public void Main_window_without_a_text_area_card_is_still_even()
    {
        using var rig = UiTests.Rig.Make(typeEnabled: true);
        rig.Vm.SetListening();
        UiTests.Settle();
        var w = rig.Window;
        var card = Shown<Border>(w).First(b => Has(b, "card") && Shown<TextBlock>(b).Any(t => t.Text == "Text area to watch"));
        CardInsetsMatch(card, w, "the text-area card with no area picked");
        Near(18, w.ClientSize.Width - Box(card, w).Right, "its right gap");

        rig.Engine.SetRegion(new Region(10, 10, 300, 200));
        UiTests.Settle();
        CardInsetsMatch(card, w, "the text-area card with an area picked");
    }

    [AvaloniaFact]
    public void Main_window_status_line_with_retry_lines_up()
    {
        using var rig = UiTests.Rig.Make();
        rig.Vm.SetProblem("Couldn't set up speech recognition: no internet connection. Check your connection and press Retry.", canRetry: true);
        UiTests.Settle();
        var w = rig.Window;
        var retry = Shown<Button>(w).First(b => b.Content as string == "Retry");
        Near(22, w.ClientSize.Width - InkRight(retry, w), "Retry's right gap");
        var dot = Shown<Avalonia.Controls.Shapes.Ellipse>(w).First(e => Has(e, "dot"));
        Near(22, Box(dot, w).Left, "the status dot's left gap");
    }

    // -- the settings window ---------------------------------------------------------------------

    [AvaloniaFact]
    public void Settings_blocks_have_equal_gaps_left_and_right()
    {
        var w = MakeSettings();
        double width = w.ClientSize.Width;
        var blocks = Shown<Control>(w).Where(c =>
            (c is Border b && Has(b, "card")) || (c is ListBox l && (Has(l, "segmented") || Has(l, "choices"))) || (c is TextBox t && Has(t, "field"))).ToList();
        Assert.NotEmpty(blocks);

        Near(22, blocks.Min(b => Box(b, w).Left), "the leftmost block");
        Near(22, width - blocks.Max(b => Box(b, w).Right), "the rightmost block's right gap");
        foreach (var b in blocks)
        {
            var box = Box(b, w);
            Assert.True(box.Left >= 21.25 && box.Right <= width - 21.25, $"{b.GetType().Name} sticks out: {box.Left:0.#}–{box.Right:0.#}");
            if (box.Width > (width - 44) * 0.6) { Near(22, box.Left, "a wide block's left gap"); Near(22, width - box.Right, "a wide block's right gap"); }
        }
        foreach (var card in Shown<Border>(w).Where(b => Has(b, "card"))) CardInsetsMatch(card, w, "a card in settings");

        // The two half-width controls side by side are mirror images.
        var pair = blocks.OfType<ListBox>().Where(l => Has(l, "segmented") && Box(l, w).Width < (width - 44) * 0.6).OrderBy(l => Box(l, w).Left).ToList();
        Assert.Equal(2, pair.Count);
        Near(Box(pair[0], w).Width, Box(pair[1], w).Width, "the two half-width controls' widths");

        foreach (var label in Shown<TextBlock>(w).Where(t => Has(t, "eyebrow")))
            Near(22, Box(label, w).Left, $"{label.Text}'s left gap");
        var title = Shown<TextBlock>(w).First(t => t.Text == "Settings");
        Assert.InRange(Box(title, w).Left, 21.5, 23.5);
    }

    [AvaloniaFact]
    public void Settings_key_editor_field_is_as_wide_as_everything_else()
    {
        var w = MakeSettings();
        ((SettingsViewModel)w.DataContext!).ChangeKeyCommand.Execute(null);
        UiTests.Settle();
        double width = w.ClientSize.Width;
        var field = Shown<TextBox>(w).First(t => Has(t, "field") && t.Watermark?.ToString()?.StartsWith("sk-ant") == true);
        var card = Shown<Border>(w).First(b => Has(b, "card") && field.GetVisualAncestors().Contains(b));
        var box = Box(field, w);
        var cardBox = Box(card, w);
        Near(cardBox.Left + 17, box.Left, "the key field's left gap inside its card");
        Near(cardBox.Right - 17, box.Right, "the key field's right gap inside its card");
        var eye = Shown<Button>(card).First(b => Has(b, "icon"));
        Assert.True(Box(eye, w).Right <= box.Right && Box(eye, w).Left >= box.Left, "the show/hide button is inside the field");
        Near(Box(eye, w).Center.Y, box.Center.Y, "the show/hide button is centred in the field", 1.5);
    }

    // -- the welcome screen ----------------------------------------------------------------------

    [AvaloniaFact]
    public void Welcome_screen_is_centred_and_even()
    {
        var entry = new KeyEntryViewModel();
        var w = new OnboardingWindow { DataContext = entry };
        w.Show();
        UiTests.Settle();
        double width = w.ClientSize.Width, height = w.ClientSize.Height;

        var field = Shown<TextBox>(w).First(t => Has(t, "field"));
        var go = Shown<Button>(w).First(b => Has(b, "primary"));
        foreach (var c in new Control[] { field, go })
        {
            Near(52, Box(c, w).Left, $"{c.GetType().Name}'s left gap");
            Near(52, width - Box(c, w).Right, $"{c.GetType().Name}'s right gap");
        }
        var eye = Shown<Button>(w).First(b => Has(b, "icon"));
        Assert.True(Box(eye, w).Right <= Box(field, w).Right, "the show/hide button is inside the field");

        // The same space above the first thing and below the last.
        var top = Shown<Control>(w).Where(c => c is Border { Width: 64 } || c is TextBlock).Min(c => Box(c, w).Top);
        var bottom = Shown<TextBlock>(w).Max(t => Box(t, w).Bottom);
        Near(top, height - bottom, "the space above and below the welcome content", 3);

        // A disabled Continue button is still readable.
        var label = Shown<TextBlock>(go).First();
        Assert.NotEqual(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)label.Foreground!).Color);
        w.Close();
    }

    // -- the smallest windows, and every display scale ----------------------------------------------

    [AvaloniaTheory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(2.5)]
    public void Nothing_runs_off_the_edge_in_the_smallest_main_window(double scale)
    {
        using var rig = FullMain();
        rig.Engine.AddTurn(Speaker.Them, "https://example.com/a/very/long/link/with/no/spaces/in/it/at/all/so/it/cannot/wrap/nicely?x=1234567890");
        rig.Vm.SetProblem("Couldn't set up speech recognition: an unusually long message that has to wrap onto several lines to fit.", canRetry: true);
        rig.Window.Width = rig.Window.MinWidth;
        rig.Window.Height = rig.Window.MinHeight;
        SetScale(rig.Window, scale);
        AllInside(rig.Window, $"main window at {scale * 100:0}%");
        Save(rig.Window, $"main-min-{scale * 100:0}");
    }

    [AvaloniaTheory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(2.5)]
    public void Nothing_runs_off_the_edge_in_the_smallest_settings_window(double scale)
    {
        var w = MakeSettings();
        ((SettingsViewModel)w.DataContext!).ChangeKeyCommand.Execute(null);
        w.Width = w.MinWidth;
        w.Height = w.MinHeight;
        SetScale(w, scale);
        AllInside(w, $"settings at {scale * 100:0}%");
        Save(w, $"settings-min-{scale * 100:0}");
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(2.5)]
    public void Welcome_screen_fits_at_every_scale_even_on_a_short_screen(double scale)
    {
        var entry = new KeyEntryViewModel();
        var w = new OnboardingWindow { DataContext = entry, Height = 420 }; // a short screen: it must scroll, not clip
        w.Show();
        SetScale(w, scale);
        entry.Key = "sk-ant-api03-whatever";
        UiTests.Settle();
        AllInside(w, $"welcome at {scale * 100:0}%");
        var scroller = Shown<ScrollViewer>(w).First();
        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height || scroller.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Save(w, $"welcome-short-{scale * 100:0}");
        w.Close();
    }

    // -- pictures for a person to look at ----------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)]
    public void Pictures_at_each_scale_in_both_themes(double scale)
    {
        using var rig = FullMain();
        SetScale(rig.Window, scale);
        foreach (var (variant, tag) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            UiTests.Theme(variant);
            Save(rig.Window, $"main-{scale * 100:0}-{tag}");
        }
        UiTests.Theme(ThemeVariant.Light);

        var settings = MakeSettings();
        SetScale(settings, scale);
        Save(settings, $"settings-{scale * 100:0}-light");
        settings.Close();

        var welcome = new OnboardingWindow { DataContext = new KeyEntryViewModel() };
        welcome.Show();
        SetScale(welcome, scale);
        Save(welcome, $"welcome-{scale * 100:0}-light");
        welcome.Close();
    }
}
