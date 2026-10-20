using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteConnect.HostService;

/// <summary>
/// The service starts the agent as LocalSystem, so whoever can replace a file in the install folder could run code as
/// LocalSystem. Before every launch the folder and all files in it are checked: only SYSTEM, Administrators and
/// TrustedInstaller may own them or hold write-type rights. If anything else can, the agent is not started (a per-user
/// folder such as %LOCALAPPDATA% or a developer's build output always fails this check).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallLocationCheck
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Null when the folder is safe to launch from; otherwise the first problem found.</summary>
    public static string? FindProblem(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return $"{directory} does not exist.";
        }

        var problem = Check(new DirectoryInfo(directory).GetAccessControl(), directory, isDirectory: true);
        if (problem is not null)
        {
            return problem;
        }

        foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            problem = Check(new DirectoryInfo(dir).GetAccessControl(), dir, isDirectory: true);
            if (problem is not null)
            {
                return problem;
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            problem = Check(new FileInfo(file).GetAccessControl(), file, isDirectory: false);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    internal static string? Check(FileSystemSecurity security, string path, bool isDirectory)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner))
        {
            return $"{path} is owned by {Describe(owner)}; only SYSTEM, Administrators or TrustedInstaller may own the install folder.";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0)
            {
                continue;
            }

            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (IsTrusted(sid))
            {
                continue;
            }

            // "CREATOR OWNER" on a folder applies only to the owners of new children, which are checked themselves.
            if (isDirectory && sid == CreatorOwner && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            return $"{path} can be modified by {Describe(sid)}.";
        }

        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => sid == LocalSystem || sid == Administrators || sid == TrustedInstaller;

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return $"{sid.Translate(typeof(NTAccount))} ({sid})";
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
