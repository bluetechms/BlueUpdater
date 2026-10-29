using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueUpdater;

// One thing the updater keeps current: a product (as named by the api) running as one or more NSSM services.
public sealed class TargetConfig
{
    public string Product { get; set; } = "";
    public List<string> Services { get; set; } = new();
}

// %ProgramData%\BlueHMS\updater\updater.json. The folder is SYSTEM/Administrators only. The api key is
// DPAPI-protected (machine scope) so a copy of the file is useless on another PC.
public sealed class UpdaterConfig
{
    public string ApiBase { get; set; } = "https://api.bluehms.com/api";
    public string? ApiKeyProtected { get; set; }
    public List<TargetConfig> Targets { get; set; } = new();

    // Local hour range [WindowStartHour, WindowEndHour) in which a swap may start. Equal values = any time.
    public int WindowStartHour { get; set; } = 3;
    public int WindowEndHour { get; set; } = 6;

    // A job within this many minutes of "now" defers the swap (night room-service prints are real).
    public int BusyMinutes { get; set; } = 10;
    public int CheckIntervalHours { get; set; } = 6;
    public int HealthStableMinutes { get; set; } = 3;
    public int HealthDeadlineMinutes { get; set; } = 15;

    // Install folders that are writable by ordinary users are refused unless a technician opted in.
    public bool AllowWritableInstallDir { get; set; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlueUpdater.v1");

    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(ApiKeyProtected)) return null;
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), Entropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return null;
            }
        }
        set => ApiKeyProtected = string.IsNullOrEmpty(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static UpdaterConfig Load(Paths paths)
    {
        var text = File.ReadAllText(paths.ConfigFile);
        return JsonSerializer.Deserialize<UpdaterConfig>(text) ?? throw new InvalidDataException("updater.json is empty");
    }

    public void Save(Paths paths)
    {
        Directory.CreateDirectory(paths.Updater);
        AtomicFile.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(this, Json));
    }

    public bool InWindow(DateTime localNow)
    {
        if (WindowStartHour == WindowEndHour) return true;
        var h = localNow.Hour;
        return WindowStartHour < WindowEndHour
            ? h >= WindowStartHour && h < WindowEndHour
            : h >= WindowStartHour || h < WindowEndHour;
    }
}

// Per-product memory that must survive restarts: which version failed (never retried until a higher one
// is published) and when we last checked / reported.
public sealed class ProductState
{
    public string? FailedVersion { get; set; }
    public string? LastReportedStatus { get; set; }
    public DateTime? LastReportUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
}

public sealed class UpdaterState
{
    public Dictionary<string, ProductState> Products { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ProductState For(string product)
    {
        if (!Products.TryGetValue(product, out var s)) Products[product] = s = new ProductState();
        return s;
    }

    public static UpdaterState Load(Paths paths)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(paths.StateFile)) ?? new UpdaterState();
        }
        catch
        {
            return new UpdaterState();
        }
    }

    public void Save(Paths paths) => AtomicFile.WriteAllText(paths.StateFile, JsonSerializer.Serialize(this, Json));
}

public static class AtomicFile
{
    // Write to a sibling temp file and replace, so a power cut never leaves a half-written journal or config.
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}
