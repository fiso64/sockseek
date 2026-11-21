using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.IO;

namespace Tests.Core;

[TestClass]
public sealed class OwnerOnlyFilePermissionsTests
{
    [TestMethod]
    public void SharingStoragePermissions_AreExplicitlyOwnerOnly()
    {
        string parent = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-permissions-{Guid.NewGuid():N}");
        string directory = Path.Combine(parent, "sharing");
        string file = Path.Combine(directory, "catalog.sqlite3");
        Directory.CreateDirectory(parent);

        try
        {
            OwnerOnlyFilePermissions.EnsureDirectory(directory);
            File.WriteAllText(file, "sensitive");
            OwnerOnlyFilePermissions.EnsureFile(file);

            if (OperatingSystem.IsWindows())
            {
                AssertWindowsFilePermissions(file);
            }
            else
            {
                UnixFileMode directoryMode = File.GetUnixFileMode(directory);
                UnixFileMode fileMode = File.GetUnixFileMode(file);
                const UnixFileMode nonOwner =
                    UnixFileMode.GroupRead
                    | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute;

                Assert.AreEqual(0, (int)(directoryMode & nonOwner));
                Assert.AreEqual(0, (int)(fileMode & nonOwner));
            }
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsFilePermissions(string file)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
        FileSecurity security =
            FileSystemAclExtensions.GetAccessControl(new FileInfo(file));
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));

        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.IsTrue(rules.Cast<FileSystemAccessRule>().All(rule =>
            !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Allow
            && owner.Equals(rule.IdentityReference)));
    }
}
