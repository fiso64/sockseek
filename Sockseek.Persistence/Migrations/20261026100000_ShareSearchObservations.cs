using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261026100000_ShareSearchObservations")]
public sealed class ShareSearchObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Rebuild the referenced membership table without cascading its dependent
        // directory/group rows. This is the same transaction boundary used by EF's
        // SQLite table rebuilds; the copied data and replacement commit together.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            -- One owner per source. Repeating a GUID in every fact and every
            -- index makes the first View more expensive than the copied layout.
            CREATE TABLE search_observation_sources (
                id INTEGER PRIMARY KEY, source_job_id TEXT NOT NULL UNIQUE,
                raw_search_job_id TEXT NULL REFERENCES search_jobs(job_id) ON DELETE SET NULL);
            CREATE UNIQUE INDEX ix_search_observation_sources_raw
                ON search_observation_sources(raw_search_job_id) WHERE raw_search_job_id IS NOT NULL;
            INSERT INTO search_observation_sources(source_job_id, raw_search_job_id)
                SELECT upper(search_job_id), search_job_id FROM search_results GROUP BY search_job_id;
            INSERT OR IGNORE INTO search_observation_sources(source_job_id)
                SELECT upper(source_job_id) FROM search_views GROUP BY source_job_id;
            CREATE TABLE search_observations (
                id INTEGER PRIMARY KEY, raw_result_id TEXT NULL,
                source_id INTEGER NOT NULL REFERENCES search_observation_sources(id), origin_kind INTEGER NOT NULL,
                origin_id TEXT NOT NULL, source_sequence INTEGER NOT NULL,
                source_revision INTEGER NOT NULL,
                username TEXT NOT NULL, response_file_count INTEGER NOT NULL,
                filename TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                bit_rate INTEGER NULL, bit_depth INTEGER NULL, sample_rate INTEGER NULL,
                duration_seconds INTEGER NULL, extension TEXT NOT NULL, upload_speed INTEGER NULL,
                has_free_upload_slot INTEGER NULL, queue_length INTEGER NULL,
                attributes_json TEXT NULL, observed_at_utc INTEGER NOT NULL, visibility TEXT NOT NULL,
                CHECK(raw_result_id IS NULL OR (source_sequence > 0 AND source_revision > 0)),
                UNIQUE(source_id, origin_kind, origin_id, source_sequence, source_revision));
            -- Old projections do not always retain a recoverable retrieval origin.
            -- Preserve their exact facts under their existing View, rather than
            -- guessing that a view-local sequence identifies a raw observation.
            INSERT INTO search_observations (
                id, source_id, origin_kind, origin_id, source_sequence, source_revision,
                username, response_file_count, filename, size_bytes, bit_rate, bit_depth, sample_rate, duration_seconds, extension, upload_speed, has_free_upload_slot, queue_length, attributes_json, observed_at_utc, visibility)
            SELECT file.rowid, source.id, 2, file.view_id, file.rowid, file.source_row_revision,
                file.username, file.response_file_count, file.filename, file.size_bytes, file.bit_rate, file.bit_depth, file.sample_rate, file.duration_seconds, file.extension, file.upload_speed, file.has_free_upload_slot, file.queue_length,
                CASE WHEN file.attributes_json IS NULL OR file.attributes_json = 'null' THEN NULL ELSE
                    (SELECT json_group_array(json_object('Code', coalesce(json_extract(value, '$.StableCode'), 0),
                        'Name', json_extract(value, '$.Type'), 'Value', json_extract(value, '$.Value')))
                     FROM json_each(file.attributes_json)) END, file.observed_at_utc, file.visibility
            FROM search_view_files file JOIN search_views owner ON owner.id=file.view_id
            JOIN search_observation_sources source ON source.source_job_id=upper(owner.source_job_id);
            INSERT INTO search_observations (
                raw_result_id, source_id, origin_kind, origin_id,
                source_sequence, source_revision, username, response_file_count, filename,
                size_bytes, bit_rate, bit_depth, sample_rate, duration_seconds, extension,
                upload_speed, has_free_upload_slot, queue_length, attributes_json, observed_at_utc, visibility)
            SELECT result.id, source.id, 0, '', result.sequence, result.revision,
                result.username, result.response_file_count, result.remote_filename, result.size_bytes, result.bit_rate, result.bit_depth,
                result.sample_rate, result.duration_seconds, result.extension, result.upload_speed, result.has_free_upload_slot,
                result.queue_length, result.attributes_json, result.observed_at_utc, result.visibility FROM search_results result
                JOIN search_observation_sources source ON source.source_job_id=upper(result.search_job_id);
            DROP TABLE search_results;
            CREATE UNIQUE INDEX ix_search_observations_raw_sequence
                ON search_observations(source_id, source_sequence)
                WHERE raw_result_id IS NOT NULL;
            CREATE UNIQUE INDEX ix_search_observations_raw_identity
                ON search_observations(source_id, username, filename, visibility)
                WHERE raw_result_id IS NOT NULL;
            CREATE VIEW search_results AS
                SELECT raw_result_id AS id, source.source_job_id AS search_job_id,
                    source_sequence AS sequence, source_revision AS revision,
                    username, filename AS remote_filename, size_bytes, bit_rate, bit_depth,
                    response_file_count, sample_rate, duration_seconds, extension, upload_speed,
                    has_free_upload_slot, queue_length, visibility, attributes_json, observed_at_utc
                FROM search_observations fact JOIN search_observation_sources source ON source.id=fact.source_id
                WHERE fact.raw_result_id IS NOT NULL AND source.raw_search_job_id IS NOT NULL;
            CREATE TABLE search_view_files_rebuilt (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, admitted_revision INTEGER NOT NULL,
                observation_id INTEGER NOT NULL, sequence INTEGER NOT NULL, source_row_revision INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL, upload_speed INTEGER NULL, queue_length INTEGER NULL,
                preference_tier TEXT NOT NULL, necessary_conditions_satisfied INTEGER NOT NULL,
                condition_matches_json TEXT NOT NULL, configured_conditions_json TEXT NOT NULL,
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL, sort_tie INTEGER NOT NULL,
                PRIMARY KEY(view_id, item_ref),
                FOREIGN KEY(view_id) REFERENCES search_views(id) ON DELETE CASCADE,
                FOREIGN KEY(observation_id) REFERENCES search_observations(id));
            INSERT INTO search_view_files_rebuilt (observation_id, view_id, item_ref, admitted_revision, sequence, source_row_revision, size_bytes, upload_speed, queue_length, preference_tier, necessary_conditions_satisfied, condition_matches_json, configured_conditions_json, sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie)
                SELECT rowid, view_id, item_ref, admitted_revision, sequence, source_row_revision, size_bytes, upload_speed, queue_length, preference_tier, necessary_conditions_satisfied, condition_matches_json, configured_conditions_json, sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie FROM search_view_files;
            DROP TABLE search_view_files;
            ALTER TABLE search_view_files_rebuilt RENAME TO search_view_files;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, item_ref, upload_speed, queue_length, size_bytes);
            CREATE INDEX ix_search_view_files_observation ON search_view_files(observation_id);
            CREATE VIEW search_view_file_rows AS
                SELECT member.rowid AS rowid, member.*,
                    fact.username, fact.response_file_count, fact.filename, fact.bit_rate, fact.bit_depth, fact.sample_rate, fact.duration_seconds, fact.extension, fact.has_free_upload_slot, fact.attributes_json, fact.observed_at_utc, fact.visibility
                FROM search_view_files member JOIN search_observations fact ON fact.id=member.observation_id;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
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
                CONSTRAINT PK_search_results PRIMARY KEY (search_job_id, sequence),
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
            DROP VIEW search_results;
            ALTER TABLE search_results_rebuilt RENAME TO search_results;
            CREATE INDEX IX_search_results_search_job_id_username
                ON search_results(search_job_id, username);
            CREATE UNIQUE INDEX IX_search_results_search_job_id_username_remote_filename_visibility
                ON search_results(search_job_id, username, remote_filename, visibility);

            CREATE TABLE search_view_files_rebuilt (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                admitted_revision INTEGER NOT NULL, sequence INTEGER NOT NULL,
                source_row_revision INTEGER NOT NULL, username TEXT NOT NULL,
                response_file_count INTEGER NOT NULL, filename TEXT NOT NULL,
                size_bytes INTEGER NOT NULL, bit_rate INTEGER NULL,
                bit_depth INTEGER NULL, sample_rate INTEGER NULL,
                duration_seconds INTEGER NULL, extension TEXT NOT NULL,
                upload_speed INTEGER NULL, has_free_upload_slot INTEGER NULL,
                queue_length INTEGER NULL, attributes_json TEXT NULL,
                observed_at_utc INTEGER NOT NULL, visibility TEXT NOT NULL,
                preference_tier TEXT NOT NULL,
                necessary_conditions_satisfied INTEGER NOT NULL DEFAULT 1,
                condition_matches_json TEXT NOT NULL,
                configured_conditions_json TEXT NOT NULL DEFAULT '[]',
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL,
                sort_tie INTEGER NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, username, filename, visibility),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);

            INSERT INTO search_view_files_rebuilt (view_id, item_ref, admitted_revision, sequence, source_row_revision, username, response_file_count, filename, size_bytes, bit_rate, bit_depth, sample_rate, duration_seconds, extension, upload_speed, has_free_upload_slot, queue_length, attributes_json, observed_at_utc, visibility, preference_tier, necessary_conditions_satisfied, condition_matches_json, configured_conditions_json, sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie)
                SELECT view_id, item_ref, admitted_revision, sequence, source_row_revision, username, response_file_count, filename, size_bytes, bit_rate, bit_depth, sample_rate, duration_seconds, extension, upload_speed, has_free_upload_slot, queue_length, CASE WHEN attributes_json IS NULL THEN NULL ELSE
                    (SELECT json_group_array(json_object('Type', json_extract(value, '$.Name'),
                        'Value', json_extract(value, '$.Value'), 'StableCode', json_extract(value, '$.Code')))
                     FROM json_each(attributes_json)) END, observed_at_utc, visibility, preference_tier, necessary_conditions_satisfied, condition_matches_json, configured_conditions_json, sort_high, sort_upload_fast, sort_mid, sort_inferred, sort_upload_medium, sort_bitrate, sort_tie FROM search_view_file_rows;
            DROP VIEW search_view_file_rows;
            DROP TABLE search_view_files;
            ALTER TABLE search_view_files_rebuilt RENAME TO search_view_files;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, item_ref, upload_speed, queue_length, size_bytes);
            DROP TABLE search_observations;
            DROP TABLE search_observation_sources;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
}
