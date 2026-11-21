using Avalonia.Controls;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class TranslationOverrideTests : IDisposable
{
    readonly string previous = TranslationOverrides.PathOverride!;
    readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    public TranslationOverrideTests() { TranslationOverrides.PathOverride = path; TranslationOverrides.Load(); }
    public void Dispose()
    {
        AvaloniaTestThread.Run(() => { TranslationOverrides.Save(new Dictionary<string, string>()); LanguageSetting.Set(AppLanguage.English); });
        File.Delete(path); TranslationOverrides.PathOverride = previous;
    }

    [Fact]
    public void Save_updates_live_labels_but_never_edits_or_merges_distinct_source_keys()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.Turkish);
            var first = new TextBlock { Text = Loc.S("Finishing") };
            var second = new TextBlock { Text = Loc.S("Passing") };
            var input = new TextBox { Text = "17" };
            TranslationOverrides.Save(new Dictionary<string, string> { ["Finishing"] = "Özel ad", ["Passing"] = "Özel ad" });
            Assert.Equal("Özel ad", first.Text); Assert.Equal("Özel ad", second.Text);
            LanguageSetting.Set(AppLanguage.English);
            Assert.Equal("Finishing", first.Text); Assert.Equal("Passing", second.Text);
            LanguageSetting.Set(AppLanguage.Turkish);
            TranslationOverrides.Save(new Dictionary<string, string> { ["Finishing"] = "Son vuruş", ["Passing"] = "Pas verme" });
            Assert.Equal("Son vuruş", first.Text); Assert.Equal("Pas verme", second.Text);
            Assert.Equal("17", input.Text);
            TranslationOverrides.Load();
            Assert.Equal("Son vuruş", Loc.S("Finishing"));
        });
    }

    [Fact]
    public void Invalid_file_keeps_previous_good_translations_and_reports_error()
    {
        TranslationOverrides.Save(new Dictionary<string, string> { ["Finishing"] = "Son vuruş" });
        File.WriteAllText(path, "{broken");
        TranslationOverrides.Load();
        Assert.NotNull(TranslationOverrides.LoadError);
        Assert.Equal("Son vuruş", TranslationOverrides.Current["Finishing"]);
    }

    [Fact]
    public void Missing_format_arguments_are_rejected_before_the_file_changes()
    {
        TranslationOverrides.Save(new Dictionary<string, string> { ["Reloaded {0}"] = "Yenilendi: {0}" });
        string before = File.ReadAllText(path);
        Assert.Throws<FormatException>(() => TranslationOverrides.Save(new Dictionary<string, string> { ["Reloaded {0}"] = "Yenilendi" }));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Throws<FormatException>(() => TranslationOverrides.Validate(new Dictionary<string, string> { ["{0}"] = "{1}" }));
    }

    [Fact]
    public void Attribute_balance_can_be_renamed_without_changing_finances()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.Turkish);
            TranslationOverrides.Save(new Dictionary<string, string> { ["@attribute:Balance"] = "Vücut dengesi" });
            Assert.Equal("Vücut dengesi", Loc.Attribute("Balance"));
            Assert.Equal("Bakiye", Loc.S("Balance"));
        });
    }
}
