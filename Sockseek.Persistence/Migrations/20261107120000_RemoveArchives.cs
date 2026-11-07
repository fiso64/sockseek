using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261107120000_RemoveArchives")]
public sealed class RemoveArchives : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing archived submissions adopt permanent deletion once. Transfer
        // removal keeps history and accounting; chat conversation closure is unchanged.
        migrationBuilder.Sql("""
            UPDATE submissions SET rerun_of_submission_id = NULL
            WHERE rerun_of_submission_id IN (SELECT id FROM submissions WHERE archived_at_utc IS NOT NULL);
            DELETE FROM jobs WHERE submission_id IN (SELECT id FROM submissions WHERE archived_at_utc IS NOT NULL);
            DELETE FROM submissions WHERE archived_at_utc IS NOT NULL;
            DROP INDEX IX_submissions_archived_at_utc;
            ALTER TABLE submissions DROP COLUMN archived_at_utc;
            ALTER TABLE transfers RENAME COLUMN archived_at_utc TO removed_at_utc;
            """);
        RenameTransferIndexes(migrationBuilder, "archived", "removed");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Deleted jobs cannot be restored by a schema downgrade.
        migrationBuilder.Sql("""
            ALTER TABLE submissions ADD COLUMN archived_at_utc INTEGER NULL;
            CREATE INDEX IX_submissions_archived_at_utc ON submissions (archived_at_utc);
            ALTER TABLE transfers RENAME COLUMN removed_at_utc TO archived_at_utc;
            """);
        RenameTransferIndexes(migrationBuilder, "removed", "archived");
    }

    private static void RenameTransferIndexes(MigrationBuilder migrationBuilder, string before, string after)
    {
        foreach (string columns in new[] { "{0}_at_utc,created_at_utc,id", "{0}_at_utc,direction,created_at_utc,id",
            "group_ref,username,direction,{0}_at_utc", "username,{0}_at_utc,created_at_utc,id" })
        {
            string oldColumns = string.Format(columns, before), newColumns = string.Format(columns, after);
            migrationBuilder.Sql($"DROP INDEX IX_transfers_{oldColumns.Replace(',', '_')}; " +
                $"CREATE INDEX IX_transfers_{newColumns.Replace(',', '_')} ON transfers ({newColumns});");
        }
    }
}
