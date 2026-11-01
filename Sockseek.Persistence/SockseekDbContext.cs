using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence;

public sealed class SockseekDbContext(DbContextOptions<SockseekDbContext> options) : DbContext(options)
{
    internal DbSet<RuntimeSessionEntity> RuntimeSessions => Set<RuntimeSessionEntity>();
    internal DbSet<JobEntity> Jobs => Set<JobEntity>();
    internal DbSet<SubmissionEntity> Submissions => Set<SubmissionEntity>();
    internal DbSet<SearchJobEntity> SearchJobs => Set<SearchJobEntity>();
    internal DbSet<SearchResultEntity> SearchResults => Set<SearchResultEntity>();
    internal DbSet<TransferEntity> Transfers => Set<TransferEntity>();
    internal DbSet<TransferAttemptEntity> TransferAttempts => Set<TransferAttemptEntity>();
    internal DbSet<TransferAccountingCheckpointEntity> TransferAccountingCheckpoints => Set<TransferAccountingCheckpointEntity>();
    internal DbSet<TransferByteBucketEntity> TransferByteBuckets => Set<TransferByteBucketEntity>();
    internal DbSet<TransferAccountingStateEntity> TransferAccountingStates => Set<TransferAccountingStateEntity>();
    internal DbSet<PeerRestrictionOverrideEntity> PeerRestrictionOverrides => Set<PeerRestrictionOverrideEntity>();
    internal DbSet<InputArtifactEntity> InputArtifacts => Set<InputArtifactEntity>();
    internal DbSet<ChatConversationEntity> ChatConversations => Set<ChatConversationEntity>();
    internal DbSet<ChatSequenceEntity> ChatSequences => Set<ChatSequenceEntity>();
    internal DbSet<ChatRoomSubscriptionEntity> ChatRoomSubscriptions => Set<ChatRoomSubscriptionEntity>();
    internal DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    internal DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(SockseekDbContext).Assembly);
}
