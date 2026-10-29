using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BlueUpdater;

// Turns a downloaded zip into a verified, extracted staging folder. Nothing in the zip is trusted: only
// entries named in the signed manifest are extracted, each is size-bounded while extracting, and the
// result is re-hashed against the manifest.
public static class PackageStager
{
    // Files that belong to the site (settings, logo, state, logs, the NSSM binary). A package that lists
    // one is rejected outright rather than silently skipped: it means the release was built wrongly.
    private static readonly Regex[] Preserved =
    {
        new(@"^appsettings.*\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(logo.*|.*\.(png|jpg|jpeg|ico))$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^nssm\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(lastSync|lastRecord)\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(settings|plans)\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.(log|state|tmp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static readonly Regex SafeSegment = new(@"^[A-Za-z0-9_\-\.\(\)\+ ]+$", RegexOptions.Compiled);
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsPreserved(string relativePath) =>
        Preserved.Any(r => r.IsMatch(System.IO.Path.GetFileName(relativePath)));

    // Relative path with '/' separators, or throws. Rejects absolute paths, drive letters, "..", empty or
    // odd segments, alternate data streams and reserved device names.
    public static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240) throw new UpdateRejectedException("empty or overlong file path");
        if (path.Contains('\\') || path.StartsWith('/') || path.Contains(':'))
            throw new UpdateRejectedException($"unsafe file path '{path}'");
        foreach (var s in path.Split('/'))
        {
            if (s.Length == 0 || s == "." || s == ".." || s.EndsWith('.') || s.EndsWith(' ')
                || !SafeSegment.IsMatch(s) || Reserved.IsMatch(s))
                throw new UpdateRejectedException($"unsafe file path '{path}'");
        }
        return path;
    }

    public static string Sha256OfFile(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    // Checks the zip file itself against the manifest's package hash/size.
    public static void VerifyPackageFile(string zipPath, ManifestPackage package)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists || info.Length != package.Size)
            throw new UpdateRejectedException("downloaded package size does not match the manifest");
        if (!string.Equals(Sha256OfFile(zipPath), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateRejectedException("downloaded package hash does not match the manifest");
    }

    // Re-checks an already extracted folder (used when a staged release is picked up after a restart).
    public static void VerifyExtracted(Manifest manifest, string filesDir)
    {
        foreach (var f in manifest.Files)
        {
            var p = System.IO.Path.Combine(filesDir, ValidateRelativePath(f.Path).Replace('/', System.IO.Path.DirectorySeparatorChar));
            var info = new FileInfo(p);
            if (!info.Exists || info.Length != f.Size || !string.Equals(Sha256OfFile(p), f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRejectedException($"staged file '{f.Path}' is missing or altered");
        }
    }

    // Extracts into `destDir`. On any failure the folder is removed.
    public static void Extract(string zipPath, Manifest manifest, string destDir)
    {
        var declared = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            var p = ValidateRelativePath(f.Path);
            if (IsPreserved(p)) throw new UpdateRejectedException($"manifest lists a site-owned file '{p}'");
            if (!declared.TryAdd(p, f)) throw new UpdateRejectedException($"manifest lists '{p}' twice");
        }

        var destFull = System.IO.Path.GetFullPath(destDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destDir);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue; // directory entry
                var rel = ValidateRelativePath(entry.FullName);
                if (!declared.TryGetValue(rel, out var want))
                    throw new UpdateRejectedException($"package contains '{rel}', which the manifest does not list");
                if (!seen.Add(rel)) throw new UpdateRejectedException($"package contains '{rel}' twice");
                if (entry.Length != want.Size) throw new UpdateRejectedException($"'{rel}' has the wrong size");

                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateRejectedException($"'{rel}' escapes the staging folder");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);

                // Stop at the declared size even if the entry header lies (zip bomb).
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > want.Size) throw new UpdateRejectedException($"'{rel}' is larger than declared");
                        output.Write(buf, 0, n);
                    }
                    if (total != want.Size) throw new UpdateRejectedException($"'{rel}' is smaller than declared");
                }

                if (!string.Equals(Sha256OfFile(target), want.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateRejectedException($"'{rel}' hash does not match the manifest");
            }

            var missing = declared.Keys.FirstOrDefault(k => !seen.Contains(k));
            if (missing != null) throw new UpdateRejectedException($"package is missing '{missing}'");
        }
        catch
        {
            try { Directory.Delete(destDir, true); } catch { /* best effort */ }
            throw;
        }
    }
}
