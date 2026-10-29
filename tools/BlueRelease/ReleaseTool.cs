using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueUpdater;

namespace BlueRelease;

public sealed record PackResult(string ZipPath, string ManifestPath, string SignaturePath, Manifest Manifest);

public static class ReleaseTool
{
    private static readonly JsonSerializerOptions ManifestJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Generates a P-256 signing key. The private half is written encrypted (PKCS#8, AES-256, PBKDF2-SHA256);
    // the public half is the one-line base64 SPKI that goes into pubkeys.txt and CLIENT_UPDATE_PUBKEYS.
    public static (string privatePath, string publicPath, string publicBase64) KeyGen(string outDir, string name, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 12)
            throw new ArgumentException("use a passphrase of at least 12 characters");
        Directory.CreateDirectory(outDir);
        var privatePath = Path.Combine(outDir, name + ".private.pk8.b64");
        var publicPath = Path.Combine(outDir, name + ".public.b64");
        if (File.Exists(privatePath) || File.Exists(publicPath))
            throw new IOException($"{name} already exists in {outDir}; refusing to overwrite a signing key");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enc = key.ExportEncryptedPkcs8PrivateKey(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(privatePath, Convert.ToBase64String(enc));
        File.WriteAllText(publicPath, pub);
        return (privatePath, publicPath, pub);
    }

    public static ECDsa LoadPrivateKey(string path, string passphrase)
    {
        var key = ECDsa.Create();
        key.ImportEncryptedPkcs8PrivateKey(passphrase, Convert.FromBase64String(File.ReadAllText(path).Trim()), out _);
        return key;
    }

    public static string Sign(ECDsa key, string json) => Convert.ToBase64String(
        key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    // Builds <out>/package.zip, manifest.json and manifest.sig from a publish folder.
    public static PackResult Pack(string product, string version, string srcDir, string exeRelative, ECDsa key,
        string outDir, int expiresDays = 400, string? minUpdater = null, IEnumerable<string>? excludes = null)
    {
        if (!VersionUtil.IsValid(version)) throw new ArgumentException($"'{version}' is not a valid version (e.g. 1.1.0.0)");
        if (!Directory.Exists(srcDir)) throw new DirectoryNotFoundException(srcDir);

        // The updater decides "is a newer build installed" from the exe's own file version. If the release
        // claims 1.1.0.0 but the exe says 1.0.0.0, every client would re-download it forever.
        var exe = Path.Combine(srcDir, exeRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(exe)) throw new FileNotFoundException("exe not found in the publish folder", exe);
        var exeVersion = VersionUtil.Normalize(FileVersionInfo.GetVersionInfo(exe).FileVersion);
        if (VersionUtil.Compare(exeVersion, version) != 0)
            throw new InvalidOperationException($"{exeRelative} reports version {exeVersion ?? "none"} but the release is {version}. Rebuild with -p:Version={version}.");

        var skip = (excludes ?? Array.Empty<string>()).ToList();
        var files = new List<(string rel, string full)>();
        var skipped = new List<string>();
        foreach (var full in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, full).Replace('\\', '/');
            if (PackageStager.IsPreserved(rel) || rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || skip.Any(p => Wild(p, rel)))
            {
                skipped.Add(rel);
                continue;
            }
            PackageStager.ValidateRelativePath(rel);
            files.Add((rel, full));
        }
        if (files.Count == 0) throw new InvalidOperationException("nothing to package");
        files.Sort((a, b) => string.CompareOrdinal(a.rel, b.rel));

        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, "package.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var manifestFiles = new List<ManifestFile>();
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (rel, full) in files)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                using var src = File.OpenRead(full);
                using var dst = entry.Open();
                using var sha = SHA256.Create();
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    dst.Write(buf, 0, n);
                    total += n;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                manifestFiles.Add(new ManifestFile { Path = rel, Sha256 = Convert.ToHexString(sha.Hash!).ToLowerInvariant(), Size = total });
            }
        }

        var zipInfo = new FileInfo(zipPath);
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); // whole seconds: every JSON parser agrees
        var manifest = new Manifest
        {
            Product = product,
            Version = version,
            Files = manifestFiles,
            Package = new ManifestPackage { Sha256 = PackageStager.Sha256OfFile(zipPath), Size = zipInfo.Length },
            MinUpdater = minUpdater,
            IssuedAt = now,
            ExpiresAt = now.AddDays(expiresDays)
        };
        var json = JsonSerializer.Serialize(manifest, ManifestJson);
        var manifestPath = Path.Combine(outDir, "manifest.json");
        var sigPath = Path.Combine(outDir, "manifest.sig");
        // Exact bytes, no BOM, no trailing newline: the signature is over precisely this string.
        File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
        File.WriteAllText(sigPath, Sign(key, json));
        if (skipped.Count > 0) Console.WriteLine("Left out: " + string.Join(", ", skipped));
        return new PackResult(zipPath, manifestPath, sigPath, manifest);
    }

    // Does what a client does: signature, product, extraction rules, hashes. Proves the output is installable.
    public static void Verify(string dir, string publicKeyBase64, string product, string installedVersion = "0.0.0.0")
    {
        var verifier = new ManifestVerifier(ManifestVerifier.ParseKeys(publicKeyBase64), "9999.0.0.0");
        var vm = verifier.Verify(File.ReadAllText(Path.Combine(dir, "manifest.json")),
            File.ReadAllText(Path.Combine(dir, "manifest.sig")).Trim(), product, installedVersion);
        var zip = Path.Combine(dir, "package.zip");
        PackageStager.VerifyPackageFile(zip, vm.Manifest.Package!);
        var tmp = Path.Combine(Path.GetTempPath(), "blue-release-verify-" + Guid.NewGuid().ToString("N"));
        try { PackageStager.Extract(zip, vm.Manifest, tmp); }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    public static async Task<string> UploadAsync(string apiBase, string token, string dir, string product, string? canary, string? notes)
    {
        var manifest = File.ReadAllText(Path.Combine(dir, "manifest.json"));
        var m = JsonSerializer.Deserialize<Manifest>(manifest)!;
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + "/clientupdate/release");
        req.Headers.Add("auth", token);
        // The api needs the text fields BEFORE the file part, so add them in this order.
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(product), "product");
        form.Add(new StringContent(m.Version), "version");
        form.Add(new StringContent(manifest, new UTF8Encoding(false)), "manifest");
        form.Add(new StringContent(File.ReadAllText(Path.Combine(dir, "manifest.sig")).Trim()), "signature");
        if (!string.IsNullOrEmpty(canary)) form.Add(new StringContent(canary), "canary");
        if (!string.IsNullOrEmpty(notes)) form.Add(new StringContent(notes), "notes");
        var file = new StreamContent(File.OpenRead(Path.Combine(dir, "package.zip")));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", $"{product}-{m.Version}.zip");
        req.Content = form;
        using var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}: {body}");
        return body;
    }

    public static async Task<string> SetStateAsync(string apiBase, string token, int id, bool? active, bool? blocked, string? canary)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + $"/clientupdate/release/{id}/state");
        req.Headers.Add("auth", token);
        var body = new Dictionary<string, object?>();
        if (active != null) body["active"] = active;
        if (blocked != null) body["blocked"] = blocked;
        if (canary != null) body["canary"] = canary;
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}: {text}");
        return text;
    }

    public static async Task<string> ListAsync(string apiBase, string token, string what)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, apiBase.TrimEnd('/') + "/clientupdate/" + what);
        req.Headers.Add("auth", token);
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)res.StatusCode}: {text}");
        return text;
    }

    // '*' matches within a path segment, '**' across folders.
    private static bool Wild(string pattern, string path)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(path, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
