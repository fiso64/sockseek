using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261120220000_ReleaseRemovedDownloadRetries")]
public sealed class ReleaseRemovedDownloadRetries : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateIndex("IX_transfers_retry_job_id", "transfers", "retry_job_id", filter: "retry_job_id IS NOT NULL");
        migration.Sql("""
            UPDATE transfers AS original SET retry_job_id = NULL, revision = revision + 1
            WHERE retry_job_id IS NOT NULL AND removed_at_utc IS NULL
              AND EXISTS (SELECT 1 FROM transfers t WHERE t.job_id = original.retry_job_id AND t.removed_at_utc IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM transfers t WHERE t.job_id = original.retry_job_id AND t.removed_at_utc IS NULL);
            """);
    }

    protected override void Down(MigrationBuilder migration)
        => migration.DropIndex("IX_transfers_retry_job_id", "transfers");
}
