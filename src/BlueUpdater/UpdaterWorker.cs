using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlueUpdater;

// The service body: recover an interrupted swap first, then wake every few minutes and give each target a
// turn. Checking the api happens about every CheckIntervalHours (with jitter so a chain of hotels does not
// hit the server together); a staged release waits for the quiet window on the shorter tick.
public sealed class UpdaterWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    private readonly Paths _paths;
    private readonly ILogger<UpdaterWorker> _log;
    private readonly ILoggerFactory _loggers;
    private readonly Random _rng = new();
    private readonly Dictionary<string, DateTime> _nextCheck = new();

    public UpdaterWorker(Paths paths, ILogger<UpdaterWorker> log, ILoggerFactory loggers)
    {
        _paths = paths;
        _log = log;
        _loggers = loggers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("BlueUpdater {Version} starting", Runtime.UpdaterVersion);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let the machine finish booting
            await RunLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.LogCritical(e, "BlueUpdater stopped on an unexpected error");
            throw; // SCM failure actions restart the service
        }
        _log.LogInformation("BlueUpdater stopping");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = UpdaterConfig.Load(_paths);
                var engine = Engine.Create(cfg, _paths, _loggers.CreateLogger("Updater"));
                if (engine == null)
                {
                    _log.LogError("No API key configured; run BlueUpdater.exe --install again");
                }
                else
                {
                    var state = UpdaterState.Load(_paths);
                    if (first)
                    {
                        first = false;
                        await engine.RecoverAsync(cfg, state, ct);
                    }
                    foreach (var target in cfg.Targets)
                    {
                        var ps = state.For(target.Product);
                        var dueAt = _nextCheck.TryGetValue(target.Product, out var d) ? d : DateTime.MinValue;
                        var hasStaged = Directory.Exists(Path.Combine(_paths.Staging, target.Product))
                                        && Directory.EnumerateDirectories(Path.Combine(_paths.Staging, target.Product)).Any();
                        if (!hasStaged && DateTime.UtcNow < dueAt) continue;
                        await engine.Updater.RunAsync(target, state, force: false, ct);
                        _nextCheck[target.Product] = DateTime.UtcNow
                            + TimeSpan.FromHours(cfg.CheckIntervalHours)
                            + TimeSpan.FromMinutes(_rng.Next(0, 31));
                    }
                    state.Save(_paths);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Update cycle failed");
            }

            await Task.Delay(Tick, ct);
        }
    }
}
