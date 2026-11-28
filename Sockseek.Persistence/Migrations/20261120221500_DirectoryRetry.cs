using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261120221500_DirectoryRetry")]
public sealed class DirectoryRetry : Migration
{
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            ALTER TABLE directory_manifests ADD COLUMN retry_job_id TEXT NULL
                REFERENCES jobs(id) ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED;
            CREATE INDEX ix_directory_manifests_retry ON directory_manifests(retry_job_id) WHERE retry_job_id IS NOT NULL;
            """);

    protected override void Down(MigrationBuilder migration)
        => migration.Sql("""
            DROP INDEX ix_directory_manifests_retry;
            ALTER TABLE directory_manifests DROP COLUMN retry_job_id;
            """);
}
