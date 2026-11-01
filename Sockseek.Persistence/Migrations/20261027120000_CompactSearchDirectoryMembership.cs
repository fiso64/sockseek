using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261027120000_CompactSearchDirectoryMembership")]
public sealed class CompactSearchDirectoryMembership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Rebuild referenced parents without cascading their retained versions.
        // Preserve rowids so the replacement keys identify the same rows.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        RebuildParents(migrationBuilder, compact: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_directory_files_rebuilt (
                directory_id INTEGER NOT NULL REFERENCES search_view_directories(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                admitted_revision INTEGER NOT NULL, relative_path TEXT NOT NULL,
                removed_revision INTEGER NULL,
                PRIMARY KEY(directory_id, file_id)) WITHOUT ROWID;
            INSERT INTO search_view_directory_files_rebuilt
                SELECT directory.id, file.id, membership.admitted_revision,
                       membership.relative_path, membership.removed_revision
                FROM search_view_directory_files membership
                JOIN search_view_directories directory
                  ON directory.view_id = membership.view_id AND directory.item_ref = membership.directory_ref
                JOIN search_view_files file
                  ON file.view_id = membership.view_id AND file.item_ref = membership.file_ref;
            DROP TABLE search_view_directory_files;
            ALTER TABLE search_view_directory_files_rebuilt RENAME TO search_view_directory_files;
            -- Reverse lookup also keeps file deletion from scanning every edge.
            CREATE INDEX ix_search_view_directory_files_file ON search_view_directory_files(file_id, directory_id);
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(
                directory_id, admitted_revision, file_id, removed_revision, relative_path);
            CREATE INDEX ix_search_view_directory_files_path ON search_view_directory_files(
                directory_id, relative_path COLLATE BINARY, file_id);
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_directory_files_rebuilt (
                view_id TEXT NOT NULL, directory_ref TEXT NOT NULL, file_ref TEXT NOT NULL,
                admitted_revision INTEGER NOT NULL, relative_path TEXT NOT NULL, removed_revision INTEGER NULL,
                PRIMARY KEY(view_id, file_ref, directory_ref),
                FOREIGN KEY(view_id, directory_ref) REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY(view_id, file_ref) REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE);
            INSERT INTO search_view_directory_files_rebuilt
                SELECT directory.view_id, directory.item_ref, file.item_ref,
                       membership.admitted_revision, membership.relative_path, membership.removed_revision
                FROM search_view_directory_files membership
                JOIN search_view_directories directory ON directory.id = membership.directory_id
                JOIN search_view_files file ON file.id = membership.file_id;
            DROP TABLE search_view_directory_files;
            ALTER TABLE search_view_directory_files_rebuilt RENAME TO search_view_directory_files;
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(
                view_id, directory_ref, admitted_revision, relative_path COLLATE BINARY, file_ref);
            CREATE INDEX ix_search_view_directory_files_path ON search_view_directory_files(
                view_id, directory_ref, relative_path COLLATE BINARY, file_ref COLLATE BINARY);
            """);
        RebuildParents(migrationBuilder, compact: false);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    private static void RebuildParents(MigrationBuilder migrationBuilder, bool compact)
    {
        string id = compact ? "id INTEGER PRIMARY KEY," : "";
        string publicKey = compact ? "UNIQUE(view_id, item_ref)" : "PRIMARY KEY(view_id, item_ref)";
        const string fileColumns = """
            view_id, item_ref, admitted_revision, observation_id, sequence, source_row_revision,
            size_bytes, upload_speed, queue_length, preference_tier, necessary_conditions_satisfied,
            condition_matches_json, configured_conditions_json, sort_high, sort_upload_fast,
            sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie
            """;
        migrationBuilder.Sql($"""
            DROP VIEW search_view_file_rows;
            CREATE TABLE search_view_files_rebuilt (
                {id}
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, admitted_revision INTEGER NOT NULL,
                observation_id INTEGER NOT NULL, sequence INTEGER NOT NULL, source_row_revision INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL, upload_speed INTEGER NULL, queue_length INTEGER NULL,
                preference_tier TEXT NOT NULL, necessary_conditions_satisfied INTEGER NOT NULL,
                condition_matches_json TEXT NOT NULL, configured_conditions_json TEXT NOT NULL,
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL, sort_tie INTEGER NOT NULL,
                {publicKey},
                FOREIGN KEY(view_id) REFERENCES search_views(id) ON DELETE CASCADE,
                FOREIGN KEY(observation_id) REFERENCES search_observations(id));
            INSERT INTO search_view_files_rebuilt(rowid, {fileColumns})
                SELECT rowid, {fileColumns} FROM search_view_files;
            DROP TABLE search_view_files;
            ALTER TABLE search_view_files_rebuilt RENAME TO search_view_files;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, item_ref, upload_speed, queue_length, size_bytes);
            CREATE INDEX ix_search_view_files_observation ON search_view_files(observation_id);
            CREATE VIEW search_view_file_rows AS
                SELECT member.rowid AS rowid, member.*,
                    fact.username, fact.response_file_count, fact.filename, fact.bit_rate, fact.bit_depth,
                    fact.sample_rate, fact.duration_seconds, fact.extension, fact.has_free_upload_slot,
                    fact.attributes_json, fact.observed_at_utc, fact.visibility
                FROM search_view_files member JOIN search_observations fact ON fact.id=member.observation_id;
            CREATE TABLE search_view_directories_rebuilt (
                {id}
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, username TEXT NOT NULL, folder_path TEXT NOT NULL,
                {publicKey}, UNIQUE(view_id, username, folder_path),
                FOREIGN KEY(view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            INSERT INTO search_view_directories_rebuilt(rowid, view_id, item_ref, username, folder_path)
                SELECT rowid, view_id, item_ref, username, folder_path FROM search_view_directories;
            DROP TABLE search_view_directories;
            ALTER TABLE search_view_directories_rebuilt RENAME TO search_view_directories;
            """);
    }
}
