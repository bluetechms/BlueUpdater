using System.Security.AccessControl;
using System.Security.Principal;

namespace BlueUpdater;

// Directory permissions: the updater runs as SYSTEM and writes into the app's install folder, so that
// folder must not be modifiable by ordinary users, and the updater's own data must not be readable or
// writable by them either.
public static class Acl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier[] Broad =
    {
        new(WellKnownSidType.BuiltinUsersSid, null),
        new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.WorldSid, null),
        new(WellKnownSidType.InteractiveSid, null)
    };

    private const FileSystemRights WriteMask =
        FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.AppendData |
        FileSystemRights.CreateDirectories | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // True when Users / Authenticated Users / Everyone / Interactive can create, modify or delete in `dir`.
    public static bool IsWritableByBroadGroups(string dir)
    {
        var rules = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (!Broad.Contains((SecurityIdentifier)r.IdentityReference)) continue;
            if ((r.FileSystemRights & WriteMask) != 0) return true;
        }
        return false;
    }

    private static DirectorySecurity Protected(IEnumerable<(SecurityIdentifier sid, FileSystemRights rights)> grants)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false); // no inherited rules
        foreach (var (sid, rights) in grants)
            sec.AddAccessRule(new FileSystemAccessRule(sid, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    public static SecurityIdentifier? Resolve(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        var a = account.Trim();
        if (a.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) return null; // already SYSTEM
        if (a.StartsWith(@".\")) a = Environment.MachineName + a.Substring(1);
        try
        {
            return (SecurityIdentifier)new NTAccount(a).Translate(typeof(SecurityIdentifier));
        }
        catch
        {
            return null;
        }
    }

    // appAccounts: the accounts the NSSM services run as (empty/LocalSystem needs nothing extra).
    public static void SecureDataRoot(Paths paths, IEnumerable<SecurityIdentifier> appAccounts)
    {
        var apps = appAccounts.Distinct().ToList();
        var baseGrants = new List<(SecurityIdentifier, FileSystemRights)> { (System, FileSystemRights.FullControl), (Admins, FileSystemRights.FullControl) };

        // Root: apps may list and read (to reach status\ and logs\), nothing more.
        var rootGrants = baseGrants.Concat(apps.Select(a => (a, FileSystemRights.ReadAndExecute))).ToList();
        new DirectoryInfo(paths.Root).SetAccessControl(Protected(rootGrants));

        // The updater's own data: SYSTEM and Administrators only.
        new DirectoryInfo(paths.Updater).SetAccessControl(Protected(baseGrants));

        // Shared folders: apps write their status and logs here.
        var sharedGrants = baseGrants.Concat(apps.Select(a => (a, FileSystemRights.Modify))).ToList();
        new DirectoryInfo(paths.Status).SetAccessControl(Protected(sharedGrants));
        new DirectoryInfo(paths.Logs).SetAccessControl(Protected(sharedGrants));
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
