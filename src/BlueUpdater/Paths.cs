namespace BlueUpdater;

// Everything lives under %ProgramData%\BlueHMS. `updater\` is SYSTEM/Administrators only; `status\` and
// `logs\` are shared with the apps (the app account gets Modify there at install time).
public sealed class Paths
{
    public string Root { get; }
    public string Status => System.IO.Path.Combine(Root, "status");
    public string Logs => System.IO.Path.Combine(Root, "logs");
    public string Updater => System.IO.Path.Combine(Root, "updater");
    public string ConfigFile => System.IO.Path.Combine(Updater, "updater.json");
    public string StateFile => System.IO.Path.Combine(Updater, "state.json");
    public string JournalFile => System.IO.Path.Combine(Updater, "swap.state");
    public string Staging => System.IO.Path.Combine(Updater, "staging");
    public string Rollback => System.IO.Path.Combine(Updater, "rollback");

    public Paths(string? root = null)
    {
        Root = root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlueHMS");
    }

    public void EnsureDirs()
    {
        foreach (var d in new[] { Root, Status, Logs, Updater, Staging, Rollback })
            Directory.CreateDirectory(d);
    }
}
