using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

public class ApplierTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly TestSigner _signer = new();
    private readonly FakeClock _clock = new();
    private readonly Paths _paths;
    private readonly FakeHost _host;
    private readonly string _install;
    private readonly UpdaterConfig _cfg = new() { HealthStableMinutes = 1, HealthDeadlineMinutes = 15, BusyMinutes = 10 };
    private bool _apiReachable = true;
    private bool _writableInstall;

    private const string OldExe = "v1.0.0.0 old";
    private const string Settings = "{\"ApiKey\":\"site-secret\"}";

    public ApplierTests()
    {
        _paths = new Paths(_tmp.Sub("data"));
        _paths.EnsureDirs();
        _install = _tmp.Sub("install");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "App.exe"), OldExe);
        File.WriteAllText(Path.Combine(_install, "appsettings.json"), Settings);
        File.WriteAllText(Path.Combine(_install, "logo.png"), "logo");
        _host = new FakeHost(_paths, _clock);
        _host.Add("BluePrinting", _install);
    }

    public void Dispose() => _tmp.Dispose();

    private UpdateApplier NewApplier(ApplierOptions? opt = null)
    {
        var health = new HealthChecker(_host, _paths, Logs.Null, () => _clock.Now, _clock.Delay);
        return new UpdateApplier(_host, _paths, health, _ => Task.FromResult(_apiReachable), Logs.Null,
            opt ?? new ApplierOptions
            {
                IsWritableByBroadGroups = _ => _writableInstall,
                CopyRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero },
                CheckDiskSpace = false
            },
            () => _clock.Now, _clock.Delay);
    }

    private (VerifiedManifest vm, string filesDir) Stage(string version, string exeContent, Dictionary<string, string>? extra = null)
    {
        var files = new Dictionary<string, string> { ["App.exe"] = exeContent };
        if (extra != null) foreach (var kv in extra) files[kv.Key] = kv.Value;
        var r = ReleaseBuilder.Build(_signer, _tmp.Sub("rel", version), "printing-outlet", version, files);
        var verifier = new ManifestVerifier(new[] { _signer.PublicOnly() }, "1.0.0.0", () => _clock.Now);
        var vm = verifier.Verify(r.ManifestJson, r.Signature, "printing-outlet", "1.0.0.0");
        var dir = _tmp.Sub("staged", version);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        PackageStager.Extract(r.Zip, r.Manifest, dir);
        return (vm, dir);
    }

    private Task<ApplyResult> Apply(string version, string exe, Dictionary<string, string>? extra = null, bool ignoreBusy = false, UpdateApplier? applier = null)
    {
        var (vm, dir) = Stage(version, exe, extra);
        return (applier ?? NewApplier()).ApplyAsync(_cfg, _host.Services.Values.Select(s => s.Info).ToList(), vm, dir, "1.0.0.0", ignoreBusy, CancellationToken.None);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_install, rel));

    private void AssertSiteFilesUntouched()
    {
        Assert.Equal(Settings, Read("appsettings.json"));
        Assert.Equal("logo", Read("logo.png"));
    }

    private void AssertServiceBackToNormal(string name = "BluePrinting")
    {
        var s = _host.Services[name];
        Assert.Equal(ServiceState.Running, s.State);
        Assert.Equal(new StartType(2, 1), s.Start); // delayed-auto, as before
        Assert.False(File.Exists(_paths.JournalFile));
    }

    [Fact]
    public async Task Healthy_update_swaps_files_keeps_site_files_and_restores_start_type()
    {
        var result = await Apply("1.1.0.0", "v1.1.0.0 ok", new() { ["lib/New.dll"] = "newdll" });

        Assert.True(result.Outcome == ApplyOutcome.Updated, result.Outcome + ": " + result.Detail);
        Assert.Equal("v1.1.0.0 ok", Read("App.exe"));
        Assert.Equal("newdll", Read(Path.Combine("lib", "New.dll")));
        AssertSiteFilesUntouched();
        AssertServiceBackToNormal();
        Assert.Contains(Directory.GetFiles(_paths.Rollback, "App.exe", SearchOption.AllDirectories), f => File.ReadAllText(f) == OldExe);
    }

    [Fact]
    public async Task Start_type_is_Disabled_while_the_files_are_swapped_so_a_reboot_cannot_start_a_half_swapped_app()
    {
        await Apply("1.1.0.0", "v1.1.0.0 ok");
        var log = _host.Log;
        var disabled = log.IndexOf("starttype BluePrinting 4/0");
        var stop = log.IndexOf("stop BluePrinting");
        var restored = log.IndexOf("starttype BluePrinting 2/1");
        var start = log.LastIndexOf("start BluePrinting");
        Assert.True(disabled >= 0 && disabled < stop && stop < restored && restored < start, string.Join(" | ", log));
    }

    [Fact]
    public async Task App_refused_by_the_api_is_rolled_back_and_the_old_build_runs_again()
    {
        var result = await Apply("1.2.0.0", "v1.2.0.0 deny", new() { ["lib/New.dll"] = "newdll" });

        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        Assert.False(File.Exists(Path.Combine(_install, "lib", "New.dll")), "a file the new build added must be removed");
        AssertSiteFilesUntouched();
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Crash_looping_build_is_rolled_back()
    {
        var result = await Apply("1.3.0.0", "v1.3.0.0 crash");
        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Latched_authentication_failure_is_rolled_back_without_waiting_for_the_deadline()
    {
        var before = _clock.Now;
        var result = await Apply("1.4.0.0", "v1.4.0.0 latch");
        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.True(_clock.Now - before < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Unreachable_api_leaves_the_new_build_in_place_as_unverified()
    {
        _apiReachable = false;
        var result = await Apply("1.2.0.0", "v1.2.0.0 deny");
        Assert.Equal(ApplyOutcome.Unverified, result.Outcome);
        Assert.Equal("v1.2.0.0 deny", Read("App.exe"));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Service_that_will_not_stop_defers_the_update_and_touches_nothing()
    {
        _host.FailStop = true;
        var result = await Apply("1.1.0.0", "v1.1.0.0 ok");

        Assert.Equal(ApplyOutcome.Deferred, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        Assert.Equal(new StartType(2, 1), _host.Services["BluePrinting"].Start);
        Assert.False(File.Exists(_paths.JournalFile));
    }

    [Fact]
    public async Task Exe_still_running_outside_the_service_defers_the_update()
    {
        _host.ExternalProcesses[Path.Combine(_install, "App.exe")] = new List<int> { 4242 }; // e.g. an open --configure dialog
        var result = await Apply("1.1.0.0", "v1.1.0.0 ok");

        Assert.Equal(ApplyOutcome.Deferred, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Busy_app_defers_unless_forced()
    {
        _host.WriteBusyStatus("BluePrinting", busy: true);
        var deferred = await Apply("1.1.0.0", "v1.1.0.0 ok");
        Assert.Equal(ApplyOutcome.Deferred, deferred.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));

        var forced = await Apply("1.1.0.0", "v1.1.0.0 ok", ignoreBusy: true);
        Assert.Equal(ApplyOutcome.Updated, forced.Outcome);
    }

    [Fact]
    public async Task A_job_in_the_last_few_minutes_also_defers()
    {
        _host.WriteBusyStatus("BluePrinting", busy: false, lastJob: _clock.Now.AddMinutes(-3));
        Assert.Equal(ApplyOutcome.Deferred, (await Apply("1.1.0.0", "v1.1.0.0 ok")).Outcome);

        _host.WriteBusyStatus("BluePrinting", busy: false, lastJob: _clock.Now.AddMinutes(-30));
        Assert.Equal(ApplyOutcome.Updated, (await Apply("1.1.0.0", "v1.1.0.0 ok")).Outcome);
    }

    [Fact]
    public async Task Install_folder_writable_by_ordinary_users_is_refused_unless_opted_in()
    {
        _writableInstall = true;
        var refused = await Apply("1.1.0.0", "v1.1.0.0 ok");
        Assert.Equal(ApplyOutcome.Refused, refused.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));

        _cfg.AllowWritableInstallDir = true;
        Assert.Equal(ApplyOutcome.Updated, (await Apply("1.1.0.0", "v1.1.0.0 ok")).Outcome);
    }

    [Fact]
    public async Task Junction_inside_the_install_folder_is_refused()
    {
        var elsewhere = _tmp.Sub("elsewhere");
        Directory.CreateDirectory(elsewhere);
        var link = Path.Combine(_install, "lib");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "test setup: could not create a junction");

        var result = await Apply("1.1.0.0", "v1.1.0.0 ok", new() { ["lib/New.dll"] = "x" });
        Assert.Equal(ApplyOutcome.Refused, result.Outcome);
        Assert.Empty(Directory.GetFiles(elsewhere));
        Assert.Equal(OldExe, Read("App.exe"));
    }

    [Fact]
    public async Task Locked_file_fails_the_swap_and_everything_already_copied_is_put_back()
    {
        File.WriteAllText(Path.Combine(_install, "Second.dll"), "old-second");
        // Held open for reading: the backup can read it, but the new copy cannot overwrite it.
        using var hold = new FileStream(Path.Combine(_install, "Second.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await Apply("1.1.0.0", "v1.1.0.0 ok", new() { ["Second.dll"] = "new-second" });

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        Assert.Equal("old-second", Read("Second.dll"));
        AssertSiteFilesUntouched();
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Two_services_sharing_one_folder_are_both_stopped_swapped_once_and_restarted()
    {
        _host.Add("BluePrinting2", _install);
        var result = await Apply("1.1.0.0", "v1.1.0.0 ok");

        Assert.Equal(ApplyOutcome.Updated, result.Outcome);
        AssertServiceBackToNormal("BluePrinting");
        AssertServiceBackToNormal("BluePrinting2");
        Assert.Equal(2, _host.Log.Count(l => l.StartsWith("stop ")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_paths.Rollback, "printing-outlet")));
    }

    [Fact]
    public async Task One_unhealthy_service_of_two_rolls_both_back()
    {
        _host.Add("BluePrinting2", _install);
        // both run the same exe, so both behave the same: deny
        var result = await Apply("1.2.0.0", "v1.2.0.0 deny");
        Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        AssertServiceBackToNormal("BluePrinting");
        AssertServiceBackToNormal("BluePrinting2");
    }

    [Fact]
    public async Task Only_the_two_newest_rollback_copies_are_kept()
    {
        for (var i = 1; i <= 4; i++)
        {
            _clock.Now = _clock.Now.AddMinutes(30);
            var exe = $"v1.{i}.0.0 ok";
            var (vm, dir) = Stage($"1.{i}.0.0", exe);
            var from = i == 1 ? "1.0.0.0" : $"1.{i - 1}.0.0";
            var r = await NewApplier().ApplyAsync(_cfg, _host.Services.Values.Select(s => s.Info).ToList(), vm, dir, from, true, CancellationToken.None);
            Assert.Equal(ApplyOutcome.Updated, r.Outcome);
            await Task.Delay(30); // distinct CreationTime for the pruning order
        }
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_paths.Rollback, "printing-outlet")).Length);
    }

    // ---- crash recovery ----

    private SwapJournal HalfDoneJournal(string phase)
    {
        var backup = _tmp.Sub("data", "updater", "rollback", "printing-outlet", "1.0.0.0_x");
        Directory.CreateDirectory(Path.Combine(backup, "0"));
        File.WriteAllText(Path.Combine(backup, "0", "App.exe"), OldExe);
        return new SwapJournal
        {
            Product = "printing-outlet",
            FromVersion = "1.0.0.0",
            ToVersion = "1.1.0.0",
            Phase = phase,
            Services = new() { "BluePrinting" },
            StartTypes = new() { ["BluePrinting"] = new StartType(2, 1) },
            Dirs = new() { _install },
            BackupDir = backup,
            Files = new() { new JournalFile { Dir = 0, Rel = "App.exe", HadOld = true }, new JournalFile { Dir = 0, Rel = "New.dll", HadOld = false } },
            StartedUtc = _clock.Now
        };
    }

    private void SimulateMachineStateAfterPowerCut(SwapJournal j)
    {
        File.WriteAllText(_paths.JournalFile, JsonSerializer.Serialize(j));
        File.WriteAllText(Path.Combine(_install, "App.exe"), "v1.1.0.0 ok");
        File.WriteAllText(Path.Combine(_install, "New.dll"), "half");
        var svc = _host.Services["BluePrinting"];
        svc.State = ServiceState.Stopped;
        svc.Pid = 0;
        svc.Start = new StartType(4, 0); // left Disabled by the interrupted swap
    }

    [Fact]
    public async Task Interrupted_swap_is_undone_at_the_next_start()
    {
        var j = HalfDoneJournal("swapping");
        SimulateMachineStateAfterPowerCut(j);

        var recovered = await NewApplier().RecoverAsync(_cfg, CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(ApplyOutcome.RolledBack, recovered!.Value.Result.Outcome);
        Assert.Equal(OldExe, Read("App.exe"));
        Assert.False(File.Exists(Path.Combine(_install, "New.dll")));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Interruption_before_any_file_changed_just_brings_the_old_build_back_up()
    {
        var j = HalfDoneJournal("stopping");
        j.Files.Clear();
        File.WriteAllText(_paths.JournalFile, JsonSerializer.Serialize(j));
        var svc = _host.Services["BluePrinting"];
        svc.State = ServiceState.Stopped;
        svc.Start = new StartType(4, 0);

        await NewApplier().RecoverAsync(_cfg, CancellationToken.None);

        Assert.Equal(OldExe, Read("App.exe"));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task Interruption_while_verifying_resumes_the_health_check()
    {
        var j = HalfDoneJournal("verifying");
        File.WriteAllText(Path.Combine(_install, "App.exe"), "v1.1.0.0 ok");
        File.WriteAllText(_paths.JournalFile, JsonSerializer.Serialize(j));
        var svc = _host.Services["BluePrinting"];
        svc.State = ServiceState.Stopped;
        svc.Start = new StartType(2, 1);

        var recovered = await NewApplier().RecoverAsync(_cfg, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Updated, recovered!.Value.Result.Outcome);
        Assert.Equal("v1.1.0.0 ok", Read("App.exe"));
        AssertServiceBackToNormal();
    }

    [Fact]
    public async Task No_journal_means_nothing_to_recover()
    {
        Assert.Null(await NewApplier().RecoverAsync(_cfg, CancellationToken.None));
    }
}
