using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261006100000_AddSearchDirectoryObservations")]
public sealed class AddSearchDirectoryObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE TABLE search_directory_observations (
                id TEXT PRIMARY KEY,
                source_job_id TEXT NOT NULL,
                username TEXT NOT NULL COLLATE BINARY,
                folder_path TEXT NOT NULL COLLATE BINARY,
                source_revision INTEGER NOT NULL,
                peer_input_json TEXT NOT NULL,
                is_complete INTEGER NOT NULL,
                observed_at_utc INTEGER NOT NULL,
                expires_at_utc INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                UNIQUE (source_job_id, username, folder_path));
            CREATE INDEX ix_search_directory_observations_expiry
                ON search_directory_observations(expires_at_utc);

            CREATE TABLE search_directory_observation_files (
                observation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                filename TEXT NOT NULL COLLATE BINARY,
                size_bytes INTEGER NULL,
                extension TEXT NULL,
                bit_rate INTEGER NULL,
                bit_depth INTEGER NULL,
                sample_rate INTEGER NULL,
                duration_seconds INTEGER NULL,
                attributes_json TEXT NULL,
                PRIMARY KEY (observation_id, filename),
                FOREIGN KEY (observation_id)
                    REFERENCES search_directory_observations(id) ON DELETE CASCADE);

            CREATE TABLE search_view_retrieval_publications (
                view_id TEXT NOT NULL,
                directory_ref TEXT NOT NULL,
                observation_id TEXT NOT NULL,
                PRIMARY KEY (view_id, directory_ref),
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("search_view_retrieval_publications");
        migrationBuilder.DropTable("search_directory_observation_files");
        migrationBuilder.DropTable("search_directory_observations");
    }
}
