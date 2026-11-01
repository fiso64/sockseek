using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025170000_CoverSearchViewFileOrdering")]
public sealed class CoverSearchViewFileOrdering : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, coverAlternativeOrders: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, coverAlternativeOrders: false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool coverAlternativeOrders)
    {
        // Keep one paging index. Alternative orders need these raw values to
        // choose page IDs without fetching every candidate's full file row.
        string extra = coverAlternativeOrders ? ", upload_speed, queue_length, size_bytes" : "";
        migrationBuilder.Sql($"""
            DROP INDEX ix_search_view_files_page;
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, item_ref{extra});
            """);
    }
}
