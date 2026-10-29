using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueUpdater;

public sealed class ManifestFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ManifestPackage
{
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

// The exact signed document: {product, version, files:[{path,sha256,size}], package:{sha256,size},
// minUpdater, issuedAt, expiresAt}. The server relays the signed bytes untouched.
public sealed class Manifest
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("files")] public List<ManifestFile> Files { get; set; } = new();
    [JsonPropertyName("package")] public ManifestPackage? Package { get; set; }
    [JsonPropertyName("minUpdater")] public string? MinUpdater { get; set; }
    [JsonPropertyName("issuedAt")] public DateTime IssuedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; set; }
}

public sealed class VerifiedManifest
{
    public required Manifest Manifest { get; init; }
    public required string RawJson { get; init; }
    public required string SignatureBase64 { get; init; }
}

public sealed class UpdateRejectedException : Exception
{
    public UpdateRejectedException(string message) : base(message) { }
}

public sealed class ManifestVerifier
{
    private readonly List<ECDsa> _keys;
    private readonly string _updaterVersion;
    private readonly Func<DateTime> _utcNow;

    public ManifestVerifier(IEnumerable<ECDsa> keys, string updaterVersion, Func<DateTime>? utcNow = null)
    {
        _keys = keys.ToList();
        _updaterVersion = updaterVersion;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // Public keys compiled into this build (pubkeys.txt, embedded). None configured = nothing verifies.
    public static List<ECDsa> LoadEmbeddedKeys()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pubkeys.txt");
        if (s == null) return new List<ECDsa>();
        using var r = new StreamReader(s);
        return ParseKeys(r.ReadToEnd());
    }

    public static List<ECDsa> ParseKeys(string text)
    {
        var keys = new List<ECDsa>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(line), out _);
            keys.Add(key);
        }
        return keys;
    }

    // Order matters: signature first (nothing in the document is trusted before that), then identity,
    // then replay/downgrade protection.
    public VerifiedManifest Verify(string rawJson, string signatureBase64, string expectedProduct, string installedVersion)
    {
        if (_keys.Count == 0) throw new UpdateRejectedException("no signing keys are compiled into this updater");

        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64); }
        catch { throw new UpdateRejectedException("signature is not valid base64"); }
        if (sig.Length != 64) throw new UpdateRejectedException("signature has the wrong length");

        var data = Encoding.UTF8.GetBytes(rawJson);
        var ok = false;
        foreach (var k in _keys)
        {
            try
            {
                if (k.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    ok = true;
                    break;
                }
            }
            catch (CryptographicException) { }
        }
        if (!ok) throw new UpdateRejectedException("manifest signature does not verify");

        Manifest m;
        try
        {
            m = JsonSerializer.Deserialize<Manifest>(rawJson) ?? throw new InvalidDataException();
        }
        catch
        {
            throw new UpdateRejectedException("manifest is not valid JSON");
        }

        if (!string.Equals(m.Product, expectedProduct, StringComparison.Ordinal))
            throw new UpdateRejectedException($"manifest is for product '{m.Product}', expected '{expectedProduct}'");
        if (!VersionUtil.IsValid(m.Version))
            throw new UpdateRejectedException("manifest version is malformed");
        if (VersionUtil.Compare(m.Version, installedVersion) <= 0)
            throw new UpdateRejectedException($"manifest version {m.Version} is not newer than installed {installedVersion}");
        if (m.ExpiresAt.ToUniversalTime() < _utcNow())
            throw new UpdateRejectedException("manifest has expired");
        if (!string.IsNullOrEmpty(m.MinUpdater) && VersionUtil.Compare(m.MinUpdater, _updaterVersion) > 0)
            throw new UpdateRejectedException($"needs updater {m.MinUpdater} or newer (this is {_updaterVersion})");
        if (m.Package == null || m.Package.Size <= 0 || !IsHex64(m.Package.Sha256))
            throw new UpdateRejectedException("manifest has no usable package hash");
        if (m.Files.Count == 0)
            throw new UpdateRejectedException("manifest lists no files");
        foreach (var f in m.Files)
            if (f.Size < 0 || !IsHex64(f.Sha256))
                throw new UpdateRejectedException($"manifest entry '{f.Path}' has a bad hash or size");

        return new VerifiedManifest { Manifest = m, RawJson = rawJson, SignatureBase64 = signatureBase64 };
    }

    private static bool IsHex64(string? s) =>
        s != null && s.Length == 64 && s.All(Uri.IsHexDigit);
}
