using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261028190000_CompactSearchConditionFacts")]
public sealed class CompactSearchConditionFacts : Migration
{
    // Freeze this migration's identities independently of future enum additions.
    private static readonly string[] Conditions =
        ["Format", "Length", "Bitrate", "SampleRate", "BitDepth", "Title", "Artist", "Album", "Username"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP VIEW search_view_file_rows;
            ALTER TABLE search_view_files ADD COLUMN matched_condition_flags INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE search_view_files ADD COLUMN configured_condition_flags INTEGER NOT NULL DEFAULT 0;
            UPDATE search_view_files SET
                matched_condition_flags = {Flags("condition_matches_json")},
                configured_condition_flags = {Flags("configured_conditions_json")};
            ALTER TABLE search_view_files DROP COLUMN condition_matches_json;
            ALTER TABLE search_view_files DROP COLUMN configured_conditions_json;
            """);
        CreateFileRows(migrationBuilder, compact: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP VIEW search_view_file_rows;
            ALTER TABLE search_view_files ADD COLUMN condition_matches_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE search_view_files ADD COLUMN configured_conditions_json TEXT NOT NULL DEFAULT '[]';
            UPDATE search_view_files SET
                condition_matches_json = {Names("matched_condition_flags")},
                configured_conditions_json = {Names("configured_condition_flags")};
            ALTER TABLE search_view_files DROP COLUMN matched_condition_flags;
            ALTER TABLE search_view_files DROP COLUMN configured_condition_flags;
            """);
        CreateFileRows(migrationBuilder, compact: false);
    }

    private static string Flags(string column)
        => "(SELECT coalesce(sum(DISTINCT CASE value "
            + string.Join(" ", Conditions.Select((name, code) => $"WHEN '{name}' THEN {1L << code}"))
            + $" ELSE 0 END), 0) FROM json_each({column}))";

    private static string Names(string column)
        => "(SELECT json_group_array(name) FROM ("
            + string.Join(" UNION ALL ", Conditions.Select((name, code) =>
                $"SELECT '{name}' AS name WHERE ({column} & {1L << code}) != 0"))
            + "))";

    private static void CreateFileRows(MigrationBuilder migrationBuilder, bool compact)
    {
        string conditions = compact
            ? "member.matched_condition_flags, member.configured_condition_flags"
            : "member.condition_matches_json, member.configured_conditions_json";
        migrationBuilder.Sql($"""
            CREATE VIEW search_view_file_rows AS
                SELECT member.id AS rowid, member.id, owner.id AS view_id,
                    lower(hex(member.ref_key)) AS item_ref, member.ref_key,
                    member.admitted_revision, member.observation_id, member.sequence,
                    member.source_row_revision, member.size_bytes, member.upload_speed,
                    member.queue_length, member.preference_tier, member.necessary_conditions_satisfied,
                    {conditions}, member.sort_high, member.sort_upload_fast, member.sort_mid,
                    member.sort_inferred, member.sort_upload_medium, member.sort_bitrate, member.sort_tie,
                    fact.username, fact.response_file_count, fact.filename, fact.bit_rate, fact.bit_depth,
                    fact.sample_rate, fact.duration_seconds, fact.extension, fact.has_free_upload_slot,
                    fact.attributes_json, fact.observed_at_utc, fact.visibility
                FROM search_view_files member JOIN search_views owner ON owner.storage_id=member.view_id
                JOIN search_observations fact ON fact.id=member.observation_id;
            """);
    }
}
