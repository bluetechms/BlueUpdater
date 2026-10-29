using System.Text.RegularExpressions;
using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

// A desktop app (BlueMicroTik): no service to stop, no status file. The update must wait while the exe is
// open in any session, replace the files otherwise, and check the exe's own version afterwards.
public class GuiTargetTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly TestSigner _signer = new();
    private readonly FakeClock _clock = new();
    private readonly Paths _paths;
    private readonly FakeHost _fake;
    private readonly GuiHost _host;
    private readonly string _install;
    private readonly string _exe;
    private readonly UpdaterConfig _cfg = new();
    private readonly TargetConfig _target;

    public GuiTargetTests()
    {
        _paths = new Paths(_tmp.Sub("data"));
        _paths.EnsureDirs();
        _install = _tmp.Sub("Program Files", "BlueMicroTik");
        Directory.CreateDirectory(_install);
        _exe = Path.Combine(_install, "BlueMicroTik.exe");
        File.WriteAllText(_exe, "v1.0.0.0");
        _fake = new FakeHost(_paths, _clock);
        _target = new TargetConfig { Product = "bluemicrotik", GuiExe = _exe };
        _host = new GuiHost(_fake, new[] { _target });
    }

    public void Dispose() => _tmp.Dispose();

    private static string? VersionOf(string path)
    {
        var m = Regex.Match(File.ReadAllText(path), @"v(\d+\.\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private UpdateApplier Applier()
    {
        var health = new HealthChecker(_host, _paths, Logs.Null, () => _clock.Now, _clock.Delay);
        return new UpdateApplier(_host, _paths, health, _ => Task.FromResult(true), Logs.Null,
            new ApplierOptions { IsWritableByBroadGroups = _ => false, CheckDiskSpace = false, ReadFileVersion = VersionOf, CopyRetryDelays = new[] { TimeSpan.Zero } },
            () => _clock.Now, _clock.Delay);
    }

    private async Task<ApplyResult> Apply(string version, string exeContent, Dictionary<string, string>? extra = null)
    {
        var files = new Dictionary<string, string> { ["BlueMicroTik.exe"] = exeContent };
        if (extra != null) foreach (var kv in extra) files[kv.Key] = kv.Value;
        var r = ReleaseBuilder.Build(_signer, _tmp.Sub("rel", version), "bluemicrotik", version, files);
        var vm = new ManifestVerifier(new[] { _signer.PublicOnly() }, "1.0.0.0", () => _clock.Now)
            .Verify(r.ManifestJson, r.Signature, "bluemicrotik", "1.0.0.0");
        var dir = _tmp.Sub("staged", version);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        PackageStager.Extract(r.Zip, r.Manifest, dir);
        var info = _host.Discover(GuiHost.NameFor("bluemicrotik"))!;
        return await Applier().ApplyAsync(_cfg, new[] { info }, vm, dir, "1.0.0.0", false, CancellationToken.None);
    }

    [Fact]
    public async Task Closed_app_is_updated_without_touching_any_service()
    {
        var result = await Apply("2.0.0.0", "v2.0.0.0", new() { ["Newtonsoft.Json.dll"] = "json" });

        Assert.Equal(ApplyOutcome.Updated, result.Outcome);
        Assert.Equal("v2.0.0.0", File.ReadAllText(_exe));
        Assert.Equal("json", File.ReadAllText(Path.Combine(_install, "Newtonsoft.Json.dll")));
        Assert.Empty(_fake.Log);
        Assert.False(File.Exists(_paths.JournalFile));
    }

    [Fact]
    public async Task Open_app_defers_the_update_and_leaves_everything_alone()
    {
        _fake.ExternalProcesses[_exe] = new List<int> { 777 }; // open in someone's session
        var result = await Apply("2.0.0.0", "v2.0.0.0");

        Assert.Equal(ApplyOutcome.Deferred, result.Outcome);
        Assert.Equal("v1.0.0.0", File.ReadAllText(_exe));
        Assert.False(File.Exists(_paths.JournalFile));
    }

    [Fact]
    public async Task Update_goes_through_once_the_app_is_closed()
    {
        _fake.ExternalProcesses[_exe] = new List<int> { 777 };
        Assert.Equal(ApplyOutcome.Deferred, (await Apply("2.0.0.0", "v2.0.0.0")).Outcome);
        _fake.ExternalProcesses.Clear();
        Assert.Equal(ApplyOutcome.Updated, (await Apply("2.0.0.0", "v2.0.0.0")).Outcome);
    }

    [Fact]
    public async Task Exe_that_does_not_report_the_released_version_is_rolled_back()
    {
        // The release claims 2.0.0.0 but the exe inside says 1.9.9.9: clients would loop on it forever.
        var result = await Apply("2.0.0.0", "v1.9.9.9", new() { ["extra.dll"] = "x" });

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal("v1.0.0.0", File.ReadAllText(_exe));
        Assert.False(File.Exists(Path.Combine(_install, "extra.dll")));
    }

    [Fact]
    public void Virtual_service_maps_to_the_exe_and_reports_running_only_while_open()
    {
        var name = GuiHost.NameFor("bluemicrotik");
        var info = _host.Discover(name)!;
        Assert.True(info.Gui);
        Assert.Equal(_exe, info.Application);
        Assert.Equal(ServiceState.Stopped, _host.GetState(name));
        _fake.ExternalProcesses[_exe] = new List<int> { 1 };
        Assert.Equal(ServiceState.Running, _host.GetState(name));
        Assert.Null(_host.Discover("gui:unknown"));
    }

    [Fact]
    public void Target_config_exposes_the_virtual_service_name()
    {
        Assert.Equal(new[] { "gui:bluemicrotik" }, _target.EffectiveServices());
        Assert.Equal(new[] { "BluePrinting" }, new TargetConfig { Product = "p", Services = { "BluePrinting" } }.EffectiveServices());
    }
}
