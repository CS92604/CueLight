using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuelight.App.ViewModels;
using Cuelight.App.Views;
using Cuelight.Core;

namespace Cuelight.App.Tests;

public class KeyEntryProviderTests
{
    private static KeyEntryViewModel Entry(Func<string, Task<(bool, string)>>? validate = null, Provider provider = Provider.Claude)
    {
        var entry = new KeyEntryViewModel(validate ?? (_ => Task.FromResult((true, ""))));
        entry.Provider = provider;
        return entry;
    }

    [Fact]
    public void The_providers_are_listed_in_the_order_of_the_enum_and_the_index_picks_one()
    {
        Assert.Equal(new[] { "Claude", "ChatGPT", "Gemini", "Grok", "Other" }, KeyEntryViewModel.ProviderItems.Select(i => i.Label));
        var entry = Entry();
        entry.ProviderIndex = 3;
        Assert.Equal(Provider.Grok, entry.Provider);
        entry.ProviderIndex = 99;                               // out of range: ignored
        entry.ProviderIndex = -1;
        Assert.Equal(Provider.Grok, entry.Provider);
        Assert.Equal(3, entry.ProviderIndex);
    }

    [Fact]
    public void Each_provider_changes_the_labels_the_hint_and_the_help_text()
    {
        var entry = Entry();
        var seen = new List<string>();
        entry.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        entry.Provider = Provider.OpenAi;
        Assert.Equal("ChatGPT API key", entry.KeyLabel);
        Assert.Equal("sk-…", entry.KeyHint);
        Assert.Equal("Get a key at platform.openai.com", entry.GetKeyLabel);
        Assert.True(entry.HasKeyPage);
        Assert.False(entry.ShowAddress);
        Assert.Contains("only ever sent to OpenAI", entry.PrivacyText);
        Assert.Contains("OpenAI bills API use separately from a ChatGPT subscription", entry.BillingText);
        Assert.Contains("Check the key with OpenAI", entry.ContinueTip);
        Assert.Contains(nameof(KeyEntryViewModel.KeyLabel), seen);
        Assert.Contains(nameof(KeyEntryViewModel.PrivacyText), seen);

        entry.Provider = Provider.Gemini;
        Assert.Equal("Get a key at aistudio.google.com", entry.GetKeyLabel);
        Assert.Contains("only ever sent to Google", entry.PrivacyText);

        entry.Provider = Provider.Grok;
        Assert.Equal("Get a key at console.x.ai", entry.GetKeyLabel);

        entry.Provider = Provider.Other;
        Assert.Equal("API key (optional)", entry.KeyLabel);
        Assert.True(entry.ShowAddress);
        Assert.False(entry.HasKeyPage);
        Assert.Contains("the service you enter", entry.PrivacyText);

        entry.Provider = Provider.Claude;
        Assert.Contains("Anthropic bills API use separately from a Claude subscription", entry.BillingText);
    }

    [Fact]
    public async Task A_key_that_belongs_to_another_provider_is_pointed_out_before_anything_is_sent()
    {
        int checks = 0;
        var entry = Entry(_ => { checks++; return Task.FromResult((true, "")); }, Provider.OpenAi);
        string? accepted = null;
        entry.Accepted = k => accepted = k;

        entry.Key = "xai-abc123";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("looks like a Grok key", entry.Error);
        Assert.Contains("Choose Grok above", entry.Error);

        entry.Key = "sk-ant-api03-abc";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("looks like a Claude key", entry.Error);

        entry.Provider = Provider.Gemini;
        Assert.Equal("", entry.Error);                          // choosing another provider starts clean
        entry.Key = "sk-proj-abc";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("looks like a ChatGPT key", entry.Error);

        Assert.Equal(0, checks);
        Assert.Null(accepted);
    }

    [Fact]
    public async Task A_key_for_the_chosen_provider_is_checked_and_handed_back()
    {
        string? checkedKey = null, accepted = null;
        var entry = Entry(k => { checkedKey = k; return Task.FromResult((true, "")); }, Provider.Gemini);
        entry.Accepted = k => accepted = k;
        entry.Key = "  AIzaSyExample123  ";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("AIzaSyExample123", checkedKey);
        Assert.Equal("AIzaSyExample123", accepted);

        var bad = Entry(_ => Task.FromResult((false, "Gemini didn't accept that key. Check it and try again.")), Provider.Gemini);
        string? never = null;
        bad.Accepted = k => never = k;
        bad.Key = "AIzaSyNope";
        await bad.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("didn't accept", bad.Error);
        Assert.Null(never);
    }

    [Fact]
    public async Task Another_service_needs_an_address_and_a_model_but_not_necessarily_a_key()
    {
        string? checkedKey = null, accepted = null;
        var entry = Entry(k => { checkedKey = k; return Task.FromResult((true, "")); }, Provider.Other);
        entry.Accepted = k => accepted = k;

        Assert.False(entry.SubmitCommand.CanExecute(null));      // nothing entered yet
        entry.BaseUrl = "http://localhost:11434/v1";
        Assert.True(entry.SubmitCommand.CanExecute(null));       // an address is enough to try

        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("name of the model", entry.Error);
        Assert.Null(accepted);

        entry.ModelId = "llama3.2";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("", entry.Error);
        Assert.Equal(ProviderKeys.NoKey, checkedKey);            // no key typed: the stand-in is what is checked and saved
        Assert.Equal(ProviderKeys.NoKey, accepted);

        entry.Key = "sk-or-v1-abc";                              // a key typed for such a service is kept as typed, whatever it looks like
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("sk-or-v1-abc", accepted);

        entry.BaseUrl = "";
        entry.Key = "";
        Assert.False(entry.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Claude_still_wants_a_Claude_key()
    {
        var entry = Entry();
        entry.Key = "sk-proj-looks-like-chatgpt";
        await entry.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("Keys start with “sk-ant-”", entry.Error);
    }
}

public class SettingsProviderTests
{
    private static (SettingsViewModel Vm, Settings Settings, Func<int> Changes) Make(Dictionary<Provider, string?>? saved = null, Settings? settings = null)
    {
        settings ??= new Settings();
        saved ??= new() { [Provider.Claude] = "sk-ant-api03-abcdef1234" };
        int changes = 0;
        var vm = new SettingsViewModel(settings, () => changes++, new KeyEntryViewModel(_ => Task.FromResult((true, ""))),
            saved.GetValueOrDefault(settings.Provider), _ => { }, p => saved.GetValueOrDefault(p));
        return (vm, settings, () => changes);
    }

    [Fact]
    public void Switching_provider_changes_the_models_the_key_and_remembers_each_choice()
    {
        var (vm, s, changes) = Make();
        Assert.Equal(0, vm.ProviderIndex);
        Assert.Equal(Models.All, vm.ModelItems);
        Assert.False(vm.ShowModelId);
        Assert.Equal("sk-ant-…1234", vm.MaskedKey);
        vm.SelectedModel = Models.All[0];                           // Opus

        vm.ProviderIndex = (int)Provider.OpenAi;
        Assert.Equal(Provider.OpenAi, s.Provider);
        Assert.Equal(Provider.OpenAi, vm.KeyEntry.Provider);
        Assert.Equal(new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6-luna" }, vm.ModelItems.Select(m => m.Id));
        Assert.Equal("gpt-6.1-sol", vm.SelectedModel!.Id);          // that provider's default
        Assert.Equal("No key saved", vm.MaskedKey);
        Assert.False(vm.HasKey);
        Assert.True(vm.IsEditingKey, "a provider with no key yet asks for one");
        Assert.Equal("CHATGPT API KEY", vm.KeyEyebrow);
        Assert.Equal("CHATGPT MODEL", vm.ModelEyebrow);
        Assert.True(vm.ShowModelList);
        Assert.True(vm.ShowModelId);
        Assert.False(vm.ShowAddress);
        Assert.True(changes() >= 1);

        vm.SelectedModel = vm.ModelItems[2];                        // Luna
        vm.ProviderIndex = (int)Provider.Claude;
        Assert.Equal("claude-opus-5-5", vm.SelectedModel!.Id);      // the old choice is back
        Assert.Equal("sk-ant-…1234", vm.MaskedKey);
        Assert.False(vm.IsEditingKey);
        Assert.True(vm.HasKey);
        vm.ProviderIndex = (int)Provider.OpenAi;
        Assert.Equal("gpt-6-luna", vm.SelectedModel!.Id);
    }

    [Fact]
    public void Switching_to_a_provider_that_already_has_a_key_shows_it_masked()
    {
        var (vm, _, _) = Make(new() { [Provider.Claude] = "sk-ant-api03-abcdef1234", [Provider.Grok] = "xai-abcdefgh5678" });
        vm.ProviderIndex = (int)Provider.Grok;
        Assert.Equal("xai-abc…5678", vm.MaskedKey);
        Assert.False(vm.IsEditingKey);
        Assert.Contains("console.x.ai", vm.KeyTip);
        Assert.Contains("xAI", vm.KeyTip);
    }

    [Fact]
    public void Any_model_name_can_be_typed_except_for_Claude_and_the_list_follows_it()
    {
        var (vm, s, _) = Make();
        vm.ProviderIndex = (int)Provider.Gemini;
        vm.ModelId = "  gemini-9-ultra ";
        Assert.Equal("gemini-9-ultra", s.Model);
        Assert.Null(vm.SelectedModel);                              // not one of the listed shortcuts
        Assert.Equal("gemini-9-ultra", vm.KeyEntry.ModelId);

        vm.SelectedModel = vm.ModelItems.First(m => m.Id == "gemini-3.8-flash");
        Assert.Equal("gemini-3.8-flash", s.Model);
        Assert.Equal("gemini-3.8-flash", vm.ModelId);               // picking a shortcut fills the box
    }

    [Fact]
    public void Another_service_has_an_address_and_a_typed_model_and_no_list()
    {
        var (vm, s, _) = Make();
        vm.ProviderIndex = (int)Provider.Other;
        Assert.False(vm.ShowModelList);
        Assert.True(vm.ShowModelId);
        Assert.True(vm.ShowAddress);
        Assert.Equal("MODEL", vm.ModelEyebrow);
        Assert.Equal("API KEY (OPTIONAL)", vm.KeyEyebrow);

        vm.BaseUrl = " http://localhost:11434/v1 ";
        vm.ModelId = "llama3.2";
        Assert.Equal("http://localhost:11434/v1", s.BaseUrl);
        Assert.Equal("llama3.2", s.Model);
        Assert.Equal("http://localhost:11434/v1", vm.KeyEntry.BaseUrl);   // so a key can be checked against that service
        Assert.Equal("llama3.2", vm.KeyEntry.ModelId);
    }

    [Fact]
    public void A_key_accepted_here_is_reported_and_shown_masked_and_a_removed_one_is_forgotten()
    {
        var reported = new List<string?>();
        var settings = new Settings();
        var entry = new KeyEntryViewModel(_ => Task.FromResult((true, "")));
        var vm = new SettingsViewModel(settings, () => { }, entry, null, reported.Add);
        vm.ProviderIndex = (int)Provider.Grok;
        Assert.True(vm.IsEditingKey);
        entry.Key = "xai-abcdefgh9999";
        entry.SubmitCommand.Execute(null);
        Assert.Equal("xai-abc…9999", vm.MaskedKey);
        Assert.False(vm.IsEditingKey);
        Assert.Equal(new string?[] { "xai-abcdefgh9999" }, reported);

        vm.RemoveKeyCommand.Execute(null);
        Assert.Equal("No key saved", vm.MaskedKey);
        Assert.Equal(new string?[] { "xai-abcdefgh9999", null }, reported);
    }

    [Fact]
    public void A_service_that_needs_no_key_says_so()
    {
        var (vm, _, _) = Make(new() { [Provider.Claude] = "sk-ant-api03-abcdef1234", [Provider.Other] = ProviderKeys.NoKey });
        vm.ProviderIndex = (int)Provider.Other;
        Assert.Equal("No key needed", vm.MaskedKey);
        Assert.True(vm.HasKey);
    }

    [Fact]
    public void The_cost_counter_counts_tokens_when_no_price_is_known_and_the_tip_names_the_provider()
    {
        UsageSnapshot Usage(decimal cost, bool unpriced) => new(3, 12_000, 0, 30_000, 400, cost, unpriced);
        Assert.Equal("42.4k tokens", MainViewModel.CostLabel(Usage(0m, true)));
        Assert.Equal("≈ $0.05+", MainViewModel.CostLabel(Usage(0.05m, true)));      // part priced, part not
        Assert.Equal("1.2M tokens", MainViewModel.CostLabel(new UsageSnapshot(9, 1_000_000, 0, 200_000, 3, 0m, true)));
        Assert.Equal("350 tokens", MainViewModel.CostLabel(new UsageSnapshot(1, 300, 0, 0, 50, 0m, true)));

        var tip = MainViewModel.CostTipFor(Usage(0.05m, false), Providers.Get(Provider.OpenAi));
        Assert.Contains("ChatGPT's suggestions", tip);
        Assert.Contains("OpenAI's list prices", tip);
        Assert.Contains("platform.openai.com", tip);
        Assert.DoesNotContain("Anthropic", tip);

        var unknown = MainViewModel.CostTipFor(Usage(0m, true), Providers.Get(Provider.Other));
        Assert.Contains("the AI's suggestions", unknown);
        Assert.Contains("counted in tokens only", unknown);
        Assert.Contains("provider's own dashboard", unknown);

        Assert.Contains("Anthropic's list prices", MainViewModel.CostTipFor(Usage(0.05m, false)));   // unchanged for Claude
    }
}

public class ProviderScreenTests
{
    private static IEnumerable<T> Visible<T>(Visual root) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible);

    private static string? TipFor(Control c)
    {
        for (Visual? v = c; v is not null; v = v.GetVisualParent())
            if (v is Control k && ToolTip.GetTip(k) is string t && t.Length > 0) return t;
        return null;
    }

    [AvaloniaFact]
    public void The_welcome_screen_offers_every_provider_and_shows_the_service_fields_only_for_Other()
    {
        var entry = new KeyEntryViewModel(_ => Task.FromResult((true, "")));
        var win = new OnboardingWindow { DataContext = entry };
        win.Show();
        UiTests.Settle();

        var chips = win.GetVisualDescendants().OfType<ListBox>().First(l => l.Classes.Contains("segmented"));
        Assert.Equal(5, chips.ItemCount);
        var labels = win.GetVisualDescendants().OfType<ListBoxItem>().Select(i => i.DataContext).OfType<ChoiceItem>().Select(c => c.Label).ToList();
        Assert.Equal(new[] { "Claude", "ChatGPT", "Gemini", "Grok", "Other" }, labels);
        Assert.Equal(0, chips.SelectedIndex);
        Assert.Equal(5, win.GetVisualDescendants().OfType<ListBoxItem>().Count(i => !string.IsNullOrWhiteSpace(TipFor(i))));

        Assert.Single(Visible<TextBox>(win));                       // only the key box
        Assert.Contains(Visible<Button>(win), b => b.Content as string == "Get a key at console.anthropic.com");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "Claude API key");
        UiTests.Shot(win, "welcome-claude-light");

        entry.ProviderIndex = (int)Provider.Grok;
        UiTests.Settle();
        Assert.Contains(Visible<Button>(win), b => b.Content as string == "Get a key at console.x.ai");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "Grok API key");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text?.Contains("only ever sent to xAI") == true);
        Assert.Equal(3, chips.SelectedIndex);

        entry.ProviderIndex = (int)Provider.Other;
        UiTests.Settle();
        Assert.Equal(3, Visible<TextBox>(win).Count());             // address, model name and the optional key
        Assert.DoesNotContain(Visible<Button>(win), b => (b.Content as string)?.StartsWith("Get a key") == true);
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "API key (optional)");
        foreach (var box in Visible<TextBox>(win))
            Assert.False(string.IsNullOrWhiteSpace(TipFor(box)), "a text field has no hover text");
        UiTests.Shot(win, "welcome-other-light");
        win.Close();
    }

    [AvaloniaFact]
    public void The_settings_window_shows_the_provider_the_models_and_the_service_fields_that_belong_to_it()
    {
        var settings = new Settings();
        var vm = new SettingsViewModel(settings, () => { }, new KeyEntryViewModel(_ => Task.FromResult((true, ""))),
            "sk-ant-api03-abcdef1234", _ => { }, p => p == Provider.Claude ? "sk-ant-api03-abcdef1234" : null);
        var win = new SettingsWindow { DataContext = vm, Width = 480, Height = 760 };
        win.Show();
        UiTests.Settle();

        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "AI PROVIDER");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "CLAUDE API KEY");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "CLAUDE MODEL");
        Assert.DoesNotContain(Visible<TextBox>(win), t => t.Watermark == "Or type any model name");

        vm.ProviderIndex = (int)Provider.OpenAi;
        UiTests.Settle();
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "CHATGPT API KEY");
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "GPT-6.1 Sol");
        Assert.Contains(Visible<TextBox>(win), t => t.Watermark == "Or type any model name");
        Assert.Contains(Visible<TextBox>(win), t => t.Watermark == "sk-…");                 // the key box, since there is no key yet
        UiTests.Shot(win, "settings-chatgpt-light");

        vm.ProviderIndex = (int)Provider.Other;
        UiTests.Settle();
        Assert.Contains(Visible<TextBlock>(win), t => t.Text == "Service address");
        Assert.DoesNotContain(Visible<TextBlock>(win), t => t.Text == "GPT-6.1 Sol");
        foreach (var box in Visible<TextBox>(win))
            Assert.False(string.IsNullOrWhiteSpace(TipFor(box)), "a text field has no hover text");
        foreach (var item in Visible<ListBoxItem>(win))
            Assert.False(string.IsNullOrWhiteSpace(TipFor(item)), $"option '{item.DataContext}' has no hover text");
        win.Close();
    }
}
