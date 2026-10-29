using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlueUpdater;

public enum ApplyOutcome
{
    Updated,     // new build installed and judged healthy
    RolledBack,  // new build was unhealthy; previous build restored (version is marked failed)
    Deferred,    // not now (app busy, a process still holds the image); retry later
    Refused,     // the machine fails a safety pre-check (writable install folder, reparse point, disk)
    Failed,      // the swap itself failed and was undone; retry later
    Unverified   // installed, but the api was unreachable so health could not be judged; kept
}

public sealed record ApplyResult(ApplyOutcome Outcome, string Detail);

public sealed class JournalFile
{
    public int Dir { get; set; }
    public string Rel { get; set; } = "";
    public bool HadOld { get; set; }
}

// Written before anything is touched and updated at each phase, so a power cut or crash mid-swap is
// recovered at the next updater start (the services stay Disabled until it is).
public sealed class SwapJournal
{
    public string Product { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    // stopping -> swapping -> verifying | rollingback
    public string Phase { get; set; } = "stopping";
    public List<string> Services { get; set; } = new();
    public Dictionary<string, StartType> StartTypes { get; set; } = new();
    public List<string> Dirs { get; set; } = new();
    public string BackupDir { get; set; } = "";
    public List<JournalFile> Files { get; set; } = new();
    public DateTime StartedUtc { get; set; }
}

public sealed class ApplierOptions
{
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(150);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan[] CopyRetryDelays { get; init; } = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };
    public Func<string, bool> IsWritableByBroadGroups { get; init; } = Acl.IsWritableByBroadGroups;
    public bool CheckDiskSpace { get; init; } = true;
    public int KeepRollbackCopies { get; init; } = 2;
}

public sealed class UpdateApplier
{
    private static readonly JsonSerializerOptions JournalJson = new() { WriteIndented = true };

    private readonly IServiceHost _host;
    private readonly Paths _paths;
    private readonly HealthChecker _health;
    private readonly Func<CancellationToken, Task<bool>> _apiReachable;
    private readonly ILogger _log;
    private readonly ApplierOptions _opt;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public UpdateApplier(IServiceHost host, Paths paths, HealthChecker health, Func<CancellationToken, Task<bool>> apiReachable,
        ILogger log, ApplierOptions? opt = null, Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _paths = paths;
        _health = health;
        _apiReachable = apiReachable;
        _log = log;
        _opt = opt ?? new ApplierOptions();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public bool HasUnfinishedJournal => File.Exists(_paths.JournalFile);

    public async Task<ApplyResult> ApplyAsync(
        UpdaterConfig cfg, IReadOnlyList<ServiceInfo> services, VerifiedManifest vm, string stagedFilesDir,
        string fromVersion, bool ignoreBusy, CancellationToken ct)
    {
        var m = vm.Manifest;
        var dirs = services.Select(s => s.InstallDir).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // ---- pre-checks: nothing has been touched yet ----
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) return new(ApplyOutcome.Refused, $"install folder '{dir}' does not exist");
            if (IsReparse(dir)) return new(ApplyOutcome.Refused, $"install folder '{dir}' is a link/junction");
            if (!cfg.AllowWritableInstallDir && _opt.IsWritableByBroadGroups(dir))
                return new(ApplyOutcome.Refused, $"install folder '{dir}' is writable by ordinary users; fix its permissions (or set AllowWritableInstallDir)");
            foreach (var f in m.Files)
            {
                var target = TargetPath(dir, f.Path);
                var reparse = ReparseAlong(dir, target);
                if (reparse != null) return new(ApplyOutcome.Refused, $"'{reparse}' is a link/junction");
            }
            if (_opt.CheckDiskSpace)
            {
                var need = m.Files.Sum(f => f.Size) * 2 + 100L * 1024 * 1024;
                try
                {
                    if (new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace < need)
                        return new(ApplyOutcome.Refused, $"not enough free disk space on {Path.GetPathRoot(dir)}");
                }
                catch { /* unknown drive type (UNC): skip */ }
            }
        }
        if (!ignoreBusy && BusyGate.IsBusy(_paths.Status, m.Product, TimeSpan.FromMinutes(cfg.BusyMinutes), _utcNow()))
            return new(ApplyOutcome.Deferred, "the app is printing or printed a job moments ago");

        // ---- journal first, then take the services out of play ----
        var stamp = _utcNow().ToString("yyyyMMddHHmmss");
        var journal = new SwapJournal
        {
            Product = m.Product,
            FromVersion = fromVersion,
            ToVersion = m.Version,
            Phase = "stopping",
            Services = services.Select(s => s.Name).ToList(),
            Dirs = dirs,
            BackupDir = Path.Combine(_paths.Rollback, m.Product, $"{fromVersion}_{stamp}"),
            StartedUtc = _utcNow()
        };
        foreach (var s in services) journal.StartTypes[s.Name] = _host.GetStartType(s.Name);
        WriteJournal(journal);

        try
        {
            foreach (var s in services) _host.SetStartType(s.Name, new StartType(4, 0)); // Disabled
            foreach (var s in services) _host.Stop(s.Name, _opt.StopTimeout);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not stop the services for the update; leaving the current build running");
            await TryRestoreServicesAsync(journal, ct);
            DeleteJournal();
            return new(ApplyOutcome.Deferred, "could not stop the service: " + e.Message);
        }

        // A stopped service is not proof that nothing is running: a user may have the exe open (for example
        // the --configure dialog), in any session.
        foreach (var image in services.Select(s => s.Application).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var left = _host.ProcessIdsForImage(image);
            if (left.Count > 0)
            {
                await TryRestoreServicesAsync(journal, ct);
                DeleteJournal();
                return new(ApplyOutcome.Deferred, $"'{Path.GetFileName(image)}' is still running outside the service (pid {string.Join(',', left)})");
            }
        }

        // ---- back up what will be replaced ----
        try
        {
            for (var d = 0; d < dirs.Count; d++)
            {
                foreach (var f in m.Files)
                {
                    var target = TargetPath(dirs[d], f.Path);
                    var had = File.Exists(target);
                    journal.Files.Add(new JournalFile { Dir = d, Rel = f.Path, HadOld = had });
                    if (!had) continue;
                    var backup = BackupPath(journal, d, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                    if (!string.Equals(PackageStager.Sha256OfFile(target), PackageStager.Sha256OfFile(backup), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"backup of '{f.Path}' does not match the original");
                }
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Backup failed; nothing was changed");
            await TryRestoreServicesAsync(journal, ct);
            DeleteJournal();
            return new(ApplyOutcome.Failed, "backup failed: " + e.Message);
        }

        journal.Phase = "swapping";
        WriteJournal(journal);

        // ---- swap: copy (not move) so the files inherit the install folder's permissions ----
        try
        {
            for (var d = 0; d < dirs.Count; d++)
            {
                foreach (var f in m.Files)
                {
                    var src = Path.Combine(stagedFilesDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    var target = TargetPath(dirs[d], f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await CopyWithRetryAsync(src, target, ct);
                    if (!string.Equals(PackageStager.Sha256OfFile(target), f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"'{f.Path}' does not match its hash after copying");
                }
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Swap failed; restoring the previous build");
            var restored = await RestoreAsync(journal, ct);
            return restored
                ? new(ApplyOutcome.Failed, "swap failed and was undone: " + e.Message)
                : new(ApplyOutcome.Failed, "swap failed and the previous build could not be fully restored: " + e.Message);
        }

        // ---- start the new build and watch it ----
        return await StartAndVerifyAsync(cfg, services, journal, ct);
    }

    private async Task<ApplyResult> StartAndVerifyAsync(UpdaterConfig cfg, IReadOnlyList<ServiceInfo> services, SwapJournal journal, CancellationToken ct)
    {
        journal.Phase = "verifying";
        WriteJournal(journal);
        var healthOpt = new HealthOptions
        {
            StableFor = TimeSpan.FromMinutes(cfg.HealthStableMinutes),
            Deadline = TimeSpan.FromMinutes(cfg.HealthDeadlineMinutes)
        };

        try
        {
            foreach (var s in services) _host.SetStartType(s.Name, journal.StartTypes[s.Name]);
            foreach (var s in services) _host.Start(s.Name, _opt.StartTimeout);
        }
        catch (Exception e)
        {
            _log.LogError(e, "The new build would not start; rolling back");
            return await RollbackAsync(cfg, services, journal, "service would not start: " + e.Message, ct);
        }

        var result = await _health.WaitAsync(journal.Product, services, journal.ToVersion, requireStatus: true, healthOpt,
            () => _apiReachable(ct), ct);
        _log.LogInformation("Health check for {Version}: {Verdict} ({Detail})", journal.ToVersion, result.Verdict, result.Detail);

        switch (result.Verdict)
        {
            case HealthVerdict.Healthy:
                DeleteJournal();
                PruneRollback(journal.Product);
                return new(ApplyOutcome.Updated, result.Detail);
            case HealthVerdict.Inconclusive:
                // Keep the new build: nothing says it is bad. The backup stays for a manual rollback.
                DeleteJournal();
                return new(ApplyOutcome.Unverified, result.Detail);
            default:
                return await RollbackAsync(cfg, services, journal, result.Detail, ct);
        }
    }

    private async Task<ApplyResult> RollbackAsync(UpdaterConfig cfg, IReadOnlyList<ServiceInfo> services, SwapJournal journal, string why, CancellationToken ct)
    {
        journal.Phase = "rollingback";
        WriteJournal(journal);
        var ok = await RestoreAsync(journal, ct);
        if (!ok) return new(ApplyOutcome.RolledBack, "rollback was incomplete: " + why);

        // The previous build may predate the status file, so only process stability can be judged.
        var opt = new HealthOptions
        {
            StableFor = TimeSpan.FromMinutes(Math.Min(cfg.HealthStableMinutes, 2)),
            Deadline = TimeSpan.FromMinutes(5)
        };
        var h = await _health.WaitAsync(journal.Product, services, journal.FromVersion, requireStatus: false, opt,
            () => _apiReachable(ct), ct);
        _log.LogWarning("Rolled back to {Version} ({Why}); stability check: {Verdict}", journal.FromVersion, why, h.Verdict);
        return new(ApplyOutcome.RolledBack, why + (h.Verdict == HealthVerdict.Healthy ? "" : "; previous build also unstable: " + h.Detail));
    }

    // Puts the previous build back (files, start types) and starts the services. Used by rollback, by a
    // failed swap, and by crash recovery. Returns false if some file could not be restored.
    private async Task<bool> RestoreAsync(SwapJournal journal, CancellationToken ct)
    {
        var allOk = true;
        foreach (var name in journal.Services)
        {
            try { _host.SetStartType(name, new StartType(4, 0)); } catch (Exception e) { _log.LogWarning(e, "Could not disable {Service}", name); }
            try { _host.Stop(name, _opt.StopTimeout); } catch (Exception e) { _log.LogWarning(e, "Could not stop {Service}", name); allOk = false; }
        }

        foreach (var f in journal.Files)
        {
            var target = TargetPath(journal.Dirs[f.Dir], f.Rel);
            try
            {
                if (f.HadOld)
                {
                    var backup = BackupPath(journal, f.Dir, f.Rel);
                    if (!File.Exists(backup)) { allOk = false; _log.LogError("Backup for {File} is missing", f.Rel); }
                    // A file the swap never got to (or that is locked and unchanged) is already right.
                    else if (!(File.Exists(target) && SameContent(target, backup))) await CopyWithRetryAsync(backup, target, ct);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target); // the new build added it; the old one never had it
                }
            }
            catch (Exception e)
            {
                allOk = false;
                _log.LogError(e, "Could not restore {File}", f.Rel);
            }
        }

        await TryRestoreServicesAsync(journal, ct);
        if (allOk) DeleteJournal();
        return allOk;
    }

    private Task TryRestoreServicesAsync(SwapJournal journal, CancellationToken ct)
    {
        foreach (var name in journal.Services)
        {
            try { if (journal.StartTypes.TryGetValue(name, out var st)) _host.SetStartType(name, st); }
            catch (Exception e) { _log.LogError(e, "Could not restore the start type of {Service}", name); }
        }
        foreach (var name in journal.Services)
        {
            try
            {
                if (journal.StartTypes.TryGetValue(name, out var st) && st.Start == 4) continue; // was disabled before us
                _host.Start(name, _opt.StartTimeout);
            }
            catch (Exception e) { _log.LogError(e, "Could not start {Service}", name); }
        }
        return Task.CompletedTask;
    }

    // Runs at updater start. A journal still on disk means the previous run died mid-swap.
    public async Task<(SwapJournal Journal, ApplyResult Result)?> RecoverAsync(UpdaterConfig cfg, CancellationToken ct)
    {
        if (!File.Exists(_paths.JournalFile)) return null;
        SwapJournal? journal;
        try { journal = JsonSerializer.Deserialize<SwapJournal>(File.ReadAllText(_paths.JournalFile)); }
        catch { journal = null; }
        if (journal == null)
        {
            _log.LogError("swap.state is unreadable; removing it");
            DeleteJournal();
            return null;
        }

        _log.LogWarning("Recovering an interrupted update of {Product} {From} -> {To} (phase {Phase})",
            journal.Product, journal.FromVersion, journal.ToVersion, journal.Phase);
        var services = journal.Services.Select(n => _host.Discover(n)).Where(s => s != null).Cast<ServiceInfo>().ToList();

        if (journal.Phase == "verifying" && services.Count == journal.Services.Count)
        {
            // The swap completed; only the observation was cut short. Make sure start types are restored
            // (the reboot may have happened before that) and watch again.
            return (journal, await StartAndVerifyAsync(cfg, services, journal, ct));
        }

        var ok = await RestoreAsync(journal, ct);
        return (journal, new ApplyResult(ApplyOutcome.RolledBack,
            ok ? $"interrupted during '{journal.Phase}'; previous build restored"
               : $"interrupted during '{journal.Phase}'; restore incomplete"));
    }

    private void PruneRollback(string product)
    {
        try
        {
            var root = Path.Combine(_paths.Rollback, product);
            if (!Directory.Exists(root)) return;
            var old = new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(_opt.KeepRollbackCopies);
            foreach (var d in old) d.Delete(true);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not prune old rollback copies");
        }
    }

    private async Task CopyWithRetryAsync(string src, string dst, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, true);
                return;
            }
            catch (IOException) when (attempt < _opt.CopyRetryDelays.Length)
            {
                // Defender / the indexer often holds a fresh file for a moment.
                await _delay(_opt.CopyRetryDelays[attempt], ct);
            }
            catch (UnauthorizedAccessException) when (attempt < _opt.CopyRetryDelays.Length)
            {
                await _delay(_opt.CopyRetryDelays[attempt], ct);
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            return new FileInfo(a).Length == new FileInfo(b).Length
                   && string.Equals(PackageStager.Sha256OfFile(a), PackageStager.Sha256OfFile(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string TargetPath(string dir, string rel) =>
        Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string BackupPath(SwapJournal j, int dirIndex, string rel) =>
        Path.Combine(j.BackupDir, dirIndex.ToString(), rel.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    // The first existing link/junction between the install folder and the file (inclusive), or null.
    private static string? ReparseAlong(string root, string target)
    {
        var current = target;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length > rootFull.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) break;
            current = parent;
        }
        return null;
    }

    private void WriteJournal(SwapJournal j)
    {
        Directory.CreateDirectory(_paths.Updater);
        AtomicFile.WriteAllText(_paths.JournalFile, JsonSerializer.Serialize(j, JournalJson));
    }

    private void DeleteJournal()
    {
        try { File.Delete(_paths.JournalFile); } catch { /* best effort */ }
    }
}
