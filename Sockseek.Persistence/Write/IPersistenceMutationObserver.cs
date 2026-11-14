namespace Sockseek.Persistence.Write;

/// <summary>
/// Observes the durable outcome of mutation batches. Notifications happen only
/// after the transaction commit is visible, or after the writer has abandoned a
/// batch permanently.
/// </summary>
public interface IPersistenceMutationObserver
{
    void Committed(IReadOnlyList<PersistenceMutation> mutations,
        IReadOnlyList<(Guid TransferId, long Revision)>? supersededTransfers = null);

    // For writer-rejected batches, valid siblings settle before loss is reported:
    // consumers may finish a source from its durable prefix on this notification.
    void PermanentlyFailed(
        IReadOnlyList<PersistenceMutation> mutations,
        Exception exception);
}
