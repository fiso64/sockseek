using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

internal static class RetainedJobFixture
{
    public static PersistedJob Create(string kind, JobSnapshotPayload? payload = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new(Guid.NewGuid(), 1, Guid.NewGuid(), null, null, null,
            kind, "Terminal", "None", null, "Succeeded", "None", "None", "None",
            null, false, null, kind, now, now, now, 1, RetainedJobPayloadCodec.SchemaVersion,
            payload == null ? null : RetainedJobPayloadCodec.Capture(payload).Value);
    }
}
