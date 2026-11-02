using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_conversations",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    peer_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_username = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    archived_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_read_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_conversations", x => x.conversation_id);
                    table.CheckConstraint("ck_chat_conversations_revision", "revision >= 0");
                    table.CheckConstraint("ck_chat_conversations_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    target_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_target = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    sender_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_sender = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    recorded_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    send_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    protocol_message_id = table.Column<int>(type: "INTEGER", nullable: true),
                    protocol_timestamp = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.message_id);
                    table.CheckConstraint("ck_chat_messages_sequence", "sequence > 0");
                });

            migrationBuilder.CreateTable(
                name: "chat_room_subscriptions",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    room_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    runtime_desired = table.Column<bool>(type: "INTEGER", nullable: false),
                    room_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    last_read_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_subscriptions", x => x.room_id);
                    table.CheckConstraint("ck_chat_room_subscriptions_revision", "revision >= 0");
                    table.CheckConstraint("ck_chat_room_subscriptions_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "chat_sequences",
                columns: table => new
                {
                    sequence_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_notification_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_sequences", x => x.sequence_id);
                    table.CheckConstraint("ck_chat_sequences_singleton", "sequence_id = 1");
                    table.CheckConstraint("ck_chat_sequences_values", "last_message_sequence >= 0 AND last_notification_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "input_artifacts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    length = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    original_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_input_artifacts", x => x.id);
                    table.CheckConstraint("ck_input_artifacts_length", "length >= 0");
                });

            migrationBuilder.CreateTable(
                name: "peer_restriction_overrides",
                columns: table => new
                {
                    restriction_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY"),
                    override_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_peer_restriction_overrides", x => new { x.restriction_kind, x.username });
                });

            migrationBuilder.CreateTable(
                name: "runtime_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    stopped_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    shutdown_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transfer_accounting_state",
                columns: table => new
                {
                    state_id = table.Column<int>(type: "INTEGER", nullable: false),
                    complete_from_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_accounting_state", x => x.state_id);
                    table.CheckConstraint("ck_transfer_accounting_state_coverage", "complete_from_utc >= 0");
                    table.CheckConstraint("ck_transfer_accounting_state_singleton", "state_id = 1");
                });

            migrationBuilder.CreateTable(
                name: "transfer_byte_buckets",
                columns: table => new
                {
                    bucket_start_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_byte_buckets", x => new { x.bucket_start_utc, x.direction, x.username });
                    table.CheckConstraint("ck_transfer_byte_bucket_bytes", "bytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source_message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    read_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.notification_id);
                    table.CheckConstraint("ck_notifications_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "FK_notifications_chat_messages_source_message_id",
                        column: x => x.source_message_id,
                        principalTable: "chat_messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    submission_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    semantic_role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    parent_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    result_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    display_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    lifecycle_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    activity_phase = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    activity_until_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    terminal_outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    skip_reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    cancellation_source = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    failure_detail = table.Column<string>(type: "TEXT", nullable: true),
                    item_name = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    query_text = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    source_detail = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    payload_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_display_id", "display_id > 0");
                    table.CheckConstraint("ck_jobs_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_jobs_revision", "revision >= 0");
                    table.CheckConstraint("ck_jobs_terminal_time", "(lifecycle_state = 'Terminal' AND completed_at_utc IS NOT NULL) OR (lifecycle_state <> 'Terminal' AND completed_at_utc IS NULL)");
                    table.ForeignKey(
                        name: "FK_jobs_jobs_parent_job_id",
                        column: x => x.parent_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_jobs_result_job_id",
                        column: x => x.result_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_jobs_source_job_id",
                        column: x => x.source_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    query = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    result_count = table.Column<long>(type: "INTEGER", nullable: false),
                    locked_file_count = table.Column<long>(type: "INTEGER", nullable: false),
                    observed_peer_count = table.Column<long>(type: "INTEGER", nullable: false),
                    is_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    result_persistence_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    results_pruned_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_jobs", x => x.job_id);
                    table.CheckConstraint("ck_search_jobs_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_search_jobs_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    workflow_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    remote_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    local_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    terminal_outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    total_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    transferred_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_progress_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    bytes_per_second = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    cancellation_source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    file_size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    file_extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    file_bit_rate = table.Column<int>(type: "INTEGER", nullable: true),
                    file_bit_depth = table.Column<int>(type: "INTEGER", nullable: true),
                    file_sample_rate = table.Column<int>(type: "INTEGER", nullable: true),
                    file_length = table.Column<int>(type: "INTEGER", nullable: true),
                    file_attributes_json = table.Column<string>(type: "TEXT", nullable: true),
                    group_ref = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    group_display_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    group_relative_path = table.Column<string>(type: "TEXT", nullable: true),
                    archived_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.id);
                    table.CheckConstraint("ck_transfers_bytes", "total_bytes >= 0 AND transferred_bytes >= 0");
                    table.CheckConstraint("ck_transfers_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_transfers_revision", "revision >= 0");
                    table.CheckConstraint("ck_transfers_terminal_time", "(terminal_outcome = 'None' AND completed_at_utc IS NULL) OR (terminal_outcome <> 'None' AND completed_at_utc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_transfers_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_transfers_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_accounting_checkpoints",
                columns: table => new
                {
                    attempt_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    transfer_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    cumulative_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    last_observed_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_accounting_checkpoints", x => x.attempt_id);
                    table.CheckConstraint("ck_transfer_accounting_checkpoint_bytes", "cumulative_bytes >= 0");
                    table.CheckConstraint("ck_transfer_accounting_checkpoint_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_transfer_accounting_checkpoints_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfer_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    transfer_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_number = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source_username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    source_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    output_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_attempts", x => x.id);
                    table.CheckConstraint("ck_transfer_attempts_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_transfer_attempts_number", "attempt_number > 0");
                    table.CheckConstraint("ck_transfer_attempts_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_transfer_attempts_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfer_attempts_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    submitted_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    specification_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    rerun_of_submission_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    preview_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    artifact_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    commit_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    commit_receipt_json = table.Column<string>(type: "TEXT", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    archived_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submissions", x => x.id);
                    table.CheckConstraint("ck_submissions_revision", "revision >= 0");
                    table.CheckConstraint("ck_submissions_specification_schema", "specification_schema_version > 0");
                    // Reruns reference submission identity, not its split payload table.
                    table.ForeignKey(
                        name: "FK_submissions_submissions_rerun_of_submission_id",
                        column: x => x.rerun_of_submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission_specifications",
                columns: table => new
                {
                    submission_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    specification_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission_specifications", x => x.submission_id);
                    table.ForeignKey("FK_submission_specifications_submissions_submission_id",
                        x => x.submission_id, "submissions", "id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_local_account_key_last_message_sequence_conversation_id",
                table: "chat_conversations",
                columns: new[] { "local_account_key", "last_message_sequence", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_local_account_key_peer_key",
                table: "chat_conversations",
                columns: new[] { "local_account_key", "peer_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_send_state",
                table: "chat_messages",
                columns: new[] { "local_account_key", "send_state" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_target_id_sequence_message_id",
                table: "chat_messages",
                columns: new[] { "local_account_key", "target_id", "sequence", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_target_kind_target_key_protocol_message_id_protocol_timestamp",
                table: "chat_messages",
                columns: new[] { "local_account_key", "target_kind", "target_key", "protocol_message_id", "protocol_timestamp" },
                unique: true,
                filter: "protocol_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_sequence",
                table: "chat_messages",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_last_message_sequence_room_id",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "last_message_sequence", "room_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_room_key",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "room_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_runtime_desired",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "runtime_desired" });

            migrationBuilder.CreateIndex(
                name: "IX_input_artifacts_expires_at_utc_id",
                table: "input_artifacts",
                columns: new[] { "expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_display_id",
                table: "jobs",
                column: "display_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_last_runtime_id_lifecycle_state",
                table: "jobs",
                columns: new[] { "last_runtime_id", "lifecycle_state" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_lifecycle_state_completed_at_utc",
                table: "jobs",
                columns: new[] { "lifecycle_state", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_parent_job_id_display_id_id",
                table: "jobs",
                columns: new[] { "parent_job_id", "display_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_result_job_id",
                table: "jobs",
                column: "result_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_source_job_id",
                table: "jobs",
                column: "source_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_submission_id_display_id_id",
                table: "jobs",
                columns: new[] { "submission_id", "display_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_workflow_id_display_id_id",
                table: "jobs",
                columns: new[] { "workflow_id", "display_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_local_account_key_read_at_utc_sequence_notification_id",
                table: "notifications",
                columns: new[] { "local_account_key", "read_at_utc", "sequence", "notification_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_local_account_key_source_message_id_kind",
                table: "notifications",
                columns: new[] { "local_account_key", "source_message_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_sequence",
                table: "notifications",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_source_message_id",
                table: "notifications",
                column: "source_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_peer_restriction_overrides_username_restriction_kind",
                table: "peer_restriction_overrides",
                columns: new[] { "username", "restriction_kind" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_sessions_started_at_utc",
                table: "runtime_sessions",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_search_jobs_result_persistence_state_completed_at_utc",
                table: "search_jobs",
                columns: new[] { "result_persistence_state", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_submissions_archived_at_utc",
                table: "submissions",
                column: "archived_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_submissions_rerun_of_submission_id",
                table: "submissions",
                column: "rerun_of_submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_submissions_submitted_at_utc_id",
                table: "submissions",
                columns: new[] { "submitted_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_transfer_accounting_checkpoints_transfer_id",
                table: "transfer_accounting_checkpoints",
                column: "transfer_id");

            migrationBuilder.CreateIndex(
                name: "IX_transfer_attempts_last_runtime_id_completed_at_utc",
                table: "transfer_attempts",
                columns: new[] { "last_runtime_id", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfer_attempts_transfer_id_attempt_number",
                table: "transfer_attempts",
                columns: new[] { "transfer_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfer_byte_buckets_direction_bucket_start_utc",
                table: "transfer_byte_buckets",
                columns: new[] { "direction", "bucket_start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfer_byte_buckets_username_bucket_start_utc",
                table: "transfer_byte_buckets",
                columns: new[] { "username", "bucket_start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_archived_at_utc_created_at_utc_id",
                table: "transfers",
                columns: new[] { "archived_at_utc", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_completed_at_utc",
                table: "transfers",
                column: "completed_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_group_ref_username_direction_archived_at_utc",
                table: "transfers",
                columns: new[] { "group_ref", "username", "direction", "archived_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_job_id",
                table: "transfers",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_last_runtime_id_terminal_outcome",
                table: "transfers",
                columns: new[] { "last_runtime_id", "terminal_outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_terminal_outcome_completed_at_utc",
                table: "transfers",
                columns: new[] { "terminal_outcome", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_username_archived_at_utc_created_at_utc_id",
                table: "transfers",
                columns: new[] { "username", "archived_at_utc", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_workflow_id_created_at_utc_id",
                table: "transfers",
                columns: new[] { "workflow_id", "created_at_utc", "id" });

            // Retained facts and accepted execution manifests share this schema.
            // Temporary Search Views and retrieved-directory snapshots have no tables.
            migrationBuilder.Sql("""
                CREATE TABLE directory_manifests (
                    job_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    display_root TEXT NOT NULL,
                    entry_count INTEGER NOT NULL,
                    total_known_bytes INTEGER NOT NULL,
                    children_materialized INTEGER NOT NULL,
                    supplemental_job_ids_json TEXT NOT NULL,
                    job_revision INTEGER NOT NULL,
                    PRIMARY KEY (job_id, attempt_number),
                    FOREIGN KEY (job_id) REFERENCES jobs(id)
                        ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
                );
                
                CREATE TABLE directory_manifest_entries (
                    job_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    ordinal INTEGER NOT NULL,
                    target_json TEXT NOT NULL,
                    relative_directory_components_json TEXT NOT NULL,
                    child_job_id TEXT NULL,
                    PRIMARY KEY (job_id, attempt_number, ordinal),
                    FOREIGN KEY (job_id, attempt_number)
                        REFERENCES directory_manifests(job_id, attempt_number)
                        ON DELETE CASCADE
                );
                
                CREATE TABLE search_observation_sources (
                    id INTEGER PRIMARY KEY,
                    search_job_id TEXT NOT NULL UNIQUE REFERENCES search_jobs(job_id) ON DELETE CASCADE);
                
                CREATE TABLE search_observations (
                    id INTEGER PRIMARY KEY,
                    source_id INTEGER NOT NULL REFERENCES search_observation_sources(id) ON DELETE CASCADE,
                    source_sequence INTEGER NOT NULL CHECK(source_sequence>0),
                    source_revision INTEGER NOT NULL CHECK(source_revision>0),
                    username TEXT NOT NULL, response_file_count INTEGER NOT NULL, filename TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL, bit_rate INTEGER, bit_depth INTEGER, sample_rate INTEGER,
                    duration_seconds INTEGER, extension TEXT NOT NULL, upload_speed INTEGER,
                    has_free_upload_slot INTEGER, queue_length INTEGER, attributes_json TEXT,
                    observed_at_utc INTEGER NOT NULL, visibility TEXT NOT NULL);
                CREATE UNIQUE INDEX ix_search_observations_raw_sequence
                    ON search_observations(source_id, source_sequence);
                CREATE UNIQUE INDEX ix_search_observations_raw_identity
                    ON search_observations(source_id, username, filename, visibility);
                
                CREATE VIEW search_results AS
                    SELECT source.search_job_id, source_sequence AS sequence, source_revision AS revision,
                        username, filename AS remote_filename, size_bytes, bit_rate, bit_depth, response_file_count,
                        sample_rate, duration_seconds, extension, upload_speed, has_free_upload_slot, queue_length,
                        visibility, attributes_json, observed_at_utc
                    FROM search_observations fact JOIN search_observation_sources source ON source.id=fact.source_id;
                
                INSERT INTO transfer_accounting_state(state_id, complete_from_utc, updated_at_utc)
                    VALUES(1, CAST(strftime('%s','now') AS INTEGER)*1000, CAST(strftime('%s','now') AS INTEGER)*1000);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW search_results;
                DROP TABLE search_observations;
                DROP TABLE search_observation_sources;
                DROP TABLE directory_manifest_entries;
                DROP TABLE directory_manifests;
                """);

            migrationBuilder.DropTable(
                name: "chat_conversations");

            migrationBuilder.DropTable(
                name: "chat_room_subscriptions");

            migrationBuilder.DropTable(
                name: "chat_sequences");

            migrationBuilder.DropTable(
                name: "input_artifacts");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "peer_restriction_overrides");

            migrationBuilder.DropTable(
                name: "search_jobs");

            migrationBuilder.DropTable(
                name: "transfer_accounting_checkpoints");

            migrationBuilder.DropTable(
                name: "transfer_accounting_state");

            migrationBuilder.DropTable(
                name: "transfer_attempts");

            migrationBuilder.DropTable(
                name: "transfer_byte_buckets");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "runtime_sessions");

            migrationBuilder.DropTable(
                name: "submission_specifications");

            migrationBuilder.DropTable(
                name: "submissions");
        }
    }
}
