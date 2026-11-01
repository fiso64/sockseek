using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030140000_RemoveUnpublishedTrackCandidates")]
public sealed class RemoveUnpublishedTrackCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older publications wrote candidates before their group passed its
        // visibility rules. Keep every historical option; only orphan View
        // memberships disappear. Canonical observations and accepted jobs stay.
        // Materialize the file-ID set once: a correlated scan without a file-
        // first membership index would be quadratic on large retained Views.
        migrationBuilder.Sql("""
            DELETE FROM search_view_files
            WHERE view_id IN (SELECT storage_id FROM search_views WHERE projection_kind='AggregateTracks')
                AND id NOT IN (SELECT file_id FROM search_view_aggregate_track_files);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Discarded derived memberships have no selectable historical group.
        // Downgrading retains every published option and its source facts.
    }
}
