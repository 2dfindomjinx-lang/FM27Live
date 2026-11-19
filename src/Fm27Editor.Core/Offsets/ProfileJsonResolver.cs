using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fm27Editor.Core.Offsets;

/// <summary>
/// Resolves compact build profiles (<c>"extends": "layouts/macos.json"</c>) and symbolic references
/// (<c>"@club.finance.vtable"</c>) into a concrete JSON profile tree. Legacy standalone profiles without
/// <c>extends</c> or <c>@</c> references pass through unchanged.
/// </summary>
public static class ProfileJsonResolver
{
    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip };
    static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    const int MaxExtendsDepth = 8;
    const int MaxReferenceDepth = 8;

    public static JsonObject Resolve(string json, Func<string, string?>? resolveLayout = null) =>
        Resolve(json, resolveLayout, out _);

    public static JsonObject Resolve(string json, Func<string, string?>? resolveLayout, out string? extendsName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = ResolveTree(json, resolveLayout, visited, depth: 0, out extendsName);
        ExpandReferences(root);
        return root;
    }

    public static string ResolveToString(string json, Func<string, string?>? resolveLayout = null) =>
        Resolve(json, resolveLayout).ToJsonString();

    public static Func<string, string?> DirectoryLayoutResolver(string baseDirectory) => name =>
    {
        foreach (var candidate in CandidatePaths(baseDirectory, name))
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        return null;
    };

    static JsonObject ResolveTree(string json, Func<string, string?>? resolveLayout,
        HashSet<string> visited, int depth, out string? extendsName)
    {
        if (depth > MaxExtendsDepth)
            throw new FormatException($"Profile 'extends' nesting exceeded {MaxExtendsDepth} levels");
        var node = JsonNode.Parse(json, NodeOptions, DocOptions) as JsonObject
            ?? throw new FormatException("Profile JSON root must be an object");
        extendsName = node["extends"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(extendsName))
        {
            node.Remove("extends");
            return node;
        }
        string normalized = NormalizeLayoutKey(extendsName);
        if (!visited.Add(normalized))
            throw new FormatException($"Cyclic 'extends' reference involving '{extendsName}'");

        string baseJson = LoadLayoutJson(extendsName, resolveLayout)
            ?? throw new FormatException($"Base layout '{extendsName}' could not be found");
        var merged = ResolveTree(baseJson, resolveLayout, visited, depth + 1, out _);
        node.Remove("extends");
        DeepMerge(merged, node);
        return merged;
    }

    static void DeepMerge(JsonObject target, JsonObject overlay)
    {
        foreach (var kv in overlay.ToList())
        {
            if (kv.Value is null)
            {
                target.Remove(kv.Key);
                continue;
            }
            if (kv.Value is JsonObject overlayObj && target[kv.Key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, overlayObj);
                continue;
            }
            target[kv.Key] = kv.Value.DeepClone();
        }
    }

    static void ExpandReferences(JsonObject root)
    {
        WalkObject(root, isRoot: true);

        void WalkObject(JsonObject obj, bool isRoot)
        {
            foreach (var kv in obj.ToList())
            {
                if (isRoot && kv.Key is "vtableClasses" or "rootSpecs" or "expectedEmpty") continue;
                if (kv.Value is JsonValue scalar && TryGetReference(scalar, out string refPath))
                    obj[kv.Key] = ResolveReference(root, refPath, 0);
                else if (kv.Value is JsonObject child)
                    WalkObject(child, isRoot: false);
                else if (kv.Value is JsonArray arr)
                    WalkArray(arr);
            }
        }

        void WalkArray(JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item is JsonValue scalar && TryGetReference(scalar, out string refPath))
                    arr[i] = ResolveReference(root, refPath, 0);
                else if (item is JsonObject child)
                    WalkObject(child, isRoot: false);
                else if (item is JsonArray inner)
                    WalkArray(inner);
            }
        }
    }

    static bool TryGetReference(JsonValue scalar, out string path)
    {
        if (scalar.GetValueKind() == JsonValueKind.String && scalar.GetValue<string>() is { } s && s.StartsWith('@'))
        {
            path = s[1..];
            return path.Length > 0;
        }
        path = "";
        return false;
    }

    static JsonNode ResolveReference(JsonObject root, string path, int depth)
    {
        if (depth > MaxReferenceDepth)
            throw new FormatException($"Cyclic symbolic profile reference '@{path}'");
        var target = GetByPath(root, path)
            ?? throw new FormatException($"Symbolic reference '@{path}' points to missing path '{path}'");
        if (target is JsonValue scalar && TryGetReference(scalar, out string nextPath))
            return ResolveReference(root, nextPath, depth + 1);
        return target.DeepClone();
    }

    public static JsonNode? GetByPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.'))
        {
            if (current is null) return null;
            int bracket = part.IndexOf('[');
            if (bracket < 0)
            {
                current = (current as JsonObject)?[part];
                continue;
            }
            string key = part[..bracket];
            current = (current as JsonObject)?[key];
            string rest = part[bracket..];
            while (rest.Length > 0 && current is JsonArray arr)
            {
                if (!rest.StartsWith('[')) return null;
                int close = rest.IndexOf(']');
                if (close < 0 || !int.TryParse(rest[1..close], out int index) || index < 0 || index >= arr.Count)
                    return null;
                current = arr[index];
                rest = rest[(close + 1)..];
            }
        }
        return current;
    }

    public static void SetByPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonNode current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;
            int bracket = part.IndexOf('[');
            if (bracket < 0)
            {
                var obj = (JsonObject)current;
                if (last) { obj[part] = value; return; }
                if (obj[part] is not JsonObject next)
                {
                    next = new JsonObject();
                    obj[part] = next;
                }
                current = next;
                continue;
            }
            string key = part[..bracket];
            int index = int.Parse(part[(bracket + 1)..^1]);
            var parentObj = (JsonObject)current;
            if (parentObj[key] is not JsonArray arr)
            {
                arr = new JsonArray();
                parentObj[key] = arr;
            }
            while (arr.Count <= index) arr.Add(null);
            if (last) { arr[index] = value; return; }
            if (arr[index] is not JsonObject elem)
            {
                elem = new JsonObject();
                arr[index] = elem;
            }
            current = elem;
        }
    }

    static string NormalizeLayoutKey(string name)
    {
        string s = name.Replace('\\', '/').Trim();
        if (s.StartsWith("offsets/", StringComparison.OrdinalIgnoreCase)) s = s["offsets/".Length..];
        if (!s.StartsWith("layouts/", StringComparison.OrdinalIgnoreCase)) s = "layouts/" + s;
        if (!s.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) s += ".json";
        return s.ToLowerInvariant();
    }

    static IEnumerable<string> CandidatePaths(string dir, string name)
    {
        string clean = name.Replace('\\', '/').Trim();
        string file = Path.GetFileName(clean);
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) file += ".json";
        yield return Path.Combine(dir, clean);
        if (!clean.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(dir, clean + ".json");
        yield return Path.Combine(dir, "layouts", file);
        yield return Path.Combine(dir, "..", "layouts", file);
    }

    public static string? LoadLayoutJson(string name, Func<string, string?>? resolveLayout = null)
    {
        if (resolveLayout?.Invoke(name) is { } fromDisk) return fromDisk;
        string normalized = "offsets/" + NormalizeLayoutKey(name);
        var asm = typeof(ProfileJsonResolver).Assembly;
        string? resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return null;
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
