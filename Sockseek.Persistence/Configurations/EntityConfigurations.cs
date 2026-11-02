using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence.Configurations;

internal sealed class RuntimeSessionConfiguration : IEntityTypeConfiguration<RuntimeSessionEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeSessionEntity> builder)
    {
        builder.ToTable("runtime_sessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.StoppedAtUtc).HasColumnName("stopped_at_utc");
        builder.Property(x => x.ShutdownKind).HasColumnName("shutdown_kind").HasMaxLength(32);
        builder.Property(x => x.Version).HasColumnName("version").HasMaxLength(64);
        builder.HasIndex(x => x.StartedAtUtc);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<JobEntity>
{
    public void Configure(EntityTypeBuilder<JobEntity> builder)
    {
        builder.ToTable("jobs", table =>
        {
            table.HasCheckConstraint("ck_jobs_revision", "revision >= 0");
            table.HasCheckConstraint("ck_jobs_last_sequence", "last_sequence >= 0");
            table.HasCheckConstraint("ck_jobs_display_id", "display_id > 0");
            table.HasCheckConstraint("ck_jobs_terminal_time", "(lifecycle_state = 'Terminal' AND completed_at_utc IS NOT NULL) OR (lifecycle_state <> 'Terminal' AND completed_at_utc IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.SemanticRole).HasColumnName("semantic_role").HasMaxLength(32);
        builder.Property(x => x.ParentJobId).HasColumnName("parent_job_id");
        builder.Property(x => x.SourceJobId).HasColumnName("source_job_id");
        builder.Property(x => x.ResultJobId).HasColumnName("result_job_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.DisplayId).HasColumnName("display_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(48);
        builder.Property(x => x.LifecycleState).HasColumnName("lifecycle_state").HasMaxLength(32);
        builder.Property(x => x.ActivityPhase).HasColumnName("activity_phase").HasMaxLength(48);
        builder.Property(x => x.ActivityUntilUtc).HasColumnName("activity_until_utc");
        builder.Property(x => x.TerminalOutcome).HasColumnName("terminal_outcome").HasMaxLength(32);
        builder.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(48);
        builder.Property(x => x.CancellationSource).HasColumnName("cancellation_source").HasMaxLength(48);
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.FailureDetail).HasColumnName("failure_detail");
        builder.Property(x => x.ItemName).HasColumnName("item_name").HasMaxLength(2048);
        builder.Property(x => x.QueryText).HasColumnName("query_text").HasMaxLength(4096);
        builder.Property(x => x.SourceDetail).HasColumnName("source_detail").HasMaxLength(128);
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.PayloadSchemaVersion).HasColumnName("payload_schema_version");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json");

        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.ParentJobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.SourceJobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.ResultJobId).OnDelete(DeleteBehavior.SetNull);
        // Listing cursors use display IDs; retention and restart reconciliation
        // use completion/lifecycle and runtime/lifecycle indexes respectively.
        builder.HasIndex(x => x.DisplayId).IsUnique();
        builder.HasIndex(x => new { x.WorkflowId, x.DisplayId, x.Id });
        builder.HasIndex(x => new { x.LastRuntimeId, x.LifecycleState });
        builder.HasIndex(x => new { x.LifecycleState, x.CompletedAtUtc });
        builder.HasIndex(x => new { x.ParentJobId, x.DisplayId, x.Id });
        builder.HasIndex(x => x.SourceJobId);
        builder.HasIndex(x => x.ResultJobId);
        builder.HasIndex(x => new { x.SubmissionId, x.DisplayId, x.Id });
    }
}

internal sealed class SubmissionConfiguration : IEntityTypeConfiguration<SubmissionEntity>
{
    public void Configure(EntityTypeBuilder<SubmissionEntity> builder)
    {
        builder.ToTable("submissions", table =>
        {
            table.HasCheckConstraint("ck_submissions_revision", "revision >= 0");
            table.HasCheckConstraint("ck_submissions_specification_schema", "specification_schema_version > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SubmittedAtUtc).HasColumnName("submitted_at_utc");
        builder.Property(x => x.SpecificationSchemaVersion).HasColumnName("specification_schema_version");
        // A submission's identity belongs to this metadata row. Keep its large
        // immutable payload separate without redirecting self-referencing reruns
        // to the payload table (as EF entity splitting does).
        builder.HasOne(x => x.Specification).WithOne()
            .HasForeignKey<SubmissionSpecificationEntity>(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.RerunOfSubmissionId).HasColumnName("rerun_of_submission_id");
        builder.Property(x => x.PreviewId).HasColumnName("preview_id");
        builder.Property(x => x.ArtifactId).HasColumnName("artifact_id").HasMaxLength(256);
        builder.Property(x => x.CommitFingerprint).HasColumnName("commit_fingerprint").HasMaxLength(64);
        builder.Property(x => x.CommitReceiptJson).HasColumnName("commit_receipt_json");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.ArchivedAtUtc).HasColumnName("archived_at_utc");
        builder.HasOne<SubmissionEntity>().WithMany().HasForeignKey(x => x.RerunOfSubmissionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.SubmittedAtUtc, x.Id });
        builder.HasIndex(x => x.RerunOfSubmissionId);
        builder.HasIndex(x => x.ArchivedAtUtc);
    }
}

internal sealed class SubmissionSpecificationConfiguration : IEntityTypeConfiguration<SubmissionSpecificationEntity>
{
    public void Configure(EntityTypeBuilder<SubmissionSpecificationEntity> builder)
    {
        builder.ToTable("submission_specifications");
        builder.HasKey(x => x.SubmissionId);
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.Json).HasColumnName("specification_json");
    }
}

internal sealed class SearchJobConfiguration : IEntityTypeConfiguration<SearchJobEntity>
{
    public void Configure(EntityTypeBuilder<SearchJobEntity> builder)
    {
        builder.ToTable("search_jobs", table => table.HasCheckConstraint("ck_search_jobs_revision", "revision >= 0"));
        builder.HasKey(x => x.JobId);
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.Query).HasColumnName("query").HasMaxLength(4096);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.ResultCount).HasColumnName("result_count");
        builder.Property(x => x.LockedFileCount).HasColumnName("locked_file_count");
        builder.Property(x => x.ObservedPeerCount).HasColumnName("observed_peer_count");
        builder.Property(x => x.IsComplete).HasColumnName("is_complete");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.ResultPersistenceState).HasColumnName("result_persistence_state").HasMaxLength(32);
        builder.Property(x => x.ResultsPrunedAtUtc).HasColumnName("results_pruned_at_utc");
        builder.HasOne<JobEntity>().WithOne().HasForeignKey<SearchJobEntity>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ResultPersistenceState, x.CompletedAtUtc });
    }
}

internal sealed class SearchResultConfiguration : IEntityTypeConfiguration<SearchResultEntity>
{
    public void Configure(EntityTypeBuilder<SearchResultEntity> builder)
    {
        // Read projection over shared facts. Writes and raw-history pruning
        // go through the persistence owner, never EF entity mutation.
        builder.ToView("search_results");
        builder.HasKey(x => new { x.SearchJobId, x.Sequence });
        builder.Property(x => x.SearchJobId).HasColumnName("search_job_id");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        builder.Property(x => x.RemoteFilename).HasColumnName("remote_filename").HasMaxLength(4096);
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.BitRate).HasColumnName("bit_rate");
        builder.Property(x => x.BitDepth).HasColumnName("bit_depth");
        builder.Property(x => x.ResponseFileCount).HasColumnName("response_file_count");
        builder.Property(x => x.SampleRate).HasColumnName("sample_rate");
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
        builder.Property(x => x.Extension).HasColumnName("extension").HasMaxLength(32);
        builder.Property(x => x.UploadSpeed).HasColumnName("upload_speed");
        builder.Property(x => x.HasFreeUploadSlot).HasColumnName("has_free_upload_slot");
        builder.Property(x => x.QueueLength).HasColumnName("queue_length");
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(16);
        builder.Property(x => x.AttributesJson).HasColumnName("attributes_json");
        builder.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");

    }
}

internal sealed class TransferConfiguration : IEntityTypeConfiguration<TransferEntity>
{
    public void Configure(EntityTypeBuilder<TransferEntity> builder)
    {
        builder.ToTable("transfers", table =>
        {
            table.HasCheckConstraint("ck_transfers_revision", "revision >= 0");
            table.HasCheckConstraint("ck_transfers_last_sequence", "last_sequence >= 0");
            table.HasCheckConstraint("ck_transfers_bytes", "total_bytes >= 0 AND transferred_bytes >= 0");
            table.HasCheckConstraint("ck_transfers_terminal_time", "(terminal_outcome = 'None' AND completed_at_utc IS NULL) OR (terminal_outcome <> 'None' AND completed_at_utc IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16);
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        builder.Property(x => x.RemotePath).HasColumnName("remote_path").HasMaxLength(4096);
        builder.Property(x => x.LocalPath).HasColumnName("local_path").HasMaxLength(4096);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.TerminalOutcome).HasColumnName("terminal_outcome").HasMaxLength(32);
        builder.Property(x => x.TotalBytes).HasColumnName("total_bytes");
        builder.Property(x => x.TransferredBytes).HasColumnName("transferred_bytes");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.LastProgressAtUtc).HasColumnName("last_progress_at_utc");
        builder.Property(x => x.BytesPerSecond).HasColumnName("bytes_per_second");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.CancellationSource).HasColumnName("cancellation_source").HasMaxLength(32);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(1024);
        builder.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes");
        builder.Property(x => x.FileExtension).HasColumnName("file_extension").HasMaxLength(32);
        builder.Property(x => x.FileBitRate).HasColumnName("file_bit_rate");
        builder.Property(x => x.FileBitDepth).HasColumnName("file_bit_depth");
        builder.Property(x => x.FileSampleRate).HasColumnName("file_sample_rate");
        builder.Property(x => x.FileLength).HasColumnName("file_length");
        builder.Property(x => x.FileAttributesJson).HasColumnName("file_attributes_json");
        builder.Property(x => x.GroupRef).HasColumnName("group_ref").HasMaxLength(4096);
        builder.Property(x => x.GroupDisplayPath).HasColumnName("group_display_path").HasMaxLength(4096);
        builder.Property(x => x.GroupRelativePath).HasColumnName("group_relative_path");
        builder.Property(x => x.ArchivedAtUtc).HasColumnName("archived_at_utc");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.JobId);
        builder.HasIndex(x => new { x.WorkflowId, x.CreatedAtUtc, x.Id });
        // Timeline cursors use acceptance time, which does not change on start.
        builder.HasIndex(x => new { x.Username, x.ArchivedAtUtc, x.CreatedAtUtc, x.Id });
        builder.HasIndex(x => new { x.TerminalOutcome, x.CompletedAtUtc });
        builder.HasIndex(x => new { x.LastRuntimeId, x.TerminalOutcome });
        builder.HasIndex(x => x.CompletedAtUtc);
        builder.HasIndex(x => new { x.ArchivedAtUtc, x.CreatedAtUtc, x.Id });
        builder.HasIndex(x => new { x.GroupRef, x.Username, x.Direction, x.ArchivedAtUtc });
    }
}

internal sealed class TransferAttemptConfiguration : IEntityTypeConfiguration<TransferAttemptEntity>
{
    public void Configure(EntityTypeBuilder<TransferAttemptEntity> builder)
    {
        builder.ToTable("transfer_attempts", table =>
        {
            table.HasCheckConstraint("ck_transfer_attempts_number", "attempt_number > 0");
            table.HasCheckConstraint("ck_transfer_attempts_revision", "revision >= 0");
            table.HasCheckConstraint("ck_transfer_attempts_last_sequence", "last_sequence >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TransferId).HasColumnName("transfer_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.SourceUsername).HasColumnName("source_username").HasMaxLength(256);
        builder.Property(x => x.SourcePath).HasColumnName("source_path").HasMaxLength(4096);
        builder.Property(x => x.OutputPath).HasColumnName("output_path").HasMaxLength(4096);
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.HasOne<TransferEntity>().WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TransferId, x.AttemptNumber }).IsUnique();
        builder.HasIndex(x => new { x.LastRuntimeId, x.CompletedAtUtc });
    }
}

internal sealed class TransferAccountingCheckpointConfiguration : IEntityTypeConfiguration<TransferAccountingCheckpointEntity>
{
    public void Configure(EntityTypeBuilder<TransferAccountingCheckpointEntity> builder)
    {
        builder.ToTable("transfer_accounting_checkpoints", table =>
        {
            table.HasCheckConstraint("ck_transfer_accounting_checkpoint_revision", "revision >= 0");
            table.HasCheckConstraint("ck_transfer_accounting_checkpoint_bytes", "cumulative_bytes >= 0");
        });
        builder.HasKey(x => x.AttemptId);
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.TransferId).HasColumnName("transfer_id");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.CumulativeBytes).HasColumnName("cumulative_bytes");
        builder.Property(x => x.LastObservedAtUtc).HasColumnName("last_observed_at_utc");
        builder.HasOne<TransferEntity>().WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.TransferId);
    }
}

internal sealed class TransferByteBucketConfiguration : IEntityTypeConfiguration<TransferByteBucketEntity>
{
    public void Configure(EntityTypeBuilder<TransferByteBucketEntity> builder)
    {
        builder.ToTable("transfer_byte_buckets", table =>
            table.HasCheckConstraint("ck_transfer_byte_bucket_bytes", "bytes >= 0"));
        builder.HasKey(x => new { x.BucketStartUtc, x.Direction, x.Username });
        builder.Property(x => x.BucketStartUtc).HasColumnName("bucket_start_utc");
        builder.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16);
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        builder.Property(x => x.Bytes).HasColumnName("bytes");
        builder.HasIndex(x => new { x.Direction, x.BucketStartUtc });
        builder.HasIndex(x => new { x.Username, x.BucketStartUtc });
    }
}

internal sealed class TransferAccountingStateConfiguration : IEntityTypeConfiguration<TransferAccountingStateEntity>
{
    public void Configure(EntityTypeBuilder<TransferAccountingStateEntity> builder)
    {
        builder.ToTable("transfer_accounting_state", table =>
        {
            table.HasCheckConstraint("ck_transfer_accounting_state_singleton", "state_id = 1");
            table.HasCheckConstraint("ck_transfer_accounting_state_coverage", "complete_from_utc >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("state_id").ValueGeneratedNever();
        builder.Property(x => x.CompleteFromUtc).HasColumnName("complete_from_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
    }
}

internal sealed class PeerRestrictionOverrideConfiguration
    : IEntityTypeConfiguration<PeerRestrictionOverrideEntity>
{
    public void Configure(EntityTypeBuilder<PeerRestrictionOverrideEntity> builder)
    {
        builder.ToTable("peer_restriction_overrides");
        builder.HasKey(x => new { x.RestrictionKind, x.Username });
        builder.Property(x => x.RestrictionKind)
            .HasColumnName("restriction_kind")
            .HasMaxLength(32);
        builder.Property(x => x.Username)
            .HasColumnName("username")
            .HasMaxLength(256)
            .UseCollation("BINARY");
        builder.Property(x => x.OverrideState)
            .HasColumnName("override_state")
            .HasMaxLength(16);
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(x => new { x.Username, x.RestrictionKind });
    }
}

internal sealed class InputArtifactConfiguration : IEntityTypeConfiguration<InputArtifactEntity>
{
    public void Configure(EntityTypeBuilder<InputArtifactEntity> builder)
    {
        builder.ToTable("input_artifacts", table =>
            table.HasCheckConstraint("ck_input_artifacts_length", "length >= 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasMaxLength(32);
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(x => x.Length).HasColumnName("length");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(x => x.OriginalName).HasColumnName("original_name").HasMaxLength(255);
        builder.HasIndex(x => new { x.ExpiresAtUtc, x.Id });
    }
}

internal sealed class ChatConversationConfiguration : IEntityTypeConfiguration<ChatConversationEntity>
{
    public void Configure(EntityTypeBuilder<ChatConversationEntity> builder)
    {
        builder.ToTable("chat_conversations", table =>
        {
            table.HasCheckConstraint("ck_chat_conversations_revision", "revision >= 0");
            table.HasCheckConstraint("ck_chat_conversations_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("conversation_id");
        builder.Property(x => x.LocalAccountKey).HasColumnName("local_account_key").HasMaxLength(1024);
        builder.Property(x => x.PeerKey).HasColumnName("peer_key").HasMaxLength(1024);
        builder.Property(x => x.DisplayUsername).HasColumnName("display_username").HasMaxLength(1024);
        builder.Property(x => x.ArchivedAtUtc).HasColumnName("archived_at_utc");
        builder.Property(x => x.LastReadSequence).HasColumnName("last_read_sequence");
        builder.Property(x => x.LastMessageSequence).HasColumnName("last_message_sequence");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(x => new { x.LocalAccountKey, x.PeerKey }).IsUnique();
        builder.HasIndex(x => new { x.LocalAccountKey, x.LastMessageSequence, x.Id });
    }
}

internal sealed class ChatSequenceConfiguration : IEntityTypeConfiguration<ChatSequenceEntity>
{
    public void Configure(EntityTypeBuilder<ChatSequenceEntity> builder)
    {
        builder.ToTable("chat_sequences", table =>
        {
            table.HasCheckConstraint("ck_chat_sequences_singleton", "sequence_id = 1");
            table.HasCheckConstraint(
                "ck_chat_sequences_values",
                "last_message_sequence >= 0 AND last_notification_sequence >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("sequence_id").ValueGeneratedNever();
        builder.Property(x => x.LastMessageSequence).HasColumnName("last_message_sequence");
        builder.Property(x => x.LastNotificationSequence).HasColumnName("last_notification_sequence");
    }
}

internal sealed class ChatRoomSubscriptionConfiguration : IEntityTypeConfiguration<ChatRoomSubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<ChatRoomSubscriptionEntity> builder)
    {
        builder.ToTable("chat_room_subscriptions", table =>
        {
            table.HasCheckConstraint("ck_chat_room_subscriptions_revision", "revision >= 0");
            table.HasCheckConstraint("ck_chat_room_subscriptions_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("room_id");
        builder.Property(x => x.LocalAccountKey).HasColumnName("local_account_key").HasMaxLength(1024);
        builder.Property(x => x.RoomKey).HasColumnName("room_key").HasMaxLength(1024);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(1024);
        builder.Property(x => x.RuntimeDesired).HasColumnName("runtime_desired");
        builder.Property(x => x.Kind).HasColumnName("room_kind").HasMaxLength(16);
        builder.Property(x => x.LastReadSequence).HasColumnName("last_read_sequence");
        builder.Property(x => x.LastMessageSequence).HasColumnName("last_message_sequence");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(x => new { x.LocalAccountKey, x.RoomKey }).IsUnique();
        builder.HasIndex(x => new { x.LocalAccountKey, x.LastMessageSequence, x.Id });
        builder.HasIndex(x => new { x.LocalAccountKey, x.RuntimeDesired });
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessageEntity>
{
    public void Configure(EntityTypeBuilder<ChatMessageEntity> builder)
    {
        builder.ToTable("chat_messages", table =>
        {
            table.HasCheckConstraint("ck_chat_messages_sequence", "sequence > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("message_id");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.LocalAccountKey).HasColumnName("local_account_key").HasMaxLength(1024);
        builder.Property(x => x.TargetKind).HasColumnName("target_kind").HasMaxLength(16);
        builder.Property(x => x.TargetId).HasColumnName("target_id");
        builder.Property(x => x.TargetKey).HasColumnName("target_key").HasMaxLength(1024);
        builder.Property(x => x.DisplayTarget).HasColumnName("display_target").HasMaxLength(1024);
        builder.Property(x => x.SenderKey).HasColumnName("sender_key").HasMaxLength(1024);
        builder.Property(x => x.DisplaySender).HasColumnName("display_sender").HasMaxLength(1024);
        builder.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16);
        builder.Property(x => x.Body).HasColumnName("body");
        builder.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
        builder.Property(x => x.SendState).HasColumnName("send_state").HasMaxLength(16);
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(2048);
        builder.Property(x => x.ProtocolMessageId).HasColumnName("protocol_message_id");
        builder.Property(x => x.ProtocolTimestamp).HasColumnName("protocol_timestamp");
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasIndex(x => new { x.LocalAccountKey, x.TargetId, x.Sequence, x.Id });
        builder.HasIndex(x => new
        {
            x.LocalAccountKey,
            x.TargetKind,
            x.TargetKey,
            x.ProtocolMessageId,
            x.ProtocolTimestamp,
        }).IsUnique().HasFilter("protocol_message_id IS NOT NULL");
        builder.HasIndex(x => new { x.LocalAccountKey, x.SendState });
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    public void Configure(EntityTypeBuilder<NotificationEntity> builder)
    {
        builder.ToTable("notifications", table =>
        {
            table.HasCheckConstraint("ck_notifications_sequence", "sequence > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("notification_id");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.LocalAccountKey).HasColumnName("local_account_key").HasMaxLength(1024);
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(x => x.SourceMessageId).HasColumnName("source_message_id");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.ReadAtUtc).HasColumnName("read_at_utc");
        builder.HasOne<ChatMessageEntity>().WithMany().HasForeignKey(x => x.SourceMessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasIndex(x => new { x.LocalAccountKey, x.SourceMessageId, x.Kind }).IsUnique();
        builder.HasIndex(x => new { x.LocalAccountKey, x.ReadAtUtc, x.Sequence, x.Id });
    }
}
