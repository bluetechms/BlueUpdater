using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace BlueUpdater;

// `BlueUpdater.exe --install ...` and `--uninstall`: run once, elevated, with a technician at the PC.
public static class Installer
{
    public const string ServiceName = "BlueUpdater";
    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlueHMS", "Updater");

    public static int Install(string[] args)
    {
        if (!Acl.IsElevated())
        {
            Console.Error.WriteLine("Run this from an elevated (Administrator) prompt.");
            return 2;
        }

        var apiKey = Arg(args, "--api-key");
        var targetArgs = Args(args, "--target");
        var guiArgs = Args(args, "--gui-target");
        if (string.IsNullOrWhiteSpace(apiKey) || (targetArgs.Count == 0 && guiArgs.Count == 0))
        {
            Console.Error.WriteLine("Usage: BlueUpdater.exe --install --api-key <service key> --target <product>:<Service>[,<Service>...] [--target ...]");
            Console.Error.WriteLine("                       [--api-base https://api.bluehms.com/api] [--window 3-6|any] [--no-restart]");
            Console.Error.WriteLine("Example: --target printing-outlet:BluePrinting");
            Console.Error.WriteLine("Desktop app (not a service): --gui-target bluemicrotik=\"C:\\Program Files\\BlueMicroTik\\BlueMicroTik.exe\"");
            return 2;
        }

        var host = new WindowsServiceHost();
        var targets = new List<TargetConfig>();
        foreach (var t in targetArgs)
        {
            var parts = t.Split(':', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                Console.Error.WriteLine($"Bad --target '{t}', expected product:Service[,Service]");
                return 2;
            }
            targets.Add(new TargetConfig { Product = parts[0], Services = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() });
        }

        foreach (var g in guiArgs)
        {
            var parts = g.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || !Path.IsPathRooted(parts[1]) || !File.Exists(parts[1]))
            {
                Console.Error.WriteLine($"Bad --gui-target '{g}': expected product=<full path of an existing exe> (install the app first).");
                return 2;
            }
            targets.Add(new TargetConfig { Product = parts[0], GuiExe = parts[1] });
            Console.WriteLine($"  {parts[0]}: desktop app {parts[1]}");
            var dir = Path.GetDirectoryName(parts[1])!;
            if (Acl.IsWritableByBroadGroups(dir))
                Console.WriteLine($"  WARNING: '{dir}' can be modified by ordinary users. Updates to it will be REFUSED until its permissions are tightened.");
        }

        // Discover before changing anything, so a typo in a service name fails early.
        var infos = new List<ServiceInfo>();
        foreach (var name in targets.SelectMany(t => t.Services))
        {
            var info = host.Discover(name);
            if (info == null)
            {
                Console.Error.WriteLine($"Service '{name}' was not found or is not an NSSM service (no Parameters\\Application).");
                return 3;
            }
            infos.Add(info);
            Console.WriteLine($"  {name}: {info.Application}  (AppDirectory {info.AppDirectory}, runs as {info.RunAs ?? "?"})");
            if (!string.Equals(Path.GetFullPath(info.AppDirectory).TrimEnd('\\'), Path.GetFullPath(info.InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  note: AppDirectory differs from the exe folder; updates replace files in {info.InstallDir}");
        }

        var cfg = new UpdaterConfig { ApiKey = apiKey, Targets = targets };
        var apiBase = Arg(args, "--api-base");
        if (!string.IsNullOrEmpty(apiBase)) cfg.ApiBase = apiBase;
        var window = Arg(args, "--window");
        if (window != null)
        {
            if (window.Equals("any", StringComparison.OrdinalIgnoreCase)) { cfg.WindowStartHour = 0; cfg.WindowEndHour = 0; }
            else
            {
                var w = window.Split('-');
                if (w.Length != 2 || !int.TryParse(w[0], out var a) || !int.TryParse(w[1], out var b) || a is < 0 or > 23 || b is < 0 or > 24)
                {
                    Console.Error.WriteLine("--window expects e.g. 3-6 (local hours) or 'any'");
                    return 2;
                }
                cfg.WindowStartHour = a; cfg.WindowEndHour = b % 24;
            }
        }

        // 1. Data folders and permissions.
        var paths = new Paths();
        paths.EnsureDirs();
        var appAccounts = infos.Select(i => Acl.Resolve(i.RunAs)).Where(s => s != null).Cast<SecurityIdentifier>().ToList();
        Acl.SecureDataRoot(paths, appAccounts);
        Console.WriteLine($"Data folder secured: {paths.Root}");

        // 2. The install folders must not be modifiable by ordinary users.
        foreach (var dir in infos.Select(i => i.InstallDir).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Acl.IsWritableByBroadGroups(dir))
                Console.WriteLine($"  WARNING: '{dir}' can be modified by ordinary users. Updates to it will be REFUSED until its permissions are tightened.");
        }

        // 3. NSSM only reads its stop timeouts when it starts the app, so set the console stop wait (60 s,
        //    long enough to finish a print) and restart the services once.
        foreach (var info in infos)
        {
            using var p = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\{info.Name}\Parameters");
            p.SetValue("AppStopMethodConsole", 60000, RegistryValueKind.DWord);
        }
        Console.WriteLine("NSSM console-stop wait set to 60 s.");
        if (!args.Contains("--no-restart"))
        {
            foreach (var info in infos)
            {
                Console.WriteLine($"Restarting {info.Name} so the new stop timeout applies...");
                try
                {
                    host.Stop(info.Name, TimeSpan.FromSeconds(150));
                    host.Start(info.Name, TimeSpan.FromSeconds(60));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  could not restart {info.Name}: {e.Message} (restart it manually)");
                }
            }
        }

        // 4. Our own copy, outside any user-writable location, then the service.
        Directory.CreateDirectory(InstallDir);
        var self = Environment.ProcessPath!;
        var dest = Path.Combine(InstallDir, "BlueUpdater.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            if (host.GetState(ServiceName) != ServiceState.Missing) host.Stop(ServiceName, TimeSpan.FromSeconds(60));
            File.Copy(self, dest, true);
        }

        cfg.Save(paths);

        if (host.GetState(ServiceName) == ServiceState.Missing)
        {
            WindowsServiceHost.RunSc("create", ServiceName, "binPath=", $"\"{dest}\" --service", "start=", "delayed-auto",
                "obj=", "LocalSystem", "DisplayName=", "BlueHMS Updater");
            WindowsServiceHost.RunSc("description", ServiceName, "Keeps BlueHMS hotel-side programs up to date.");
        }
        WindowsServiceHost.RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        host.Start(ServiceName, TimeSpan.FromSeconds(60));
        Console.WriteLine($"Service '{ServiceName}' installed and started. Log: {paths.Logs}");
        return 0;
    }

    public static int Uninstall()
    {
        if (!Acl.IsElevated())
        {
            Console.Error.WriteLine("Run this from an elevated (Administrator) prompt.");
            return 2;
        }
        var host = new WindowsServiceHost();
        if (host.GetState(ServiceName) != ServiceState.Missing)
        {
            try { host.Stop(ServiceName, TimeSpan.FromSeconds(60)); } catch { /* already stopped */ }
            WindowsServiceHost.RunSc("delete", ServiceName);
        }
        Console.WriteLine("Service removed. Data in %ProgramData%\\BlueHMS and the program folder were left in place.");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Args(string[] args, string name)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) list.Add(args[i + 1]);
        return list;
    }
}
