using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App;

/// <summary>
/// Centralized UI text and game-term localization table (English / Türkçe).
/// Strings stay indexed by English key so English defaults and existing callers read naturally.
/// </summary>
public static partial class Loc
{
    static readonly Lazy<Dictionary<string, string>> Table = new(() =>
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("FM27Live.Turkish")!;
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });
    static readonly Lazy<Dictionary<string, string>> Insensitive = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Table.Value) map.TryAdd(key, value);
        return map;
    });
    public static bool IsTr => LanguageSetting.IsTurkish;
    public static CultureInfo FormatCulture => IsTr ? CultureInfo.GetCultureInfo("tr-TR") : CultureInfo.InvariantCulture;
    static string Lookup(string english) => !IsTr ? english : TranslationOverrides.Current.TryGetValue(english, out var custom) ? custom : Insensitive.Value.TryGetValue(english, out var value) ? value : english;
    public static string S(string english) => LiveText.Remember(new string(Lookup(english).AsSpan()), () => Lookup(english));
    internal static string RoleLabel(string key, string englishLabel)
    {
        string Render() => IsTr ? Lookup(key) : englishLabel;
        return LiveText.Remember(new string(Render().AsSpan()), Render);
    }
    public static string T(string english, string turkish) => LiveText.Remember(new string((IsTr ? turkish : english).AsSpan()), () => IsTr ? turkish : english);
    static string Context(string context, string english, string fallback)
    {
        string Render() => !IsTr ? english : TranslationOverrides.Current.GetValueOrDefault("@" + context + ":" + english, fallback);
        return LiveText.Remember(new string(Render().AsSpan()), Render);
    }
    public static string Attribute(string english) => english == "Balance" ? Context("attribute", english, "Denge") : S(english);
    public static string KitColour(string english) => english switch
    {
        "Foreground" => Context("kit", english, "Ana renk"),
        "Background" => Context("kit", english, "Arka plan rengi"),
        _ => S(english),
    };
    public static string ClubField(string english) => english == "Training" ? Context("club", english, "Antrenman tesisleri") : S(english);
    public static string F(string englishTemplate, params object?[] args)
    {
        string Render() => string.Format(FormatCulture, Lookup(englishTemplate), args.Select(LiveText.CurrentArgument).ToArray());
        return LiveText.Remember(Render(), Render);
    }
    public static IReadOnlyDictionary<string, string> TurkishTable => Table.Value;
    internal static IReadOnlyDictionary<string, string> EditableTurkishTable => new Dictionary<string, string>(Table.Value)
    {
        ["@attribute:Balance"] = "Denge", ["@kit:Foreground"] = "Ana renk", ["@kit:Background"] = "Arka plan rengi", ["@club:Training"] = "Antrenman tesisleri",
    };

    /// <summary>Keeps a control property synced with language changes while attached to the visual tree.</summary>
    public static T Bind<T>(T control, Action apply) where T : Visual
    {
        apply();
        void Refresh() => LiveText.OnUi(apply);
        control.AttachedToVisualTree += (_, _) => { apply(); LanguageSetting.Changed += Refresh; TranslationOverrides.Changed += Refresh; };
        control.DetachedFromVisualTree += (_, _) => { LanguageSetting.Changed -= Refresh; TranslationOverrides.Changed -= Refresh; };
        return control;
    }

    public static TextBlock BindText(TextBlock tb, string key) => Bind(tb, () => tb.Text = S(key));
    public static Button BindContent(Button btn, string key) => Bind(btn, () => btn.Content = S(key));
    public static T BindTip<T>(T ctl, string key) where T : Control => Bind(ctl, () =>
    {
        ToolTip.SetTip(ctl, S(key));
        Avalonia.Automation.AutomationProperties.SetName(ctl, S(key));
    });
    public static T BindToolTip<T>(T ctl, string key) where T : Control => BindTip(ctl, key);
    public static TextBox BindWatermark(TextBox tb, string key) => Bind(tb, () => tb.Watermark = S(key));
}
