using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.0.0.0", "1.0.0.1", -1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.1", "1.1.0.0", 0)]
    [InlineData("2.0.0.0", "1.99.99.99", 1)]
    public void Compare_is_numeric_and_four_part(string a, string b, int expected) =>
        Assert.Equal(expected, VersionUtil.Compare(a, b));

    [Theory]
    [InlineData("1.1.0.0+abc123", "1.1.0.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v3.4.5-beta", "3.4.5.0")]
    public void Normalize_strips_suffixes(string raw, string expected) => Assert.Equal(expected, VersionUtil.Normalize(raw));

    [Theory]
    [InlineData("1.0.0.0", true)]
    [InlineData("1", false)]
    [InlineData("1.0.0.0.0", false)]
    [InlineData("1.0.x", false)]
    [InlineData("", false)]
    public void IsValid(string v, bool ok) => Assert.Equal(ok, VersionUtil.IsValid(v));
}

public class ManifestVerifierTests
{
    private static readonly Dictionary<string, string> Files = new() { ["App.exe"] = "binary-content" };

    private static (ManifestVerifier v, TestSigner s, TempDir d) Setup(Func<DateTime>? now = null)
    {
        var s = new TestSigner();
        return (new ManifestVerifier(new[] { s.PublicOnly() }, "1.0.0.0", now), s, new TempDir());
    }

    [Fact]
    public void Accepts_a_valid_newer_release()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files);
            var vm = v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0");
            Assert.Equal("1.1.0.0", vm.Manifest.Version);
        }
    }

    [Fact]
    public void Accepts_a_signature_from_the_backup_key()
    {
        var primary = new TestSigner();
        var backup = new TestSigner();
        var v = new ManifestVerifier(new[] { primary.PublicOnly(), backup.PublicOnly() }, "1.0.0.0");
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(backup, d.Path, "printing-outlet", "1.1.0.0", Files);
        v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0");
    }

    [Fact]
    public void Rejects_a_signature_from_an_unknown_key()
    {
        var (v, _, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "printing-outlet", "1.1.0.0", Files);
            Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Rejects_a_manifest_edited_after_signing()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files);
            var tampered = r.ManifestJson.Replace("App.exe", "Evil.exe");
            Assert.Throws<UpdateRejectedException>(() => v.Verify(tampered, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Rejects_when_no_keys_are_compiled_in()
    {
        var s = new TestSigner();
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files);
        var v = new ManifestVerifier(Array.Empty<ECDsa>(), "1.0.0.0");
        Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
    }

    [Fact]
    public void Rejects_a_manifest_for_another_product()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "bluepunch", "1.1.0.0", Files);
            Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Theory]
    [InlineData("1.0.0.0")] // same version (replay)
    [InlineData("0.9.0.0")] // downgrade
    public void Rejects_same_or_older_versions(string version)
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", version, Files);
            Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Rejects_an_expired_manifest()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files, expires: DateTime.UtcNow.AddDays(-1));
            Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Rejects_a_release_that_needs_a_newer_updater()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files, minUpdater: "2.0.0.0");
            Assert.Throws<UpdateRejectedException>(() => v.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Rejects_a_signature_of_the_wrong_length()
    {
        var (v, s, d) = Setup();
        using (d)
        {
            var r = ReleaseBuilder.Build(s, d.Path, "printing-outlet", "1.1.0.0", Files);
            Assert.Throws<UpdateRejectedException>(() =>
                v.Verify(r.ManifestJson, Convert.ToBase64String(new byte[10]), "printing-outlet", "1.0.0.0"));
        }
    }

    [Fact]
    public void Embedded_key_file_parses_comments_and_keys()
    {
        var s = new TestSigner();
        var text = "# comment\n\n" + Convert.ToBase64String(s.Key.ExportSubjectPublicKeyInfo()) + "\r\n";
        Assert.Single(ManifestVerifier.ParseKeys(text));
    }
}

public class PackageStagerTests
{
    private static Dictionary<string, string> Files() => new() { ["App.exe"] = "exe-bytes", ["lib/Native.dll"] = "dll-bytes" };

    [Fact]
    public void Extracts_a_good_package_with_nested_folders()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files());
        PackageStager.VerifyPackageFile(r.Zip, r.Manifest.Package!);
        PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out"));
        Assert.Equal("dll-bytes", File.ReadAllText(d.Sub("out", "lib", "Native.dll")));
        PackageStager.VerifyExtracted(r.Manifest, d.Sub("out"));
    }

    [Fact]
    public void Rejects_a_zip_that_differs_from_the_signed_package_hash()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files());
        File.AppendAllText(r.Zip, "x");
        Assert.Throws<UpdateRejectedException>(() => PackageStager.VerifyPackageFile(r.Zip, r.Manifest.Package!));
    }

    [Fact]
    public void Rejects_an_entry_the_manifest_does_not_list()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files(),
            mutateZip: z => { using var s = z.CreateEntry("extra.exe").Open(); s.WriteByte(1); });
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
        Assert.False(Directory.Exists(d.Sub("out")));
    }

    [Fact]
    public void Rejects_a_package_missing_a_listed_file()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files(),
            mutateManifest: m => m.Files.Add(new ManifestFile { Path = "gone.dll", Sha256 = new string('a', 64), Size = 1 }));
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
    }

    [Fact]
    public void Rejects_a_file_whose_content_does_not_match_its_hash()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files(),
            mutateManifest: m => m.Files[0].Sha256 = new string('b', 64));
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("/abs/evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("a\\evil.dll")]
    [InlineData("evil.dll:stream")]
    [InlineData("CON.txt")]
    [InlineData("trailingdot.")]
    [InlineData("a//b.dll")]
    public void Rejects_unsafe_paths(string path)
    {
        Assert.Throws<UpdateRejectedException>(() => PackageStager.ValidateRelativePath(path));
    }

    [Fact]
    public void Zip_slip_entry_is_rejected_even_if_the_manifest_listed_it()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", new() { ["../evil.dll"] = "x" });
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
        Assert.False(File.Exists(d.Sub("evil.dll")));
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    [InlineData("logo.png")]
    [InlineData("nssm.exe")]
    [InlineData("lastSync.txt")]
    [InlineData("sub/settings.json")]
    [InlineData("service.log")]
    public void Site_owned_files_in_a_release_are_rejected(string path)
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", new() { ["App.exe"] = "a", [path] = "mine" });
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
    }

    [Fact]
    public void Entry_that_decompresses_larger_than_declared_is_rejected()
    {
        using var d = new TempDir();
        // Manifest claims 5 bytes; the zip holds 50.
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", new() { ["App.exe"] = new string('x', 50) },
            mutateManifest: m => { m.Files[0].Size = 5; });
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
    }

    [Fact]
    public void Duplicate_manifest_entries_are_rejected()
    {
        using var d = new TempDir();
        var r = ReleaseBuilder.Build(new TestSigner(), d.Path, "p", "1.1.0.0", Files(),
            mutateManifest: m => m.Files.Add(m.Files[0]));
        Assert.Throws<UpdateRejectedException>(() => PackageStager.Extract(r.Zip, r.Manifest, d.Sub("out")));
    }
}
