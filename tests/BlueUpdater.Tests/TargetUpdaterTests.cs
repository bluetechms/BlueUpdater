using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

public sealed class FakeApi : IUpdateApi
{
    public Release? Offer { get; set; }
    public bool Offline { get; set; }
    public bool Reachable { get; set; } = true;
    public int ManifestCalls, Downloads;
    public List<ReportBody> Reports { get; } = new();
    public Action<string>? CorruptDownload { get; set; }

    public Task<ManifestResponse?> GetManifestAsync(string product, string currentVersion, string instance, CancellationToken ct)
    {
        ManifestCalls++;
        if (Offline) throw new UpdateApiException("offline");
        if (Offer == null || VersionUtil.Compare(Offer.Manifest.Version, currentVersion) <= 0) return Task.FromResult<ManifestResponse?>(null);
        return Task.FromResult<ManifestResponse?>(new ManifestResponse { Version = Offer.Manifest.Version, Manifest = Offer.ManifestJson, Signature = Offer.Signature });
    }

    public Task DownloadAsync(string product, string version, string instance, string destFile, long expectedSize, CancellationToken ct)
    {
        Downloads++;
        File.Copy(Offer!.Zip, destFile, true);
        CorruptDownload?.Invoke(destFile);
        return Task.CompletedTask;
    }

    public Task ReportAsync(string product, string instance, ReportBody body, CancellationToken ct)
    {
        Reports.Add(body);
        return Task.CompletedTask;
    }

    public Task<bool> ReachableAsync(CancellationToken ct) => Task.FromResult(Reachable);
}

public class TargetUpdaterTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly TestSigner _signer = new();
    private readonly FakeClock _clock = new();
    private readonly Paths _paths;
    private readonly FakeHost _host;
    private readonly FakeApi _api = new();
    private readonly string _install;
    private readonly UpdaterConfig _cfg = new() { HealthStableMinutes = 1, HealthDeadlineMinutes = 15, WindowStartHour = 3, WindowEndHour = 6 };
    private readonly TargetConfig _target = new() { Product = "printing-outlet", Services = { "BluePrinting" } };
    private DateTime _localNow = new(2026, 10, 30, 4, 0, 0); // inside the 03-06 window

    public TargetUpdaterTests()
    {
        _paths = new Paths(_tmp.Sub("data"));
        _paths.EnsureDirs();
        _install = _tmp.Sub("install");
        Directory.CreateDirectory(_install);
        // A real PE with a version resource (this test assembly, 1.0.0.0) so FileVersionInfo has something to read.
        File.Copy(typeof(TargetUpdaterTests).Assembly.Location, Path.Combine(_install, "App.exe"));
        File.WriteAllText(Path.Combine(_install, "appsettings.json"), "{\"ApiKey\":\"site\"}");
        _host = new FakeHost(_paths, _clock);
        _host.Add("BluePrinting", _install);
    }

    public void Dispose() => _tmp.Dispose();

    private TargetUpdater NewUpdater()
    {
        var verifier = new ManifestVerifier(new[] { _signer.PublicOnly() }, "1.0.0.0", () => _clock.Now);
        var health = new HealthChecker(_host, _paths, Logs.Null, () => _clock.Now, _clock.Delay);
        var applier = new UpdateApplier(_host, _paths, health, _ => Task.FromResult(true), Logs.Null,
            new ApplierOptions { IsWritableByBroadGroups = _ => false, CopyRetryDelays = new[] { TimeSpan.Zero }, CheckDiskSpace = false },
            () => _clock.Now, _clock.Delay);
        return new TargetUpdater(_cfg, _paths, _host, _api, verifier, applier, Logs.Null, "1.0.0.0", () => _localNow);
    }

    private void Offer(string version, string exe, Action<Manifest>? mutate = null, string? signWith = null)
    {
        _api.Offer = ReleaseBuilder.Build(_signer, _tmp.Sub("rel", version), "printing-outlet", version,
            new() { ["App.exe"] = exe }, mutateManifest: mutate, signWithOtherJson: signWith);
    }

    private string Exe => File.ReadAllText(Path.Combine(_install, "App.exe"), System.Text.Encoding.Latin1);

    [Fact]
    public async Task Full_cycle_downloads_verifies_swaps_and_reports_updated()
    {
        Offer("1.1.0.0", "v1.1.0.0 ok");
        var state = new UpdaterState();

        await NewUpdater().RunAsync(_target, state, force: false, CancellationToken.None);

        Assert.Equal("v1.1.0.0 ok", Exe);
        Assert.Equal("updated", _api.Reports.Last().Status);
        Assert.Equal("1.1.0.0", _api.Reports.Last().TargetVersion);
        Assert.Equal("{\"ApiKey\":\"site\"}", File.ReadAllText(Path.Combine(_install, "appsettings.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_paths.Staging, "printing-outlet")));
        Assert.Null(state.For("printing-outlet").FailedVersion);
    }

    [Fact]
    public async Task Nothing_newer_reports_current_and_changes_nothing()
    {
        var before = Exe;
        await NewUpdater().RunAsync(_target, new UpdaterState(), false, CancellationToken.None);
        Assert.Equal("current", _api.Reports.Single().Status);
        Assert.Equal(before, Exe);
    }

    [Fact]
    public async Task Outside_the_quiet_window_the_release_is_staged_and_applied_later_without_downloading_again()
    {
        Offer("1.1.0.0", "v1.1.0.0 ok");
        var state = new UpdaterState();
        var updater = NewUpdater();

        _localNow = new DateTime(2026, 10, 30, 14, 0, 0);
        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Equal("staged", _api.Reports.Last().Status);
        Assert.NotEqual("v1.1.0.0 ok", Exe);
        Assert.Equal(1, _api.Downloads);

        _localNow = new DateTime(2026, 10, 31, 4, 0, 0);
        _api.Offer = null; // even if the server stops offering it, the staged copy is still valid and signed
        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Equal("v1.1.0.0 ok", Exe);
        Assert.Equal(1, _api.Downloads);
        Assert.Equal("updated", _api.Reports.Last().Status);
    }

    [Fact]
    public async Task Force_ignores_the_window()
    {
        Offer("1.1.0.0", "v1.1.0.0 ok");
        _localNow = new DateTime(2026, 10, 30, 14, 0, 0);
        await NewUpdater().RunAsync(_target, new UpdaterState(), force: true, CancellationToken.None);
        Assert.Equal("v1.1.0.0 ok", Exe);
    }

    [Fact]
    public async Task Bad_signature_is_rejected_reported_and_never_downloaded()
    {
        Offer("1.1.0.0", "v1.1.0.0 ok", signWith: "{\"something\":\"else\"}");
        var before = Exe;
        await NewUpdater().RunAsync(_target, new UpdaterState(), false, CancellationToken.None);

        Assert.Equal("rejected", _api.Reports.Last().Status);
        Assert.Equal(0, _api.Downloads);
        Assert.Equal(before, Exe);
    }

    [Fact]
    public async Task Tampered_download_is_rejected_and_nothing_is_swapped()
    {
        Offer("1.1.0.0", "v1.1.0.0 ok");
        _api.CorruptDownload = f => File.AppendAllText(f, "garbage");
        var before = Exe;

        await NewUpdater().RunAsync(_target, new UpdaterState(), false, CancellationToken.None);

        Assert.Equal("rejected", _api.Reports.Last().Status);
        Assert.Equal(before, Exe);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_paths.Staging, "printing-outlet")));
        Assert.DoesNotContain(_host.Log, l => l.StartsWith("stop "));
    }

    [Fact]
    public async Task Failed_version_is_not_retried_until_a_higher_one_is_published()
    {
        Offer("1.2.0.0", "v1.2.0.0 deny");
        var state = new UpdaterState();
        var updater = NewUpdater();

        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Equal("rolledback", _api.Reports.Last().Status);
        Assert.Equal("1.2.0.0", state.For("printing-outlet").FailedVersion);
        var downloads = _api.Downloads;

        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Equal(downloads, _api.Downloads); // same bad version: skipped without downloading

        Offer("1.3.0.1", "v1.3.0.1 ok");
        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Equal("v1.3.0.1 ok", Exe);
        Assert.Null(state.For("printing-outlet").FailedVersion);
    }

    [Fact]
    public async Task Offline_check_is_silent_and_retried_soon()
    {
        _api.Offline = true;
        var state = new UpdaterState();
        await NewUpdater().RunAsync(_target, state, false, CancellationToken.None);

        Assert.Empty(_api.Reports);
        Assert.True(DateTime.UtcNow - state.For("printing-outlet").LastCheckUtc!.Value >= TimeSpan.FromHours(_cfg.CheckIntervalHours) - TimeSpan.FromMinutes(31));
    }

    [Fact]
    public async Task Missing_service_is_reported_not_crashed_on()
    {
        var t = new TargetConfig { Product = "printing-outlet", Services = { "Nope" } };
        await NewUpdater().RunAsync(t, new UpdaterState(), false, CancellationToken.None);
        Assert.Equal("refused", _api.Reports.Single().Status);
    }

    [Fact]
    public async Task Same_status_is_not_reported_twice_within_a_day()
    {
        var state = new UpdaterState();
        var updater = NewUpdater();
        await updater.RunAsync(_target, state, false, CancellationToken.None);
        await updater.RunAsync(_target, state, false, CancellationToken.None);
        Assert.Single(_api.Reports);
    }

    [Fact]
    public async Task Release_listing_a_site_owned_file_is_rejected()
    {
        _api.Offer = ReleaseBuilder.Build(_signer, _tmp.Sub("rel", "1.1.0.0"), "printing-outlet", "1.1.0.0",
            new() { ["App.exe"] = "v1.1.0.0 ok", ["appsettings.json"] = "{}" });
        var before = Exe;
        await NewUpdater().RunAsync(_target, new UpdaterState(), false, CancellationToken.None);
        Assert.Equal("rejected", _api.Reports.Last().Status);
        Assert.Equal(before, Exe);
        Assert.Equal("{\"ApiKey\":\"site\"}", File.ReadAllText(Path.Combine(_install, "appsettings.json")));
    }
}
