using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261029100000_IndexSearchDirectoryRanking")]
public sealed class IndexSearchDirectoryRanking : Migration
{
    private static readonly string[] RankingColumns =
        ["sort_high", "sort_upload_fast", "sort_mid", "sort_inferred",
         "sort_upload_medium", "sort_bitrate", "sort_tie", "sequence"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (string column in RankingColumns)
            migrationBuilder.Sql($"ALTER TABLE search_view_directory_versions ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;");
        // Existing versions retain exactly their chosen file's immutable keys.
        // New publications supply these keys with the directory contribution.
        migrationBuilder.Sql($"""
            UPDATE search_view_directory_versions SET
                {string.Join(", ", RankingColumns.Select(column => column + " = file." + column))}
            FROM search_view_files file
            WHERE file.id = search_view_directory_versions.best_file_id;
            DROP INDEX ix_search_view_directory_versions_revision;
            CREATE INDEX ix_search_view_directory_versions_relevance ON search_view_directory_versions(
                view_id, sort_high DESC, sort_upload_fast DESC, sort_mid DESC,
                sort_inferred DESC, sort_upload_medium DESC, sort_bitrate DESC,
                sort_tie DESC, sequence, item_ref, revision) WHERE is_removed = 0;
            """);
        // The primary key already supports latest-version lookups/grouping.
        // Replace the revision index rather than maintaining both per arrival.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX ix_search_view_directory_versions_relevance;");
        foreach (string column in RankingColumns)
            migrationBuilder.Sql($"ALTER TABLE search_view_directory_versions DROP COLUMN {column};");
        migrationBuilder.Sql("""
            CREATE INDEX ix_search_view_directory_versions_revision
                ON search_view_directory_versions(view_id, revision, item_ref);
            """);
    }
}
