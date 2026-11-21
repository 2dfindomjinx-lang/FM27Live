namespace Fm27Editor.Core.Settings;

/// <summary>Display-only preference: English keeps the original order; Turkish follows the game's layout by default.</summary>
public static class AttributeOrderSetting
{
    public static string? SettingPathOverride { get; set; }
    static string SettingPath => SettingPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FM27Live", "attribute-order.txt");
    public static bool UseEnglishOrder { get; private set; }
    public static bool UseTurkishOrder => LanguageSetting.IsTurkish && !UseEnglishOrder;
    public static event Action? Changed;

    public static void Load()
    {
        UseEnglishOrder = false;
        try { UseEnglishOrder = File.Exists(SettingPath) && File.ReadAllText(SettingPath).Trim() == "english"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Set(bool useEnglishOrder)
    {
        UseEnglishOrder = useEnglishOrder;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!);
            File.WriteAllText(SettingPath, useEnglishOrder ? "english" : "language");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
}
