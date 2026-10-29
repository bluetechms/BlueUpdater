using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BlueUpdater;

// What the updater needs to know about one NSSM-wrapped service. Read from
// HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters, never assumed.
public sealed record ServiceInfo(string Name, string Application, string AppDirectory, string? RunAs)
{
    public string InstallDir => Path.GetDirectoryName(Application) ?? AppDirectory;
}

// SCM start type as found in the registry (Start: 2 auto, 3 manual, 4 disabled; DelayedAutostart 0/1).
public sealed record StartType(int Start, int Delayed);

public enum ServiceState { Missing, Stopped, Running, Transitioning }

// Everything the updater does to the machine goes through this, so the swap/rollback logic can be tested
// against a fake without admin rights or a real service.
public interface IServiceHost
{
    ServiceInfo? Discover(string serviceName);
    ServiceState GetState(string serviceName);
    void Stop(string serviceName, TimeSpan timeout);
    void Start(string serviceName, TimeSpan timeout);
    StartType GetStartType(string serviceName);
    void SetStartType(string serviceName, StartType type);
    // Live processes whose executable is exactly this path, in any session.
    IReadOnlyList<int> ProcessIdsForImage(string imagePath);
}

public sealed partial class WindowsServiceHost : IServiceHost
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9_.\-]{1,80}$", RegexOptions.Compiled);

    private static string ServiceKey(string name)
    {
        if (!SafeName.IsMatch(name)) throw new ArgumentException("unsafe service name", nameof(name));
        return @"SYSTEM\CurrentControlSet\Services\" + name;
    }

    public ServiceInfo? Discover(string serviceName)
    {
        using var svc = Registry.LocalMachine.OpenSubKey(ServiceKey(serviceName));
        if (svc == null) return null;
        using var p = svc.OpenSubKey("Parameters");
        var app = p?.GetValue("Application") as string;
        if (string.IsNullOrWhiteSpace(app)) return null;
        app = Environment.ExpandEnvironmentVariables(app.Trim().Trim('"'));
        var dir = p?.GetValue("AppDirectory") as string;
        dir = string.IsNullOrWhiteSpace(dir) ? Path.GetDirectoryName(app) ?? "" : Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
        return new ServiceInfo(serviceName, app, dir, svc.GetValue("ObjectName") as string);
    }

    public ServiceState GetState(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.Running => ServiceState.Running,
                _ => ServiceState.Transitioning
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.Missing;
        }
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        using var sc = new ServiceController(serviceName);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public StartType GetStartType(string serviceName)
    {
        using var svc = Registry.LocalMachine.OpenSubKey(ServiceKey(serviceName)) ?? throw new InvalidOperationException("service not found");
        var start = Convert.ToInt32(svc.GetValue("Start", 2));
        var delayed = Convert.ToInt32(svc.GetValue("DelayedAutostart", 0));
        return new StartType(start, delayed);
    }

    // sc.exe rather than writing the registry: the SCM caches the configuration and only sc/ChangeServiceConfig
    // updates it consistently. Arguments are fixed strings plus a validated service name.
    public void SetStartType(string serviceName, StartType type)
    {
        if (!SafeName.IsMatch(serviceName)) throw new ArgumentException("unsafe service name", nameof(serviceName));
        var mode = type.Start switch
        {
            4 => "disabled",
            3 => "demand",
            _ => type.Delayed == 1 ? "delayed-auto" : "auto"
        };
        RunSc("config", serviceName, "start=", mode);
    }

    public IReadOnlyList<int> ProcessIdsForImage(string imagePath)
    {
        var result = new List<int>();
        var name = Path.GetFileNameWithoutExtension(imagePath);
        foreach (var proc in Process.GetProcessesByName(name))
        {
            using (proc)
            {
                var image = QueryImage(proc.Id);
                // Unknown image (access denied) is treated as a match: better to defer an update than to
                // overwrite a binary that might be running.
                if (image == null || string.Equals(image, imagePath, StringComparison.OrdinalIgnoreCase))
                    result.Add(proc.Id);
            }
        }
        return result;
    }

    public static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start sc.exe");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc.exe {string.Join(' ', args)} failed ({p.ExitCode}): {output.Trim()}");
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    private static string? QueryImage(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
