using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BlueUpdater;

// What an app writes to %ProgramData%\BlueHMS\status\<product>_<instance>.json (see BluePOSPrinting
// UpdateSupport.cs). Read tolerantly: unknown fields are ignored and a half-written file is just "absent".
public sealed class AppStatusFile
{
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public int Pid { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? UpdatedUtc { get; init; }
    public DateTime? LastApiOkUtc { get; init; }
    public int? LastDeniedCode { get; init; }
    public bool AuthLatched { get; init; }
    public bool Busy { get; init; }
    public DateTime? LastJobUtc { get; init; }

    public static List<AppStatusFile> ReadAll(string statusDir, string product)
    {
        var list = new List<AppStatusFile>();
        if (!Directory.Exists(statusDir)) return list;
        foreach (var file in Directory.EnumerateFiles(statusDir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                if (!string.Equals(Str(r, "product"), product, StringComparison.Ordinal)) continue;
                list.Add(new AppStatusFile
                {
                    Product = product,
                    Version = Str(r, "version") ?? "",
                    Pid = Int(r, "pid") ?? 0,
                    StartedUtc = Time(r, "startedUtc"),
                    UpdatedUtc = Time(r, "updatedUtc"),
                    LastApiOkUtc = Time(r, "lastApiOkUtc"),
                    LastDeniedCode = Int(r, "lastDeniedCode"),
                    AuthLatched = Bool(r, "authLatched"),
                    Busy = Bool(r, "busy"),
                    LastJobUtc = Time(r, "lastJobUtc")
                });
            }
            catch
            {
                // partial write or foreign file: ignore
            }
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // TryGetInt32 throws (rather than returning false) on a JSON null, which the app writes for "no value".
    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime
            : null;
}

public enum HealthVerdict { Healthy, Unhealthy, Inconclusive }

public sealed record HealthResult(HealthVerdict Verdict, string Detail);

public sealed class HealthOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    // The services must keep the same processes (no restarts) for this long.
    public TimeSpan StableFor { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxRestarts { get; init; } = 3;
}

// Judges a freshly started build. Healthy = the same process(es) for StableFor and, when the app reports
// status, a 2xx api call on the expected version since that process started. The updater never runs the
// app and never prints: the status file is the app's own word on whether it can do its job.
public sealed class HealthChecker
{
    private readonly IServiceHost _host;
    private readonly Paths _paths;
    private readonly ILogger _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HealthChecker(IServiceHost host, Paths paths, ILogger log,
        Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _paths = paths;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    // requireStatus=false is for a rollback to a build that predates the status file (e.g. 1.0.0.0):
    // process stability is all that can be judged. apiReachable is only consulted at the deadline.
    public async Task<HealthResult> WaitAsync(
        string product, IReadOnlyList<ServiceInfo> services, string expectedVersion,
        bool requireStatus, HealthOptions opt, Func<Task<bool>> apiReachable, CancellationToken ct)
    {
        var started = _utcNow();
        HashSet<int>? stablePids = null;
        DateTime stableSince = default;
        var restarts = 0;
        string lastWhy = "waiting for the first poll";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var allRunning = services.All(s => _host.GetState(s.Name) == ServiceState.Running);
            var images = services.Select(s => s.Application).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pids = new HashSet<int>(images.SelectMany(i => _host.ProcessIdsForImage(i)));

            if (!allRunning || pids.Count == 0)
            {
                lastWhy = !allRunning ? "a service is not running" : "no app process found";
                if (stablePids != null) { restarts++; stablePids = null; }
            }
            else if (stablePids == null || !stablePids.SetEquals(pids))
            {
                if (stablePids != null) restarts++;
                stablePids = pids;
                stableSince = _utcNow();
                lastWhy = "processes (re)started";
            }

            if (restarts >= opt.MaxRestarts)
                return new HealthResult(HealthVerdict.Unhealthy, $"app restarted {restarts} times (crash loop)");

            if (stablePids != null)
            {
                var statusOk = true;
                if (requireStatus)
                {
                    var all = AppStatusFile.ReadAll(_paths.Status, product);
                    foreach (var pid in stablePids)
                    {
                        var st = all.FirstOrDefault(s => s.Pid == pid);
                        if (st == null) { statusOk = false; lastWhy = $"no status file yet for pid {pid}"; break; }
                        if (!string.Equals(VersionUtil.Normalize(st.Version), expectedVersion, StringComparison.Ordinal))
                            { statusOk = false; lastWhy = $"status file reports version {st.Version}, expected {expectedVersion}"; break; }
                        if (st.AuthLatched)
                            return new HealthResult(HealthVerdict.Unhealthy, "app latched an authentication failure (401)");
                        if (st.LastApiOkUtc == null || st.StartedUtc == null || st.LastApiOkUtc < st.StartedUtc)
                        {
                            statusOk = false;
                            lastWhy = st.LastDeniedCode != null
                                ? $"api answered {st.LastDeniedCode} and no call has succeeded"
                                : "no successful api call yet";
                            break;
                        }
                    }
                }

                if (statusOk)
                {
                    if (_utcNow() - stableSince >= opt.StableFor)
                        return new HealthResult(HealthVerdict.Healthy, "stable");
                    lastWhy = "stable so far, waiting out the observation window";
                }
                else
                {
                    stableSince = _utcNow(); // the clock only runs while everything is fine
                }
            }

            if (_utcNow() - started >= opt.Deadline)
            {
                // The app never proved itself. Whether that is the app's fault depends on whether anything can
                // reach the api from this PC at all: if not, the new build is not to blame.
                bool reachable;
                try { reachable = await apiReachable(); } catch { reachable = false; }
                _log.LogWarning("Health check deadline reached: {Why}; api reachable from updater: {Reachable}", lastWhy, reachable);
                return reachable
                    ? new HealthResult(HealthVerdict.Unhealthy, lastWhy)
                    : new HealthResult(HealthVerdict.Inconclusive, "the api is unreachable from this PC, cannot judge the new build");
            }

            await _delay(opt.PollInterval, ct);
        }
    }
}

// True when a print job is in progress or finished within the last `busyFor`. Apps that write no status
// file (the 1.0.0.0 build) give no signal; the quiet window is then the only protection.
public static class BusyGate
{
    public static bool IsBusy(string statusDir, string product, TimeSpan busyFor, DateTime utcNow)
    {
        foreach (var s in AppStatusFile.ReadAll(statusDir, product))
        {
            // A status file nobody has refreshed for a while belongs to a process that is gone.
            if (s.UpdatedUtc == null || utcNow - s.UpdatedUtc > TimeSpan.FromMinutes(5)) continue;
            if (s.Busy) return true;
            if (s.LastJobUtc != null && utcNow - s.LastJobUtc < busyFor) return true;
        }
        return false;
    }
}
