using System.Text.Json;

namespace Fm27Editor.App.Live;

public sealed class LivePreferences
{
    public bool AutomaticUpdates { get; set; } = true;
    public double OverlayWidth { get; set; } = 1100;
    public double OverlayHeight { get; set; } = 720;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public double OverlayOpacity { get; set; } = 0.97;
    static string FilePath => Path.Combine(LiveFeed.DataRoot, "live-settings.json");
    public static LivePreferences Load()
    {
        try { return JsonSerializer.Deserialize<LivePreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(LiveFeed.DataRoot);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
