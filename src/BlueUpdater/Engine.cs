using System.Reflection;
using Microsoft.Extensions.Logging;

namespace BlueUpdater;

public static class Runtime
{
    public static string UpdaterVersion { get; } =
        VersionUtil.Normalize(Assembly.GetExecutingAssembly().GetName().Version?.ToString()) ?? "1.0.0.0";
}

// Wires the real collaborators together. Shared by the service and the `--check` command line.
public sealed class Engine
{
    public required TargetUpdater Updater { get; init; }
    public required UpdateApplier Applier { get; init; }
    private Paths DataPaths { get; init; } = null!;

    public static Engine? Create(UpdaterConfig cfg, Paths paths, ILogger log)
    {
        var key = cfg.ApiKey;
        if (string.IsNullOrEmpty(key)) return null;

        var host = new WindowsServiceHost();
        var api = new UpdateApi(cfg.ApiBase, key, Environment.MachineName, Runtime.UpdaterVersion);
        var verifier = new ManifestVerifier(ManifestVerifier.LoadEmbeddedKeys(), Runtime.UpdaterVersion);
        var health = new HealthChecker(host, paths, log);
        var applier = new UpdateApplier(host, paths, health, ct => api.ReachableAsync(ct), log);
        var updater = new TargetUpdater(cfg, paths, host, api, verifier, applier, log, Runtime.UpdaterVersion);
        return new Engine { Updater = updater, Applier = applier, DataPaths = paths };
    }

    // Finish or undo a swap that a crash or power cut interrupted, and record the outcome.
    public async Task RecoverAsync(UpdaterConfig cfg, UpdaterState state, CancellationToken ct)
    {
        var recovered = await Applier.RecoverAsync(cfg, ct);
        if (recovered == null) return;
        var (journal, result) = recovered.Value;
        var target = cfg.Targets.FirstOrDefault(t => t.Product == journal.Product);
        if (target == null) return;
        await Updater.HandleResultAsync(target, state.For(journal.Product), journal.FromVersion, journal.ToVersion, result, ct);
        state.Save(DataPaths);
    }
}
