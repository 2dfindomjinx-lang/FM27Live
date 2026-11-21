using System.Text;
using System.Text.Json;

namespace Fm27Editor.App;

/// <summary>User-owned translations live outside the executable and survive application updates.</summary>
internal static class TranslationOverrides
{
    public static string? PathOverride { get; set; }
    public static string FilePath => PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FM27Live", "translations", "tr.custom.json");
    static IReadOnlyDictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyDictionary<string, string> Current => current;
    public static event Action? Changed;
    public static string? LoadError { get; private set; }

    public static void Load()
    {
        try
        {
            var next = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? throw new FormatException("Translation file is empty")
                : new Dictionary<string, string>();
            current = Validate(next);
            LoadError = null;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { LoadError = ex.Message; }
    }

    public static Dictionary<string, string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) throw new FormatException("Translation keys and values cannot be empty");
            string template = key.StartsWith("@", StringComparison.Ordinal) && key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
            // Plain strings may contain literal braces; formatted messages must keep their argument slots.
            if (System.Text.RegularExpressions.Regex.IsMatch(template, @"\{\d"))
            {
                var source = CompositeFormat.Parse(template);
                var target = CompositeFormat.Parse(value);
                if (source.MinimumArgumentCount != target.MinimumArgumentCount) throw new FormatException($"Keep the {{0}}, {{1}} argument slots in: {key}");
            }
            if (!next.TryAdd(key, value)) throw new FormatException($"Duplicate translation key: {key}");
        }
        return next;
    }

    public static void Save(IReadOnlyDictionary<string, string> values)
    {
        var next = Validate(values);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, FilePath, true);
        current = next;
        LoadError = null;
        Changed?.Invoke();
    }
}
