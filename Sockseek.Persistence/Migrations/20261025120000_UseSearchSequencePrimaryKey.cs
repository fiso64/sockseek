using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025120000_UseSearchSequencePrimaryKey")]
public sealed class UseSearchSequencePrimaryKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, sequenceKey: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, sequenceKey: false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool sequenceKey)
    {
        string key = sequenceKey ? "search_job_id, sequence" : "id";
        migrationBuilder.Sql($"""
            CREATE TABLE search_results_rebuilt (
                id TEXT NOT NULL, search_job_id TEXT NOT NULL,
                sequence INTEGER NOT NULL, revision INTEGER NOT NULL,
                username TEXT NOT NULL, remote_filename TEXT NOT NULL,
                size_bytes INTEGER NOT NULL, bit_rate INTEGER NULL, bit_depth INTEGER NULL,
                response_file_count INTEGER NOT NULL, sample_rate INTEGER NULL,
                duration_seconds INTEGER NULL, extension TEXT NOT NULL,
                upload_speed INTEGER NULL, has_free_upload_slot INTEGER NULL,
                attributes_json TEXT NULL, observed_at_utc INTEGER NOT NULL,
                queue_length INTEGER NULL, visibility TEXT NOT NULL DEFAULT 'Public',
                CONSTRAINT PK_search_results PRIMARY KEY ({key}),
                CONSTRAINT ck_search_results_revision CHECK (revision > 0),
                CONSTRAINT ck_search_results_sequence CHECK (sequence > 0),
                CONSTRAINT FK_search_results_search_jobs_search_job_id
                    FOREIGN KEY (search_job_id) REFERENCES search_jobs(job_id) ON DELETE CASCADE);
            INSERT INTO search_results_rebuilt (
                id, search_job_id, sequence, revision, username, remote_filename,
                size_bytes, bit_rate, bit_depth, response_file_count, sample_rate,
                duration_seconds, extension, upload_speed, has_free_upload_slot,
                attributes_json, observed_at_utc, queue_length, visibility)
            SELECT id, search_job_id, sequence, revision, username, remote_filename,
                size_bytes, bit_rate, bit_depth, response_file_count, sample_rate,
                duration_seconds, extension, upload_speed, has_free_upload_slot,
                attributes_json, observed_at_utc, queue_length, visibility
            FROM search_results;
            DROP TABLE search_results;
            ALTER TABLE search_results_rebuilt RENAME TO search_results;
            CREATE INDEX IX_search_results_search_job_id_username
                ON search_results(search_job_id, username);
            CREATE UNIQUE INDEX IX_search_results_search_job_id_username_remote_filename_visibility
                ON search_results(search_job_id, username, remote_filename, visibility);
            """);
        if (!sequenceKey)
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IX_search_results_search_job_id_sequence
                    ON search_results(search_job_id, sequence);
                """);
    }
}
