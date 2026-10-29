using Microsoft.Extensions.Logging;

namespace BlueUpdater;

// One product on this PC: find out what is installed, ask the api what is newer, download + verify +
// stage it, and (inside the quiet window, when the app is idle) hand it to the UpdateApplier.
public sealed class TargetUpdater
{
    private readonly UpdaterConfig _cfg;
    private readonly Paths _paths;
    private readonly IServiceHost _host;
    private readonly IUpdateApi _api;
    private readonly ManifestVerifier _verifier;
    private readonly UpdateApplier _applier;
    private readonly ILogger _log;
    private readonly string _updaterVersion;
    private readonly Func<DateTime> _now;

    public TargetUpdater(UpdaterConfig cfg, Paths paths, IServiceHost host, IUpdateApi api, ManifestVerifier verifier,
        UpdateApplier applier, ILogger log, string updaterVersion, Func<DateTime>? localNow = null)
    {
        _cfg = cfg;
        _paths = paths;
        _host = host;
        _api = api;
        _verifier = verifier;
        _applier = applier;
        _log = log;
        _updaterVersion = updaterVersion;
        _now = localNow ?? (() => DateTime.Now);
    }

    public static string InstanceName(TargetConfig t) => string.Join(",", t.Services).Truncate(80);

    // `force` ignores the quiet window and the busy check (technician testing on a bench PC).
    public async Task RunAsync(TargetConfig target, UpdaterState state, bool force, CancellationToken ct)
    {
        var ps = state.For(target.Product);
        var instance = InstanceName(target);

        var services = new List<ServiceInfo>();
        foreach (var name in target.Services)
        {
            var info = _host.Discover(name);
            if (info == null)
            {
                _log.LogWarning("Service {Service} is not installed (or has no NSSM Application); skipping {Product}", name, target.Product);
                await ReportAsync(target, ps, "refused", null, null, $"service '{name}' not found", ct);
                return;
            }
            services.Add(info);
        }

        var installed = InstalledVersion(services);
        if (installed == null)
        {
            _log.LogWarning("Cannot read the installed version of {Product}", target.Product);
            await ReportAsync(target, ps, "refused", null, null, "cannot read installed version", ct);
            return;
        }

        // Use what is already staged if it is still newer than what is installed; otherwise ask the api.
        var staged = FindStaged(target.Product, installed);
        if (staged == null)
        {
            ps.LastCheckUtc = DateTime.UtcNow;
            ManifestResponse? offer;
            try
            {
                offer = await _api.GetManifestAsync(target.Product, installed, instance, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Update check for {Product} failed: {Message}", target.Product, e.Message);
                // offline: look again in ~30 minutes rather than waiting a whole interval
                ps.LastCheckUtc = DateTime.UtcNow - TimeSpan.FromHours(_cfg.CheckIntervalHours) + TimeSpan.FromMinutes(30);
                return;
            }

            if (offer == null)
            {
                await ReportAsync(target, ps, "current", installed, null, null, ct);
                return;
            }

            VerifiedManifest vm;
            try
            {
                vm = _verifier.Verify(offer.Manifest, offer.Signature, target.Product, installed);
            }
            catch (UpdateRejectedException e)
            {
                _log.LogError("Rejected the offered {Product} update: {Reason}", target.Product, e.Message);
                await ReportAsync(target, ps, "rejected", installed, offer.Version, e.Message, ct);
                return;
            }

            if (ps.FailedVersion != null && VersionUtil.Compare(vm.Manifest.Version, ps.FailedVersion) <= 0)
            {
                _log.LogInformation("{Product} {Version} failed here before; waiting for a newer release", target.Product, vm.Manifest.Version);
                return;
            }

            try
            {
                staged = await StageAsync(target, vm, instance, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError("Staging {Product} {Version} failed: {Message}", target.Product, vm.Manifest.Version, e.Message);
                await ReportAsync(target, ps, e is UpdateRejectedException ? "rejected" : "failed", installed, vm.Manifest.Version, e.Message, ct);
                return;
            }
        }

        if (ps.FailedVersion != null && VersionUtil.Compare(staged.Verified.Manifest.Version, ps.FailedVersion) <= 0) return;

        if (!force && !_cfg.InWindow(_now()))
        {
            _log.LogInformation("{Product} {Version} is staged; waiting for the quiet window", target.Product, staged.Verified.Manifest.Version);
            await ReportAsync(target, ps, "staged", installed, staged.Verified.Manifest.Version, null, ct);
            return;
        }

        var result = await _applier.ApplyAsync(_cfg, services, staged.Verified, staged.FilesDir, installed, force, ct);
        await HandleResultAsync(target, ps, installed, staged.Verified.Manifest.Version, result, ct);
        if (result.Outcome is ApplyOutcome.Updated or ApplyOutcome.Unverified or ApplyOutcome.RolledBack)
            DeleteStaged(target.Product, staged.Verified.Manifest.Version);
    }

    public async Task HandleResultAsync(TargetConfig target, ProductState ps, string from, string to, ApplyResult result, CancellationToken ct)
    {
        _log.LogInformation("{Product} {From} -> {To}: {Outcome} ({Detail})", target.Product, from, to, result.Outcome, result.Detail);
        switch (result.Outcome)
        {
            case ApplyOutcome.Updated:
                ps.FailedVersion = null;
                await ReportAsync(target, ps, "updated", to, to, null, ct);
                break;
            case ApplyOutcome.Unverified:
                await ReportAsync(target, ps, "unverified", to, to, result.Detail, ct);
                break;
            case ApplyOutcome.RolledBack:
                ps.FailedVersion = to; // never retried until a higher version is published
                await ReportAsync(target, ps, "rolledback", from, to, result.Detail, ct);
                break;
            case ApplyOutcome.Refused:
                await ReportAsync(target, ps, "refused", from, to, result.Detail, ct);
                break;
            case ApplyOutcome.Failed:
                await ReportAsync(target, ps, "failed", from, to, result.Detail, ct);
                break;
            case ApplyOutcome.Deferred:
                await ReportAsync(target, ps, "deferred", from, to, result.Detail, ct);
                break;
        }
    }

    // The lowest version among the services' executables (all of them must end up on the new build).
    public string? InstalledVersion(IReadOnlyList<ServiceInfo> services)
    {
        string? lowest = null;
        foreach (var s in services)
        {
            string? v = null;
            try { v = VersionUtil.Normalize(System.Diagnostics.FileVersionInfo.GetVersionInfo(s.Application).FileVersion); }
            catch { /* missing exe */ }
            if (v == null) return null;
            if (lowest == null || VersionUtil.Compare(v, lowest) < 0) lowest = v;
        }
        return lowest;
    }

    public sealed record Staged(VerifiedManifest Verified, string FilesDir);

    private string StageDir(string product, string version) => Path.Combine(_paths.Staging, product, version);

    private async Task<Staged> StageAsync(TargetConfig target, VerifiedManifest vm, string instance, CancellationToken ct)
    {
        var m = vm.Manifest;
        var dir = StageDir(target.Product, m.Version);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "package.zip");
            _log.LogInformation("Downloading {Product} {Version} ({Size} bytes)", target.Product, m.Version, m.Package!.Size);
            await _api.DownloadAsync(target.Product, m.Version, instance, zip, m.Package.Size, ct);
            PackageStager.VerifyPackageFile(zip, m.Package);
            var files = Path.Combine(dir, "files");
            PackageStager.Extract(zip, m, files);
            File.Delete(zip);
            // Persist exactly what was signed so a restart can re-verify instead of re-downloading.
            File.WriteAllText(Path.Combine(dir, "manifest.json"), vm.RawJson, new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "manifest.sig"), vm.SignatureBase64);
            File.WriteAllText(Path.Combine(dir, "ready"), "");
            return new Staged(vm, files);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
            throw;
        }
    }

    // A previously staged release, re-verified from disk (signature, still newer than installed, files intact).
    private Staged? FindStaged(string product, string installed)
    {
        var root = Path.Combine(_paths.Staging, product);
        if (!Directory.Exists(root)) return null;
        Staged? best = null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, "ready"))) throw new InvalidDataException("incomplete");
                var vm = _verifier.Verify(File.ReadAllText(Path.Combine(dir, "manifest.json")),
                    File.ReadAllText(Path.Combine(dir, "manifest.sig")).Trim(), product, installed);
                var files = Path.Combine(dir, "files");
                PackageStager.VerifyExtracted(vm.Manifest, files);
                if (best == null || VersionUtil.Compare(vm.Manifest.Version, best.Verified.Manifest.Version) > 0)
                    best = new Staged(vm, files);
            }
            catch (Exception e)
            {
                _log.LogInformation("Discarding staged folder {Dir}: {Reason}", Path.GetFileName(dir), e.Message);
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }
        return best;
    }

    private void DeleteStaged(string product, string version)
    {
        try { Directory.Delete(StageDir(product, version), true); } catch { /* best effort */ }
    }

    // One report per change of status, plus a check-in at least daily so the fleet table shows last-seen.
    public async Task ReportAsync(TargetConfig target, ProductState ps, string status, string? version, string? targetVersion, string? error, CancellationToken ct)
    {
        var changed = !string.Equals(ps.LastReportedStatus, status, StringComparison.Ordinal);
        var stale = ps.LastReportUtc == null || DateTime.UtcNow - ps.LastReportUtc > TimeSpan.FromHours(24);
        if (!changed && !stale) return;
        try
        {
            await _api.ReportAsync(target.Product, InstanceName(target), new ReportBody
            {
                Status = status,
                Version = version,
                TargetVersion = targetVersion,
                UpdaterVersion = _updaterVersion,
                Error = error
            }, ct);
            ps.LastReportedStatus = status;
            ps.LastReportUtc = DateTime.UtcNow;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogInformation("Could not send the {Status} report: {Message}", status, e.Message);
        }
    }
}

internal static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}
