using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class LocalizationTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), "fm27-lang-test-" + Guid.NewGuid().ToString("N") + ".txt");

    public LocalizationTests()
    {
        LanguageSetting.SettingPathOverride = file;
        LanguageSetting.Set(AppLanguage.English);
    }

    public void Dispose()
    {
        LanguageSetting.Set(AppLanguage.English);
        LanguageSetting.SettingPathOverride = Path.Combine(Path.GetTempPath(), "fm27-app-tests-lang-" + Environment.ProcessId + ".txt");
        if (File.Exists(file)) File.Delete(file);
    }

    [Fact]
    public void Defaults_to_english_and_switches_to_turkish_for_strings_formatting_and_pairs()
    {
        Assert.False(Loc.IsTr);
        Assert.Equal("Players", Loc.S("Players"));
        Assert.Equal("Nothing changed (3 checked)", Loc.F("Nothing changed ({0:N0} checked)", 3));
        Assert.Equal("Save", Loc.T("Save", "Kaydet"));

        LanguageSetting.Set(AppLanguage.Turkish);
        Assert.True(Loc.IsTr);
        Assert.Equal("Oyuncular", Loc.S("Players"));
        Assert.Equal("3 kayıt kontrol edildi, değişiklik yapılmadı", Loc.F("Nothing changed ({0:N0} checked)", 3));
        Assert.Equal(" — 25 oyuncudan 20 tanesi gösteriliyor (5 gizli)",
            Loc.F(" — {0:N0} of {1:N0} players shown ({2:N0} hidden)", 20, 25, 5));
        Assert.Equal("Kaydet", Loc.T("Save", "Kaydet"));

        LanguageSetting.Set(AppLanguage.English);
        Assert.False(Loc.IsTr);
        Assert.Equal("Players", Loc.S("Players"));
    }

    [Fact]
    public void Language_setting_persists_choice_and_defaults_to_english_when_missing()
    {
        LanguageSetting.Set(AppLanguage.Turkish);
        Assert.Equal("tr", File.ReadAllText(file).Trim());

        LanguageSetting.Load();
        Assert.Equal(AppLanguage.Turkish, LanguageSetting.Current);
        Assert.Equal("Türkçe", LanguageSetting.DisplayName(AppLanguage.Turkish));

        File.Delete(file);
        LanguageSetting.Load();
        Assert.Equal(AppLanguage.English, LanguageSetting.Current);
        Assert.Equal("English", LanguageSetting.DisplayName(AppLanguage.English));
    }

    [Fact]
    public void Turkish_table_entries_are_populated_without_blank_translations_and_include_retire_and_bulk_keys()
    {
        Assert.True(Loc.TurkishTable.Count > 2000);
        foreach (var (key, value) in Loc.TurkishTable)
        {
            Assert.False(string.IsNullOrWhiteSpace(key), $"Blank localization key found (value='{value}')");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Blank Turkish translation for '{key}'");
        }

        string[] requiredKeys =
        [
            "Retiring",
            "Search retiring…",
            "Prevent retirement",
            "Actions ({0:N0} selected)",
            "Mass Edit…",
            "Language",
            "Save changes",
        ];
        foreach (var key in requiredKeys)
            Assert.True(Loc.TurkishTable.ContainsKey(key), $"Missing Turkish key: {key}");
    }
}
