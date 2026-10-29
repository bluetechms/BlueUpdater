using System.Security.Cryptography;
using BlueRelease;
using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

public class ReleaseToolTests
{
    private static string MakePublishDir(TempDir d)
    {
        var src = d.Sub("publish");
        Directory.CreateDirectory(Path.Combine(src, "lib"));
        // A real PE with a version resource (1.0.0.0).
        File.Copy(typeof(ReleaseToolTests).Assembly.Location, Path.Combine(src, "App.exe"));
        File.WriteAllText(Path.Combine(src, "lib", "Native.dll"), "native");
        File.WriteAllText(Path.Combine(src, "appsettings.json"), "{\"ApiKey\":\"must-not-ship\"}");
        File.WriteAllText(Path.Combine(src, "appsettings.Development.json"), "{}");
        File.WriteAllText(Path.Combine(src, "App.pdb"), "symbols");
        return src;
    }

    [Fact]
    public void Packed_release_passes_exactly_the_checks_a_client_makes_and_leaves_site_files_out()
    {
        using var d = new TempDir();
        var (priv, pub, pubB64) = ReleaseTool.KeyGen(d.Sub("keys"), "test", "correct horse battery");
        using var key = ReleaseTool.LoadPrivateKey(priv, "correct horse battery");

        var r = ReleaseTool.Pack("printing-outlet", "1.0.0.0", MakePublishDir(d), "App.exe", key, d.Sub("out"));

        Assert.Equal(new[] { "App.exe", "lib/Native.dll" }, r.Manifest.Files.Select(f => f.Path).ToArray());
        ReleaseTool.Verify(d.Sub("out"), pubB64, "printing-outlet");
        Assert.Equal(File.ReadAllText(pub).Trim(), pubB64);
    }

    [Fact]
    public void Release_whose_exe_reports_another_version_is_refused()
    {
        using var d = new TempDir();
        var (priv, _, _) = ReleaseTool.KeyGen(d.Sub("keys"), "test", "correct horse battery");
        using var key = ReleaseTool.LoadPrivateKey(priv, "correct horse battery");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ReleaseTool.Pack("printing-outlet", "1.1.0.0", MakePublishDir(d), "App.exe", key, d.Sub("out")));
        Assert.Contains("reports version 1.0.0.0", ex.Message);
    }

    [Fact]
    public void Wrong_passphrase_cannot_open_the_key()
    {
        using var d = new TempDir();
        var (priv, _, _) = ReleaseTool.KeyGen(d.Sub("keys"), "test", "correct horse battery");
        Assert.ThrowsAny<CryptographicException>(() => ReleaseTool.LoadPrivateKey(priv, "wrong passphrase!!"));
    }

    [Fact]
    public void Keygen_never_overwrites_an_existing_key_and_rejects_weak_passphrases()
    {
        using var d = new TempDir();
        ReleaseTool.KeyGen(d.Sub("keys"), "test", "correct horse battery");
        Assert.Throws<IOException>(() => ReleaseTool.KeyGen(d.Sub("keys"), "test", "another long passphrase"));
        Assert.Throws<ArgumentException>(() => ReleaseTool.KeyGen(d.Sub("keys"), "other", "short"));
    }

    [Fact]
    public void Release_signed_by_a_different_key_fails_verification()
    {
        using var d = new TempDir();
        var (priv, _, _) = ReleaseTool.KeyGen(d.Sub("keys"), "a", "correct horse battery");
        var (_, _, otherPub) = ReleaseTool.KeyGen(d.Sub("keys"), "b", "correct horse battery");
        using var key = ReleaseTool.LoadPrivateKey(priv, "correct horse battery");
        ReleaseTool.Pack("printing-outlet", "1.0.0.0", MakePublishDir(d), "App.exe", key, d.Sub("out"));
        Assert.Throws<UpdateRejectedException>(() => ReleaseTool.Verify(d.Sub("out"), otherPub, "printing-outlet"));
    }

    [Fact]
    public void Exclude_patterns_drop_files()
    {
        using var d = new TempDir();
        var (priv, _, _) = ReleaseTool.KeyGen(d.Sub("keys"), "test", "correct horse battery");
        using var key = ReleaseTool.LoadPrivateKey(priv, "correct horse battery");
        var r = ReleaseTool.Pack("printing-outlet", "1.0.0.0", MakePublishDir(d), "App.exe", key, d.Sub("out"), excludes: new[] { "lib/*" });
        Assert.Equal(new[] { "App.exe" }, r.Manifest.Files.Select(f => f.Path).ToArray());
    }
}
