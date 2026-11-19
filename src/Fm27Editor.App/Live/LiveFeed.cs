using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fm27Editor.App.Live;

public sealed record SignedEnvelope(string Payload, string Signature);
public sealed record LiveManifest(int Schema, string Product, string Version, string Url, long Size, string Sha256, string Notes);
public sealed record StagedUpdate(string Folder, LiveManifest Manifest);

/// <summary>The pinned release key authenticates metadata as well as the executable hash.</summary>
public static class LiveFeed
{
    public const string Repository = "2dfindomjinx-lang/FM27Live";
    public const string Product = "FM27Live";
    public const string AssetName = "FM27Live.exe";
    public const string FeedUrl = "https://github.com/" + Repository + "/releases/latest/download/update.json";
    public const long MaximumSize = 400L * 1024 * 1024;
    public static string DataRoot => Environment.GetEnvironmentVariable("FM27LIVE_DATA_DIR") is { Length: > 0 } testRoot
        ? Path.GetFullPath(testRoot) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
    public static string StageRoot => Path.Combine(DataRoot, "updates");
    public static Version Current => UpdateCheck.CurrentVersion() ?? new Version(0, 0, 0);
    public static string PublicKey => FileKey.Value;
    static readonly Lazy<string> FileKey = new(() =>
    {
        using var stream = typeof(LiveFeed).Assembly.GetManifestResourceStream("FM27Live.UpdatePublicKey")
            ?? throw new InvalidDataException("Güncelleme doğrulama anahtarı bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static LiveManifest Verify(string envelopeJson, string publicKey)
    {
        if (envelopeJson.Length > 64 * 1024) throw new InvalidDataException("Güncelleme bildirimi çok büyük.");
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeJson) ?? throw new InvalidDataException("Geçersiz bildirim.");
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        if (!key.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Güncellemenin yayıncı imzası doğrulanamadı.");
        var manifest = JsonSerializer.Deserialize<LiveManifest>(payload) ?? throw new InvalidDataException("Geçersiz paket.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(LiveManifest m)
    {
        if (m.Schema != 1 || m.Product != Product || !Version.TryParse(m.Version, out var version) ||
            version.Build < 0 || version.Revision >= 0 || version <= new Version(0, 0, 0))
            throw new InvalidDataException("Paket sürümü veya ürün kimliği geçersiz.");
        string expected = $"https://github.com/{Repository}/releases/download/v{m.Version}/{AssetName}";
        if (m.Url != expected || m.Size < 2 || m.Size > MaximumSize || m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Paket adresi, boyutu veya özeti geçersiz.");
    }

    public static async Task VerifyFileAsync(string file, LiveManifest manifest, CancellationToken ct = default)
    {
        if (new FileInfo(file).Length != manifest.Size) throw new InvalidDataException("İndirilen dosyanın boyutu eşleşmiyor.");
        await using var stream = File.OpenRead(file);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("İndirilen dosya doğrulanamadı.");
        stream.Position = 0;
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("Paket bir Windows uygulaması değil.");
    }

    public static async Task<StagedUpdate?> CheckAndStageAsync(Version current, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FM27Live/" + current);
        using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await http.GetAsync(FeedUrl, HttpCompletionOption.ResponseHeadersRead, checkTimeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        string json = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, 64 * 1024, checkTimeout.Token));
        var m = Verify(json, PublicKey);
        if (Version.Parse(m.Version) <= current || IsFailed(m.Version)) return null;
        string folder = Path.Combine(StageRoot, m.Version);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, AssetName);
        if (File.Exists(target))
        {
            try { await VerifyFileAsync(target, m, ct); await File.WriteAllTextAsync(Path.Combine(folder, "update.json"), json, ct); return new(folder, m); }
            catch (InvalidDataException) { }
        }
        string partial = target + ".partial";
        try
        {
            using var download = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            download.EnsureSuccessStatusCode();
            if (download.Content.Headers.ContentLength is { } size && size != m.Size) throw new InvalidDataException("Paket boyutu eşleşmiyor.");
            await using (var input = await download.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(partial))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > m.Size) throw new InvalidDataException("Paket beklenenden büyük.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await VerifyFileAsync(partial, m, ct);
            File.Move(partial, target, true);
            await File.WriteAllTextAsync(Path.Combine(folder, "update.json"), json, ct);
            return new(folder, m);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + read > limit) throw new InvalidDataException("Bildirim çok büyük.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    public static bool IsFailed(string version) => File.Exists(Path.Combine(StageRoot, version, "failed.txt"));

    public static async Task<StagedUpdate?> ReadStagedAsync(Version current)
    {
        if (!Directory.Exists(StageRoot)) return null;
        foreach (var dir in Directory.GetDirectories(StageRoot).OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version()))
        {
            try
            {
                var m = Verify(await File.ReadAllTextAsync(Path.Combine(dir, "update.json")), PublicKey);
                if (Version.Parse(m.Version) <= current || IsFailed(m.Version) || Path.GetFileName(dir) != m.Version) continue;
                await VerifyFileAsync(Path.Combine(dir, AssetName), m);
                return new(dir, m);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or CryptographicException) { }
        }
        return null;
    }
}
