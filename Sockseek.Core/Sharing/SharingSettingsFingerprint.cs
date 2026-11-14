using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

public static class SharingSettingsFingerprint
{
    public static string Compute(SharingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = ShareCatalogVersions.Schema,
            Roots = settings.Roots.Select(root => new
            {
                root.LocalPath,
                Alias = root.EffectiveAlias,
            }),
            Exclusions = settings.ExcludedDirectories,
            Filters = settings.Filters,
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }
}
