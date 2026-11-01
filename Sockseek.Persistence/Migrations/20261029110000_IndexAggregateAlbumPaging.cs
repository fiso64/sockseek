using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261029110000_IndexAggregateAlbumPaging")]
public sealed class IndexAggregateAlbumPaging : Migration
{
    // Replace the revision-first index; the primary key still resolves latest
    // versions, including removals, without another index to maintain per arrival.
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP INDEX ix_search_view_aggregate_album_versions_page;
            CREATE INDEX ix_search_view_aggregate_album_versions_page
                ON search_view_aggregate_album_versions(
                    view_id, share_count DESC, group_index, item_ref, revision)
                WHERE is_removed = 0;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            DROP INDEX ix_search_view_aggregate_album_versions_page;
            CREATE INDEX ix_search_view_aggregate_album_versions_page
                ON search_view_aggregate_album_versions(
                    view_id, revision, share_count DESC, group_index, item_ref);
            """);
}
