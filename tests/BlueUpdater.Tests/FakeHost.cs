using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueUpdater;

namespace BlueUpdater.Tests;

// A pretend SCM + pretend app. The "app" is whatever bytes sit in Application: a file containing
// "v1.1.0.0 ok" behaves like a healthy 1.1.0.0, "v1.2.0.0 deny" like a build whose key is refused (403),
// "v1.3.0.0 crash" restarts forever, "v1.4.0.0 latch" latches a 401, "cycle" finished a cycle with no api call (BluePunch with nothing to upload), "idle" proves nothing. Anything else is an old build that
// writes no status file. Like the real SCM, a Disabled service cannot be started.
public sealed class FakeHost : IServiceHost
{
    public sealed class Svc
    {
        public ServiceInfo Info = null!;
        public ServiceState State = ServiceState.Running;
        public StartType Start = new(2, 1);
        public int Pid;
    }

    private static readonly Regex Behaviour = new(@"v(\d+\.\d+\.\d+\.\d+) (ok|deny|crash|latch|cycle|idle)", RegexOptions.Compiled);

    public Dictionary<string, Svc> Services { get; } = new();
    public Dictionary<string, List<int>> ExternalProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailStop { get; set; }
    public List<string> Log { get; } = new();
    private int _nextPid = 1000;

    private readonly Paths _paths;
    private readonly FakeClock _clock;

    public FakeHost(Paths paths, FakeClock clock)
    {
        _paths = paths;
        _clock = clock;
    }

    public Svc Add(string name, string installDir, string exeName = "App.exe")
    {
        var s = new Svc { Info = new ServiceInfo(name, Path.Combine(installDir, exeName), installDir, "LocalSystem") };
        Services[name] = s;
        s.Pid = _nextPid++;
        WriteStatus(s);
        return s;
    }

    public ServiceInfo? Discover(string serviceName) => Services.TryGetValue(serviceName, out var s) ? s.Info : null;

    public ServiceState GetState(string serviceName) => Services.TryGetValue(serviceName, out var s) ? s.State : ServiceState.Missing;

    public void Stop(string serviceName, TimeSpan timeout)
    {
        Log.Add("stop " + serviceName);
        if (FailStop) throw new System.ServiceProcess.TimeoutException("stop timed out");
        var s = Services[serviceName];
        s.State = ServiceState.Stopped;
        s.Pid = 0;
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        Log.Add("start " + serviceName);
        var s = Services[serviceName];
        if (s.Start.Start == 4) throw new InvalidOperationException("the service is disabled and cannot be started");
        if (s.State == ServiceState.Running) return;
        s.State = ServiceState.Running;
        s.Pid = _nextPid++;
        WriteStatus(s);
    }

    public StartType GetStartType(string serviceName) => Services[serviceName].Start;

    public void SetStartType(string serviceName, StartType type)
    {
        Log.Add($"starttype {serviceName} {type.Start}/{type.Delayed}");
        Services[serviceName].Start = type;
    }

    public IReadOnlyList<int> ProcessIdsForImage(string imagePath)
    {
        var pids = new List<int>();
        foreach (var s in Services.Values.Where(v => v.State == ServiceState.Running && string.Equals(v.Info.Application, imagePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (CurrentMode(s)?.mode == "crash") s.Pid = _nextPid++; // a new process every time we look
            pids.Add(s.Pid);
        }
        if (ExternalProcesses.TryGetValue(imagePath, out var ext)) pids.AddRange(ext);
        return pids;
    }

    private (string version, string mode)? CurrentMode(Svc s)
    {
        if (!File.Exists(s.Info.Application)) return null;
        var m = Behaviour.Match(Encoding.Latin1.GetString(File.ReadAllBytes(s.Info.Application)));
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    // What the real app's AppStatus would have written once it is up.
    private void WriteStatus(Svc s)
    {
        var cur = CurrentMode(s);
        if (cur == null) return; // old build: no status file
        var now = _clock.Now;
        var doc = new Dictionary<string, object?>
        {
            ["product"] = "printing-outlet",
            ["version"] = cur.Value.version,
            ["pid"] = s.Pid,
            ["startedUtc"] = now,
            ["updatedUtc"] = now,
            ["outlet"] = s.Info.Name,
            ["lastApiOkUtc"] = cur.Value.mode == "ok" ? now : null,
            ["lastCycleOkUtc"] = cur.Value.mode == "cycle" ? now : null,
            ["lastDeniedCode"] = cur.Value.mode == "deny" ? 403 : cur.Value.mode == "latch" ? 401 : null,
            ["authLatched"] = cur.Value.mode == "latch",
            ["busy"] = false
        };
        Directory.CreateDirectory(_paths.Status);
        File.WriteAllText(Path.Combine(_paths.Status, $"printing-outlet_{s.Info.Name}.json"), JsonSerializer.Serialize(doc));
    }

    public void WriteBusyStatus(string service, bool busy, DateTime? lastJob = null)
    {
        var s = Services[service];
        var doc = new Dictionary<string, object?>
        {
            ["product"] = "printing-outlet",
            ["version"] = "1.0.0.0",
            ["pid"] = s.Pid,
            ["startedUtc"] = _clock.Now.AddHours(-1),
            ["updatedUtc"] = _clock.Now,
            ["busy"] = busy,
            ["lastJobUtc"] = lastJob
        };
        Directory.CreateDirectory(_paths.Status);
        File.WriteAllText(Path.Combine(_paths.Status, $"printing-outlet_{service}.json"), JsonSerializer.Serialize(doc));
    }
}
