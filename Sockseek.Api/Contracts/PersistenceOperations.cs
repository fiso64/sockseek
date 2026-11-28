namespace Sockseek.Api;

public sealed record PersistenceBackupRequestDto(string? BackupPath = null);
public sealed record PersistenceBackupResultDto(
    string BackupPath,
    long SizeBytes,
    bool IntegrityHealthy,
    string IntegrityResult);
public sealed record PersistenceIntegrityResultDto(bool IsHealthy, string Result);
public sealed record PersistenceCheckpointResultDto(int Busy, int LogFrames, int CheckpointedFrames);
public sealed record PersistenceRetentionResultDto(
    int PrunedJobs,
    int PrunedSearchResults,
    int SearchesMarkedPruned,
    long DurationMilliseconds,
    int PrunedTransfers = 0,
    int PrunedTransferAttempts = 0,
    int PrunedChatMessages = 0);
