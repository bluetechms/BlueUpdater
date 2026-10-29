using BlueUpdater;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// BlueUpdater.exe                    run as the Windows service (also what SCM starts, with --service)
// BlueUpdater.exe --install ...      one-time setup, elevated (see Installer.cs)
// BlueUpdater.exe --uninstall        remove the service
// BlueUpdater.exe --check [--force]  one update pass from the command line (bench testing)
// BlueUpdater.exe --status           show configuration and the last known state
// BlueUpdater.exe --version

if (args.Contains("--version"))
{
    Console.WriteLine(Runtime.UpdaterVersion);
    return 0;
}
if (args.Contains("--install")) return Installer.Install(args);
if (args.Contains("--uninstall")) return Installer.Uninstall();
if (args.Contains("--status")) return Cli.Status();
if (args.Contains("--check")) return await Cli.CheckAsync(args.Contains("--force"));

var paths = new Paths();
paths.EnsureDirs();

// No command-line args to the host: a service started by SCM has System32 as its working directory and `--service`
// is ours, not configuration.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = Installer.ServiceName);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(paths.Logs, "blueupdater"));
builder.Services.AddSingleton(paths);
builder.Services.AddHostedService<UpdaterWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

await builder.Build().RunAsync();
return 0;

internal static class Cli
{
    public static int Status()
    {
        var paths = new Paths();
        Console.WriteLine($"BlueUpdater {Runtime.UpdaterVersion}");
        if (!File.Exists(paths.ConfigFile))
        {
            Console.WriteLine("Not installed (no updater.json).");
            return 1;
        }
        var cfg = UpdaterConfig.Load(paths);
        Console.WriteLine($"Api: {cfg.ApiBase}  key: {(cfg.ApiKey == null ? "MISSING/unreadable" : "set")}");
        Console.WriteLine($"Quiet window (local hours): {(cfg.WindowStartHour == cfg.WindowEndHour ? "any time" : cfg.WindowStartHour + "-" + cfg.WindowEndHour)}");
        var host = Engine.BuildHost(cfg);
        var state = UpdaterState.Load(paths);
        foreach (var t in cfg.Targets)
        {
            var ps = state.For(t.Product);
            Console.WriteLine($"- {t.Product}  failed version: {ps.FailedVersion ?? "none"}  last status: {ps.LastReportedStatus ?? "-"}");
            foreach (var s in t.EffectiveServices())
            {
                var info = host.Discover(s);
                string v = "?";
                try { if (info != null) v = System.Diagnostics.FileVersionInfo.GetVersionInfo(info.Application).FileVersion ?? "?"; } catch { }
                Console.WriteLine($"    {s}: {(info == null ? "not found" : info.Application)}  version {v}  state {host.GetState(s)}");
            }
        }
        Console.WriteLine(File.Exists(paths.JournalFile) ? "An interrupted update is pending recovery (swap.state present)." : "No update in progress.");
        return 0;
    }

    // One pass on the command line. --force skips the quiet window and busy check.
    public static async Task<int> CheckAsync(bool force)
    {
        var paths = new Paths();
        paths.EnsureDirs();
        var cfg = UpdaterConfig.Load(paths);
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new FileLoggerProvider(paths.Logs, "blueupdater")).AddSimpleConsole());
        var engine = Engine.Create(cfg, paths, loggers.CreateLogger("Updater"));
        if (engine == null)
        {
            Console.Error.WriteLine("No API key configured.");
            return 1;
        }
        var state = UpdaterState.Load(paths);
        await engine.RecoverAsync(cfg, state, CancellationToken.None);
        foreach (var t in cfg.Targets) await engine.Updater.RunAsync(t, state, force, CancellationToken.None);
        state.Save(paths);
        return 0;
    }
}
