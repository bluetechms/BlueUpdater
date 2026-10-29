namespace BlueUpdater;

// A desktop app (BlueMicroTik) is not a Windows service: there is nothing to stop or start. Its "service" is a
// virtual name ("gui:<product>") mapped to an exe path from updater.json. The one rule that matters is kept:
// the files may only be replaced while no copy of the exe is running, in any session. Stop therefore fails
// (and the update is deferred) while the app is open; Start is a no-op because the next user launch is the start.
public sealed class GuiHost : IServiceHost
{
    public const string Prefix = "gui:";

    private readonly IServiceHost _inner;
    private readonly Dictionary<string, string> _apps;

    public GuiHost(IServiceHost inner, IEnumerable<TargetConfig> targets)
    {
        _inner = inner;
        _apps = targets.Where(t => !string.IsNullOrEmpty(t.GuiExe))
            .ToDictionary(t => NameFor(t.Product), t => t.GuiExe!, StringComparer.OrdinalIgnoreCase);
    }

    public static string NameFor(string product) => Prefix + product;

    public static bool Owns(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public ServiceInfo? Discover(string serviceName)
    {
        if (!_apps.TryGetValue(serviceName, out var exe)) return null;
        return new ServiceInfo(serviceName, exe, Path.GetDirectoryName(exe) ?? "", null, Gui: true);
    }

    public ServiceState GetState(string serviceName) =>
        _apps.TryGetValue(serviceName, out var exe)
            ? (_inner.ProcessIdsForImage(exe).Count > 0 ? ServiceState.Running : ServiceState.Stopped)
            : ServiceState.Missing;

    public void Stop(string serviceName, TimeSpan timeout)
    {
        if (_apps.TryGetValue(serviceName, out var exe) && _inner.ProcessIdsForImage(exe).Count > 0)
            throw new InvalidOperationException("the app is open; it will be updated when it is closed");
    }

    public void Start(string serviceName, TimeSpan timeout) { }

    public StartType GetStartType(string serviceName) => new(3, 0);

    public void SetStartType(string serviceName, StartType type) { }

    public IReadOnlyList<int> ProcessIdsForImage(string imagePath) => _inner.ProcessIdsForImage(imagePath);
}

// Routes "gui:" names to the GuiHost and everything else to the real service manager.
public sealed class CompositeServiceHost : IServiceHost
{
    private readonly IServiceHost _services;
    private readonly GuiHost _gui;

    public CompositeServiceHost(IServiceHost services, GuiHost gui)
    {
        _services = services;
        _gui = gui;
    }

    private IServiceHost For(string name) => GuiHost.Owns(name) ? _gui : _services;

    public ServiceInfo? Discover(string serviceName) => For(serviceName).Discover(serviceName);
    public ServiceState GetState(string serviceName) => For(serviceName).GetState(serviceName);
    public void Stop(string serviceName, TimeSpan timeout) => For(serviceName).Stop(serviceName, timeout);
    public void Start(string serviceName, TimeSpan timeout) => For(serviceName).Start(serviceName, timeout);
    public StartType GetStartType(string serviceName) => For(serviceName).GetStartType(serviceName);
    public void SetStartType(string serviceName, StartType type) => For(serviceName).SetStartType(serviceName, type);
    public IReadOnlyList<int> ProcessIdsForImage(string imagePath) => _services.ProcessIdsForImage(imagePath);
}
