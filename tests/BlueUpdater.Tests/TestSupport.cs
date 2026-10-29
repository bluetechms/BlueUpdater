using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueUpdater;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlueUpdater.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bu-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

// Signs manifests the way scripts/publish-client-release.ps1 does: ECDSA P-256 / SHA-256, IEEE P1363.
public sealed class TestSigner
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ECDsa PublicOnly()
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _);
        return pub;
    }

    public string Sign(string json) => Convert.ToBase64String(
        Key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

public sealed class Release
{
    public string Zip { get; init; } = "";
    public Manifest Manifest { get; init; } = new();
    public string ManifestJson { get; init; } = "";
    public string Signature { get; init; } = "";
}

public static class ReleaseBuilder
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // files: relative path -> content. mutate lets a test corrupt the manifest or the zip after the fact.
    public static Release Build(TestSigner signer, string dir, string product, string version,
        Dictionary<string, string> files, DateTime? expires = null, string? minUpdater = null,
        Action<Manifest>? mutateManifest = null, Action<ZipArchive>? mutateZip = null, string? signWithOtherJson = null)
    {
        Directory.CreateDirectory(dir);
        var zipPath = System.IO.Path.Combine(dir, $"{product}-{version}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (path, content) in files)
            {
                var e = zip.CreateEntry(path);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
            mutateZip?.Invoke(zip);
        }
        var zipBytes = File.ReadAllBytes(zipPath);
        var m = new Manifest
        {
            Product = product,
            Version = version,
            Files = files.Select(f => new ManifestFile { Path = f.Key, Sha256 = Sha(Encoding.UTF8.GetBytes(f.Value)), Size = Encoding.UTF8.GetByteCount(f.Value) }).ToList(),
            Package = new ManifestPackage { Sha256 = Sha(zipBytes), Size = zipBytes.Length },
            MinUpdater = minUpdater,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAt = expires ?? DateTime.UtcNow.AddDays(30)
        };
        mutateManifest?.Invoke(m);
        var json = JsonSerializer.Serialize(m, Json);
        return new Release { Zip = zipPath, Manifest = m, ManifestJson = json, Signature = signer.Sign(signWithOtherJson ?? json) };
    }
}

public static class Logs
{
    public static ILogger Null => NullLogger.Instance;
}

// Deterministic time: Delay advances the clock instead of waiting.
public sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
    public Task Delay(TimeSpan t, CancellationToken ct)
    {
        Now += t;
        return Task.CompletedTask;
    }
}
