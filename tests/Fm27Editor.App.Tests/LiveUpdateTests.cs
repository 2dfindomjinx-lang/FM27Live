using System.Security.Cryptography;
using System.Text.Json;
using Fm27Editor.App.Live;

namespace Fm27Editor.App.Tests;

public sealed class LiveUpdateTests
{
    static LiveManifest Manifest(byte[] bytes) => new(1, "FM27Live", "1.2.3",
        "https://github.com/2dfindomjinx-lang/FM27Live/releases/download/v1.2.3/FM27Live.exe",
        bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "Test release");
    static string Sign(LiveManifest m, ECDsa key)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(m);
        return JsonSerializer.Serialize(new SignedEnvelope(Convert.ToBase64String(data),
            Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
    }

    [Fact] public void PublisherSignatureAuthenticatesManifest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = Manifest("MZcontent"u8.ToArray());
        Assert.Equal(m, LiveFeed.Verify(Sign(m, key), key.ExportSubjectPublicKeyInfoPem()));
    }
    [Fact] public void AnotherPublishersSignatureIsRejected()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => LiveFeed.Verify(Sign(Manifest("MZtest"u8.ToArray()), impostor), trusted.ExportSubjectPublicKeyInfoPem()));
    }
    [Fact] public void TamperedPayloadIsRejectedEvenWithOriginalSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = Manifest("MZtest"u8.ToArray());
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(Sign(m, key))!;
        envelope = envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(m with { Version = "9.9.9" })) };
        Assert.Throws<InvalidDataException>(() => LiveFeed.Verify(JsonSerializer.Serialize(envelope), key.ExportSubjectPublicKeyInfoPem()));
    }
    [Theory]
    [InlineData("https://evil.example/update.exe")]
    [InlineData("http://github.com/2dfindomjinx-lang/FM27Live/releases/download/v1.2.3/FM27Live.exe")]
    [InlineData("https://github.com/Jackhamerp24/Football-Manager-27-Editor/releases/download/v1.2.3/Fm27Editor.exe")]
    [InlineData("https://github.com/2dfindomjinx-lang/FM27Live/releases/download/v1.2.2/FM27Live.exe")]
    public void WrongOriginOrTagIsRejected(string url) => Assert.Throws<InvalidDataException>(() => LiveFeed.Validate(Manifest("MZ"u8.ToArray()) with { Url = url }));

    [Fact] public async Task CorruptedDownloadIsRejected()
    {
        string file = Path.GetTempFileName();
        try
        {
            var expected = "MZgood"u8.ToArray();
            await File.WriteAllBytesAsync(file, expected);
            await LiveFeed.VerifyFileAsync(file, Manifest(expected));
            await File.WriteAllBytesAsync(file, "MZevil"u8.ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => LiveFeed.VerifyFileAsync(file, Manifest(expected)));
        }
        finally { File.Delete(file); }
    }
    [Fact] public void OversizedOrWrongProductIsRejected()
    {
        var m = Manifest("MZ"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => LiveFeed.Validate(m with { Size = LiveFeed.MaximumSize + 1 }));
        Assert.Throws<InvalidDataException>(() => LiveFeed.Validate(m with { Product = "Other" }));
        Assert.Throws<InvalidDataException>(() => LiveFeed.Validate(m with { Version = "1.2.3-beta" }));
    }
    [Fact] public void AtomicInstallPreservesBackupAndSettings()
    {
        string dir = Directory.CreateTempSubdirectory("fm27live-update-").FullName;
        try
        {
            string target = Path.Combine(dir, "FM27Live.exe"), pending = target + ".pending", backup = target + ".previous";
            File.WriteAllText(target, "old"); File.WriteAllText(pending, "new"); File.WriteAllText(Path.Combine(dir, "settings.json"), "keep");
            LiveInstaller.AtomicReplace(target, pending, backup);
            Assert.Equal("new", File.ReadAllText(target));
            Assert.Equal("old", File.ReadAllText(backup));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "settings.json")));
            Assert.ThrowsAny<IOException>(() => LiveInstaller.AtomicReplace(target, pending, backup));
            Assert.Equal("new", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, true); }
    }
}
