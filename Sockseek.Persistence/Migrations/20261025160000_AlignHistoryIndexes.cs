using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025160000_AlignHistoryIndexes")]
public sealed class AlignHistoryIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP INDEX IX_jobs_parent_job_id;
            DROP INDEX IX_transfers_direction_state_created_at_utc;
            DROP INDEX IX_transfers_direction_started_at_utc_id;
            DROP INDEX IX_transfers_username_direction_started_at_utc_id;
            DROP INDEX IX_transfers_direction_terminal_outcome_completed_at_utc;
            DROP INDEX IX_transfer_attempts_transfer_id_started_at_utc;
            CREATE INDEX IX_transfers_username_archived_at_utc_created_at_utc_id
                ON transfers(username, archived_at_utc, created_at_utc, id);
            CREATE INDEX IX_transfers_terminal_outcome_completed_at_utc
                ON transfers(terminal_outcome, completed_at_utc);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP INDEX IX_transfers_username_archived_at_utc_created_at_utc_id;
            DROP INDEX IX_transfers_terminal_outcome_completed_at_utc;
            CREATE INDEX IX_jobs_parent_job_id ON jobs(parent_job_id);
            CREATE INDEX IX_transfers_direction_state_created_at_utc
                ON transfers(direction, state, created_at_utc);
            CREATE INDEX IX_transfers_direction_started_at_utc_id
                ON transfers(direction, started_at_utc, id);
            CREATE INDEX IX_transfers_username_direction_started_at_utc_id
                ON transfers(username, direction, started_at_utc, id);
            CREATE INDEX IX_transfers_direction_terminal_outcome_completed_at_utc
                ON transfers(direction, terminal_outcome, completed_at_utc);
            CREATE INDEX IX_transfer_attempts_transfer_id_started_at_utc
                ON transfer_attempts(transfer_id, started_at_utc);
            """);
}
