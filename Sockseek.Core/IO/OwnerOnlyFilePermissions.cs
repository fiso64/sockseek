using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Sockseek.Core.IO;

/// <summary>
/// Applies an explicit daemon-owner-only boundary to sensitive local state.
/// Sharing catalogs contain absolute roots and complete peer-visible listings,
/// so relying on an ambient umask or inherited broad ACL is insufficient.
/// </summary>
public static class OwnerOnlyFilePermissions
{
    public static void EnsureDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);

        if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier owner = CurrentWindowsOwner();
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(fullPath),
                security);
            return;
        }

        File.SetUnixFileMode(
            fullPath,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);
    }

    public static void EnsureFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Sensitive file does not exist.", fullPath);

        if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier owner = CurrentWindowsOwner();
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(fullPath), security);
            return;
        }

        File.SetUnixFileMode(
            fullPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsOwner()
        => WindowsIdentity.GetCurrent().User
           ?? throw new InvalidOperationException(
               "The current Windows account has no security identifier.");
}
