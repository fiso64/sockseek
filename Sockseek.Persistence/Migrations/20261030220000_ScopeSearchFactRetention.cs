using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030220000_ScopeSearchFactRetention")]
public sealed class ScopeSearchFactRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            ALTER TABLE search_views ADD COLUMN source_id INTEGER NULL
                REFERENCES search_observation_sources(id);
            UPDATE search_views SET source_id = (
                SELECT id FROM search_observation_sources source
                WHERE source.source_job_id=upper(search_views.source_job_id))
            WHERE EXISTS (SELECT 1 FROM search_view_files file WHERE file.view_id=search_views.storage_id);
            CREATE INDEX ix_search_views_source_id ON search_views(source_id);
            CREATE UNIQUE INDEX ix_search_views_source_identity ON search_views(storage_id,source_id);
            CREATE UNIQUE INDEX ix_search_observations_source_identity ON search_observations(source_id,id);
            """);
        RebuildFiles(migrationBuilder, sourceScoped: true);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        RebuildFiles(migrationBuilder, sourceScoped: false);
        migrationBuilder.Sql("""
            DROP INDEX ix_search_views_source_id;
            DROP INDEX ix_search_views_source_identity;
            DROP INDEX ix_search_observations_source_identity;
            ALTER TABLE search_views DROP COLUMN source_id;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    private const string Columns = """
        id, view_id, ref_key, admitted_revision, observation_id, sequence, source_row_revision,
        size_bytes, upload_speed, queue_length, preference_tier, necessary_conditions_satisfied,
        matched_condition_flags, configured_condition_flags, sort_high, sort_upload_fast,
        sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie
        """;

    private static void RebuildFiles(MigrationBuilder migrationBuilder, bool sourceScoped)
    {
        // Preserve file IDs: directory/group histories reference these rows.
        // Index the shared source, rather than each random observation ID. The
        // retention owner deletes facts only after the source has no View pins.
        // Composite foreign keys still reject missing facts and cross-source links.
        string sourceColumn = sourceScoped ? "source_id INTEGER NOT NULL," : "";
        string foreignKeys = sourceScoped
            ? """
                FOREIGN KEY(view_id,source_id) REFERENCES search_views(storage_id,source_id) ON DELETE CASCADE,
                FOREIGN KEY(source_id,observation_id) REFERENCES search_observations(source_id,id)
                """
            : """
                FOREIGN KEY(view_id) REFERENCES search_views(storage_id) ON DELETE CASCADE,
                FOREIGN KEY(observation_id) REFERENCES search_observations(id)
                """;
        string selected = string.Join(",", Columns.Split(',').Select(column => "file." + column.Trim()));
        // Cover both View-FK columns when collecting cascade rowids. Reading
        // every file merely to check its source can spill dirty index pages.
        // The leading source also supports fact-FK checks after its last pin ends.
        string lookup = sourceScoped ? "source ON search_view_files(source_id,view_id)"
            : "observation ON search_view_files(observation_id)";
        migrationBuilder.Sql($"""
            DROP VIEW search_view_file_rows;
            CREATE TABLE search_view_files_rebuilt (
                id INTEGER PRIMARY KEY, view_id INTEGER NOT NULL,
                ref_key BLOB NOT NULL CHECK(length(ref_key)=16),
                admitted_revision INTEGER NOT NULL, observation_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL, source_row_revision INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL, upload_speed INTEGER NULL, queue_length INTEGER NULL,
                preference_tier TEXT NOT NULL, necessary_conditions_satisfied INTEGER NOT NULL,
                matched_condition_flags INTEGER NOT NULL, configured_condition_flags INTEGER NOT NULL,
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL, sort_tie INTEGER NOT NULL,
                {sourceColumn}
                UNIQUE(view_id,ref_key), {foreignKeys});
            INSERT INTO search_view_files_rebuilt ({Columns}{(sourceScoped ? ",source_id" : "")})
                SELECT {selected}{(sourceScoped ? ",owner.source_id" : "")}
                FROM search_view_files file JOIN search_views owner ON owner.storage_id=file.view_id;
            DROP TABLE search_view_files;
            ALTER TABLE search_view_files_rebuilt RENAME TO search_view_files;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id,admitted_revision,sort_high DESC,sort_upload_fast DESC,
                sort_mid DESC,sort_inferred DESC,sort_upload_medium DESC,
                sort_bitrate DESC,sort_tie DESC,sequence,ref_key,upload_speed,queue_length,size_bytes);
            CREATE INDEX ix_search_view_files_{lookup};
            CREATE VIEW search_view_file_rows AS
                SELECT member.id AS rowid, member.id, owner.id AS view_id,
                    lower(hex(member.ref_key)) AS item_ref, member.ref_key,
                    member.admitted_revision, member.observation_id, member.sequence,
                    member.source_row_revision, member.size_bytes, member.upload_speed,
                    member.queue_length, member.preference_tier, member.necessary_conditions_satisfied,
                    member.matched_condition_flags, member.configured_condition_flags,
                    member.sort_high, member.sort_upload_fast, member.sort_mid,
                    member.sort_inferred, member.sort_upload_medium, member.sort_bitrate, member.sort_tie,
                    fact.username, fact.response_file_count, fact.filename, fact.bit_rate, fact.bit_depth,
                    fact.sample_rate, fact.duration_seconds, fact.extension, fact.has_free_upload_slot,
                    fact.attributes_json, fact.observed_at_utc, fact.visibility
                FROM search_view_files member JOIN search_views owner ON owner.storage_id=member.view_id
                JOIN search_observations fact ON fact.id=member.observation_id;
            """);
    }
}
