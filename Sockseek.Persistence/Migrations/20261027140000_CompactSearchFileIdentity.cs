using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261027140000_CompactSearchFileIdentity")]
public sealed class CompactSearchFileIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // One atomic replacement, preserving parent rowids and public refs. Disable
        // cascades only while replacing referenced tables, as in EF's rebuilds.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("DROP VIEW search_view_file_rows;");
        RebuildViews(migrationBuilder, compact: true);
        RebuildFiles(migrationBuilder, compact: true);
        RebuildReferences(migrationBuilder, compact: true);
        CreateFileRows(migrationBuilder, compact: true);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("DROP VIEW search_view_file_rows;");
        RebuildFiles(migrationBuilder, compact: false);
        RebuildReferences(migrationBuilder, compact: false);
        RebuildViews(migrationBuilder, compact: false);
        CreateFileRows(migrationBuilder, compact: false);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    private static void RebuildViews(MigrationBuilder migrationBuilder, bool compact)
    {
        string identity = compact ? "storage_id INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE" : "id TEXT PRIMARY KEY";
        const string columns = """
            id, source_job_id, projection_kind, definition_json, created_at_utc, expires_at_utc,
            revision, source_revision, consumed_sequence, is_complete, retention_state,
            public_file_count, locked_file_count, public_bytes, locked_bytes, observed_peer_count,
            projected_file_count, projected_public_file_count, projected_locked_file_count,
            preferred_file_count, other_file_count, top_level_item_count, selectable_option_count
            """;
        migrationBuilder.Sql($"""
            CREATE TABLE search_views_rebuilt (
                {identity}, source_job_id TEXT NOT NULL, projection_kind TEXT NOT NULL,
                definition_json TEXT NOT NULL, created_at_utc INTEGER NOT NULL, expires_at_utc INTEGER NOT NULL,
                revision INTEGER NOT NULL, source_revision INTEGER NOT NULL, consumed_sequence INTEGER NOT NULL,
                is_complete INTEGER NOT NULL, retention_state TEXT NOT NULL,
                public_file_count INTEGER NOT NULL, locked_file_count INTEGER NOT NULL,
                public_bytes INTEGER NOT NULL, locked_bytes INTEGER NOT NULL, observed_peer_count INTEGER NOT NULL,
                projected_file_count INTEGER NOT NULL, projected_public_file_count INTEGER NOT NULL,
                projected_locked_file_count INTEGER NOT NULL, preferred_file_count INTEGER NOT NULL,
                other_file_count INTEGER NOT NULL, top_level_item_count INTEGER NOT NULL,
                selectable_option_count INTEGER NOT NULL);
            INSERT INTO search_views_rebuilt(rowid, {columns}) SELECT rowid, {columns} FROM search_views;
            DROP TABLE search_views;
            ALTER TABLE search_views_rebuilt RENAME TO search_views;
            CREATE INDEX ix_search_views_source ON search_views(source_job_id, created_at_utc);
            CREATE INDEX ix_search_views_expiry ON search_views(expires_at_utc);
            """);
    }

    private const string FileColumns = """
        admitted_revision, observation_id, sequence, source_row_revision, size_bytes, upload_speed, queue_length,
        preference_tier, necessary_conditions_satisfied, condition_matches_json, configured_conditions_json,
        sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie
        """;

    private static void RebuildFiles(MigrationBuilder migrationBuilder, bool compact)
    {
        string viewType = compact ? "INTEGER" : "TEXT";
        string reference = compact ? "ref_key BLOB NOT NULL CHECK(length(ref_key)=16)" : "item_ref TEXT NOT NULL";
        string key = compact ? "ref_key" : "item_ref";
        string viewTarget = compact ? "storage_id" : "id";
        string identitySelect = compact ? "owner.storage_id, unhex(file.item_ref)" : "owner.id, lower(hex(file.ref_key))";
        string viewJoin = compact ? "owner.id = file.view_id" : "owner.storage_id = file.view_id";
        string selectedColumns = string.Join(", ", FileColumns.Split(',').Select(column => "file." + column.Trim()));
        migrationBuilder.Sql($"""
            CREATE TABLE search_view_files_rebuilt (
                id INTEGER PRIMARY KEY, view_id {viewType} NOT NULL, {reference},
                admitted_revision INTEGER NOT NULL, observation_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL, source_row_revision INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL, upload_speed INTEGER NULL, queue_length INTEGER NULL,
                preference_tier TEXT NOT NULL, necessary_conditions_satisfied INTEGER NOT NULL,
                condition_matches_json TEXT NOT NULL, configured_conditions_json TEXT NOT NULL,
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL, sort_tie INTEGER NOT NULL,
                UNIQUE(view_id, {key}),
                FOREIGN KEY(view_id) REFERENCES search_views({viewTarget}) ON DELETE CASCADE,
                FOREIGN KEY(observation_id) REFERENCES search_observations(id));
            INSERT INTO search_view_files_rebuilt(id, view_id, {key}, {FileColumns})
                SELECT file.id, {identitySelect}, {selectedColumns}
                FROM search_view_files file JOIN search_views owner ON {viewJoin};
            DROP TABLE search_view_files;
            ALTER TABLE search_view_files_rebuilt RENAME TO search_view_files;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, {key}, upload_speed, queue_length, size_bytes);
            CREATE INDEX ix_search_view_files_observation ON search_view_files(observation_id);
            """);
    }

    private static void RebuildReferences(MigrationBuilder migrationBuilder, bool compact)
    {
        string best = compact ? "best_file_id INTEGER" : "best_file_ref TEXT";
        string bestName = compact ? "best_file_id" : "best_file_ref";
        string representative = compact ? "representative_file_id INTEGER" : "representative_file_ref TEXT";
        string representativeName = compact ? "representative_file_id" : "representative_file_ref";
        string option = compact ? "file_id INTEGER" : "file_ref TEXT";
        string optionName = compact ? "file_id" : "file_ref";
        string directoryForeignKey = compact ? "best_file_id" : "view_id, best_file_ref";
        string trackForeignKey = compact ? "representative_file_id" : "view_id, representative_file_ref";
        string optionForeignKey = compact ? "file_id" : "view_id, file_ref";
        string target = compact ? "id" : "view_id, item_ref";
        string selectedFile = compact ? "file.id" : "file.item_ref";
        string JoinFile(string alias, string oldColumn) => compact
            ? $"JOIN search_views owner ON owner.id = {alias}.view_id\n"
                + $"JOIN search_view_files file ON file.view_id = owner.storage_id AND file.ref_key = unhex({alias}.{oldColumn})"
            : $"JOIN search_view_files file ON file.id = {alias}.{oldColumn}";
        string withoutRowid = compact ? " WITHOUT ROWID" : "";
        migrationBuilder.Sql($"""
            CREATE TABLE search_view_directory_versions_rebuilt (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL, {best} NOT NULL,
                public_matching_count INTEGER NOT NULL, locked_matching_count INTEGER NOT NULL,
                public_matching_bytes INTEGER NOT NULL, locked_matching_bytes INTEGER NOT NULL,
                is_fully_retrieved INTEGER NOT NULL, retrieved_file_count INTEGER NULL,
                retrieved_bytes INTEGER NULL, is_removed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(view_id, item_ref, revision),
                FOREIGN KEY(view_id, item_ref) REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY({directoryForeignKey}) REFERENCES search_view_files({target}) ON DELETE CASCADE,
                FOREIGN KEY(view_id, revision) REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_directory_versions_rebuilt
                SELECT version.view_id, version.item_ref, version.revision, {selectedFile},
                    version.public_matching_count, version.locked_matching_count,
                    version.public_matching_bytes, version.locked_matching_bytes,
                    version.is_fully_retrieved, version.retrieved_file_count, version.retrieved_bytes, version.is_removed
                FROM search_view_directory_versions version
                {JoinFile("version", compact ? "best_file_ref" : "best_file_id")};
            DROP TABLE search_view_directory_versions;
            ALTER TABLE search_view_directory_versions_rebuilt RENAME TO search_view_directory_versions;
            CREATE INDEX ix_search_view_directory_versions_revision
                ON search_view_directory_versions(view_id, revision, item_ref);
            CREATE TABLE search_view_aggregate_track_versions_rebuilt (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                share_count INTEGER NOT NULL, selectable_option_count INTEGER NOT NULL, {representative} NOT NULL,
                PRIMARY KEY(view_id, item_ref, revision),
                FOREIGN KEY(view_id, item_ref) REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY({trackForeignKey}) REFERENCES search_view_files({target}) ON DELETE CASCADE,
                FOREIGN KEY(view_id, revision) REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_aggregate_track_versions_rebuilt
                SELECT version.view_id, version.item_ref, version.revision,
                    version.share_count, version.selectable_option_count, {selectedFile}
                FROM search_view_aggregate_track_versions version
                {JoinFile("version", compact ? "representative_file_ref" : "representative_file_id")};
            DROP TABLE search_view_aggregate_track_versions;
            ALTER TABLE search_view_aggregate_track_versions_rebuilt RENAME TO search_view_aggregate_track_versions;
            CREATE INDEX ix_search_view_aggregate_track_versions_page ON search_view_aggregate_track_versions(
                view_id, revision, share_count DESC, item_ref);
            CREATE TABLE search_view_aggregate_track_files_rebuilt (
                view_id TEXT NOT NULL, group_ref TEXT NOT NULL, {option} NOT NULL, admitted_revision INTEGER NOT NULL,
                PRIMARY KEY(view_id, group_ref, {optionName}),
                FOREIGN KEY(view_id, group_ref) REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY({optionForeignKey}) REFERENCES search_view_files({target}) ON DELETE CASCADE){withoutRowid};
            INSERT INTO search_view_aggregate_track_files_rebuilt
                SELECT option.view_id, option.group_ref, {selectedFile}, option.admitted_revision
                FROM search_view_aggregate_track_files option
                {JoinFile("option", compact ? "file_ref" : "file_id")};
            DROP TABLE search_view_aggregate_track_files;
            ALTER TABLE search_view_aggregate_track_files_rebuilt RENAME TO search_view_aggregate_track_files;
            CREATE INDEX ix_search_view_aggregate_track_files_page ON search_view_aggregate_track_files(
                view_id, group_ref, admitted_revision, {optionName});
            """);
        if (compact)
            migrationBuilder.Sql($"""
                CREATE INDEX ix_search_view_directory_versions_file ON search_view_directory_versions({bestName});
                CREATE INDEX ix_search_view_aggregate_track_versions_file ON search_view_aggregate_track_versions({representativeName});
                CREATE INDEX ix_search_view_aggregate_track_files_file ON search_view_aggregate_track_files(file_id);
                """);
    }

    private static void CreateFileRows(MigrationBuilder migrationBuilder, bool compact)
    {
        string membership = compact
            ? """
                member.id AS rowid, member.id, owner.id AS view_id,
                lower(hex(member.ref_key)) AS item_ref, member.ref_key,
                """ + string.Join(", ", FileColumns.Split(',').Select(column => "member." + column.Trim()))
            : "member.rowid AS rowid, member.*";
        string viewJoin = compact ? "JOIN search_views owner ON owner.storage_id=member.view_id" : "";
        migrationBuilder.Sql($"""
            CREATE VIEW search_view_file_rows AS
                SELECT {membership},
                    fact.username, fact.response_file_count, fact.filename, fact.bit_rate, fact.bit_depth,
                    fact.sample_rate, fact.duration_seconds, fact.extension, fact.has_free_upload_slot,
                    fact.attributes_json, fact.observed_at_utc, fact.visibility
                FROM search_view_files member {viewJoin}
                JOIN search_observations fact ON fact.id=member.observation_id;
            """);
    }
}
