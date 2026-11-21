namespace Sockseek.Core.Transfers;

// Opaque new row identities retain insertion locality in durable history indexes.
public static class TransferIds
{
    public static Guid New()
        => Guid.CreateVersion7();
}

public static class TransferAttemptIds
{
    public static Guid New()
        => Guid.CreateVersion7();
}
