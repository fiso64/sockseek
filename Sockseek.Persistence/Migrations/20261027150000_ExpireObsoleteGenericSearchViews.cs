using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261027150000_ExpireObsoleteGenericSearchViews")]
public sealed class ExpireObsoleteGenericSearchViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            -- These derived views used exact-folder grouping or omitted bare
            -- files. Discard that obsolete cache once rather than scanning and
            -- repairing results on requests. Cascades remove only its projected
            -- rows; source facts, search history and accepted jobs remain intact.
            DELETE FROM search_views
            WHERE projection_kind = 'GenericDirectories'
              AND CASE WHEN json_valid(definition_json)
                THEN json_extract(definition_json, '$.genericDirectoryGroupingVersion') IS NOT 2
                ELSE 1 END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Expired derived caches cannot be restored; create a fresh Search View.
    }
}
