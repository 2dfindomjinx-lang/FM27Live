using Avalonia.Controls;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class LiveLanguageTests
{
    [Fact]
    public void Background_language_change_updates_controls_on_the_creating_thread()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.English);
            var label = new TextBlock { Text = Loc.S("Settings") };
            int ownerThread = Environment.CurrentManagedThreadId, changedThread = 0;
            label.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) changedThread = Environment.CurrentManagedThreadId; };
            try
            {
                Task.Run(() => LanguageSetting.Set(AppLanguage.Turkish)).GetAwaiter().GetResult();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal("Ayarlar", label.Text);
                Assert.Equal(ownerThread, changedThread);
            }
            finally { LanguageSetting.Set(AppLanguage.English); }
        });
    }

    [Fact]
    public void Late_realized_labels_use_the_current_language_even_with_cached_items()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.English);
            var cached = Loc.S("All Clubs");
            LanguageSetting.Set(AppLanguage.Turkish);
            try
            {
                var label = new TextBlock { Text = cached };
                Assert.Equal("Tüm kulüpler", label.Text);
                LanguageSetting.Set(AppLanguage.English);
                Assert.Equal("All Clubs", label.Text);
            }
            finally { LanguageSetting.Set(AppLanguage.English); }
        });
    }

    [Fact]
    public void Language_switch_updates_existing_and_detached_labels_without_editing_user_values()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.English);
            var label = new TextBlock { Text = Loc.S("Wage Budget") };
            var input = new TextBox { Text = "999999", Watermark = Loc.S("Search") };
            var name = new TextBlock { Text = new string("Club".ToCharArray()) };
            var save = new Button { Content = Loc.S("Save changes"), IsEnabled = true };
            var football = new TextBlock { Text = Loc.Attribute("Balance") };
            var finance = new TextBlock { Text = Loc.S("Balance") };
            var formatted = new TextBlock { Text = Loc.F("{0}: {1}", Loc.S("Club"), "Beşiktaş") };
            int edits = 0;
            input.TextChanged += (_, _) => edits++;
            try
            {
                LanguageSetting.Set(AppLanguage.Turkish);
                Assert.Equal("Maaş bütçesi", label.Text);
                Assert.Equal("Değişiklikleri kaydet", save.Content);
                Assert.Equal("Ara", input.Watermark);
                Assert.Equal("Denge", football.Text);
                Assert.Equal("Bakiye", finance.Text);
                Assert.Equal("Kulüp: Beşiktaş", formatted.Text);
                Assert.Equal("999999", input.Text);
                Assert.Equal("Club", name.Text);
                Assert.True(save.IsEnabled);
                Assert.Equal(0, edits);
                LanguageSetting.Set(AppLanguage.English);
                Assert.Equal("Wage Budget", label.Text);
                Assert.Equal("Save changes", save.Content);
                Assert.Equal("Balance", football.Text);
                Assert.Equal("Balance", finance.Text);
                Assert.Equal("Club: Beşiktaş", formatted.Text);
                Assert.Equal("999999", input.Text);
                Assert.Equal(0, edits);
            }
            finally { LanguageSetting.Set(AppLanguage.English); }
        });
    }

    [Fact]
    public void Legacy_Vietnamese_preference_migrates_to_Turkish()
    {
        var previous = LanguageSetting.SettingPathOverride;
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(file, "vi");
            LanguageSetting.SettingPathOverride = file;
            LanguageSetting.Load();
            Assert.Equal(AppLanguage.Turkish, LanguageSetting.Current);
            Assert.Equal(new[] { "en", "tr" }, Enum.GetValues<AppLanguage>().Select(LanguageSetting.CodeOf));
        }
        finally { LanguageSetting.SettingPathOverride = previous; File.Delete(file); LanguageSetting.Set(AppLanguage.English); }
    }
}
