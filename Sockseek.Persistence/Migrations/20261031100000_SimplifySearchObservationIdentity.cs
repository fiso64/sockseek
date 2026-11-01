using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261031100000_SimplifySearchObservationIdentity")]
public sealed class SimplifySearchObservationIdentity : Migration
{
    // These numeric observation IDs are referenced by retained Views. Preserve
    // them, all facts and the source-scoped foreign keys while rebuilding.
    private const string Columns = """
        id,source_id,origin_kind,origin_id,source_sequence,source_revision,
        username,response_file_count,filename,size_bytes,bit_rate,bit_depth,
        sample_rate,duration_seconds,extension,upload_speed,has_free_upload_slot,
        queue_length,attributes_json,observed_at_utc,visibility
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, restoreUnusedId: false);

    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, restoreUnusedId: true);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool restoreUnusedId)
    {
        // Keep dependent read views/FKs pointing at the unchanged table name
        // during the replacement; restore normal ALTER behavior below. This is
        // confined to the migration, not an alternate runtime representation.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;", suppressTransaction: true);
        string oldColumn = restoreUnusedId ? "raw_result_id TEXT NULL," : "";
        string identityConstraint = restoreUnusedId
            ? "CHECK(raw_result_id IS NULL OR (source_sequence>0 AND source_revision>0)), UNIQUE(source_id,origin_kind,origin_id,source_sequence,source_revision)"
            : "CHECK(origin_kind<>0 OR (source_sequence>0 AND source_revision>0))";
        // The removed GUID was neither a public identity nor a referenced key.
        // A downgrade needs only a row-unique non-null marker for raw history.
        // Early publishers could retain an original before raw collection had
        // committed it. Keep those facts available to their Views without making
        // them newly visible through raw history when its marker is removed.
        string sourceColumns = restoreUnusedId ? Columns : Columns.Replace("origin_kind",
            "CASE WHEN origin_kind=0 AND raw_result_id IS NULL THEN 2 ELSE origin_kind END", StringComparison.Ordinal);
        string oldInsertColumn = restoreUnusedId ? ",raw_result_id" : "";
        string oldInsertValue = restoreUnusedId
            ? ",CASE WHEN origin_kind=0 THEN printf('00000000-0000-0000-%04X-%012X',id>>48,id&0xFFFFFFFFFFFF) END" : "";
        string rawPredicate = restoreUnusedId ? "raw_result_id IS NOT NULL" : "origin_kind=0";
        string oldViewId = restoreUnusedId ? "raw_result_id AS id," : "";
        migrationBuilder.Sql($"""
            DROP VIEW search_results;
            CREATE TABLE search_observations_rebuilt(
                id INTEGER PRIMARY KEY,{oldColumn}
                source_id INTEGER NOT NULL REFERENCES search_observation_sources(id),origin_kind INTEGER NOT NULL,
                origin_id TEXT NOT NULL,source_sequence INTEGER NOT NULL,source_revision INTEGER NOT NULL,
                username TEXT NOT NULL,response_file_count INTEGER NOT NULL,filename TEXT NOT NULL,size_bytes INTEGER NOT NULL,
                bit_rate INTEGER,bit_depth INTEGER,sample_rate INTEGER,duration_seconds INTEGER,extension TEXT NOT NULL,
                upload_speed INTEGER,has_free_upload_slot INTEGER,queue_length INTEGER,attributes_json TEXT,
                observed_at_utc INTEGER NOT NULL,visibility TEXT NOT NULL,{identityConstraint});
            INSERT INTO search_observations_rebuilt({Columns}{oldInsertColumn})
                SELECT {sourceColumns}{oldInsertValue} FROM search_observations;
            DROP TABLE search_observations;
            ALTER TABLE search_observations_rebuilt RENAME TO search_observations;
            CREATE UNIQUE INDEX ix_search_observations_source_identity ON search_observations(source_id,id);
            CREATE UNIQUE INDEX ix_search_observations_raw_sequence
                ON search_observations(source_id,source_sequence) WHERE {rawPredicate};
            CREATE UNIQUE INDEX ix_search_observations_raw_identity
                ON search_observations(source_id,username,filename,visibility) WHERE {rawPredicate};
            CREATE VIEW search_results AS
                SELECT {oldViewId}source.source_job_id AS search_job_id,source_sequence AS sequence,source_revision AS revision,
                    username,filename AS remote_filename,size_bytes,bit_rate,bit_depth,response_file_count,sample_rate,
                    duration_seconds,extension,upload_speed,has_free_upload_slot,queue_length,visibility,attributes_json,observed_at_utc
                FROM search_observations fact JOIN search_observation_sources source ON source.id=fact.source_id
                WHERE {rawPredicate} AND source.raw_search_job_id IS NOT NULL;
            """);
        if (!restoreUnusedId)
            migrationBuilder.Sql("""
                -- Raw sequence uniqueness is stronger than the old origin key.
                -- Retrieved/migrated observations still need their full origin.
                CREATE UNIQUE INDEX ix_search_observations_origin
                    ON search_observations(source_id,origin_kind,origin_id,source_sequence,source_revision)
                    WHERE origin_kind<>0;
                """);
        migrationBuilder.Sql("PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
}
