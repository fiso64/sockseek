using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260924120000_AddDirectoryManifests")]
public sealed class AddDirectoryManifests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
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
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP TABLE directory_manifest_entries;
            DROP TABLE directory_manifests;
            """);
}
