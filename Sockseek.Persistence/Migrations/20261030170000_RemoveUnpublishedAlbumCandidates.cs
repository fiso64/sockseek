using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030170000_RemoveUnpublishedAlbumCandidates")]
public sealed class RemoveUnpublishedAlbumCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older publications admitted file rows before their album passed folder
        // rules. Preserve every historical child/best-child reference, including
        // removed folders. Source observations and accepted work remain intact.
        // Uncorrelated sets avoid a per-file scan of the membership tables.
        migrationBuilder.Sql("""
            DELETE FROM search_view_files
            WHERE view_id IN (SELECT storage_id FROM search_views
                WHERE projection_kind IN ('AlbumDirectories', 'AggregateAlbums'))
                AND id NOT IN (SELECT file_id FROM search_view_directory_files)
                AND id NOT IN (SELECT best_file_id FROM search_view_directory_versions);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Removed derived candidates have no historical directory membership.
    }
}
