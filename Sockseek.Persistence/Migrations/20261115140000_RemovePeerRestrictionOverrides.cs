using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261115140000_RemovePeerRestrictionOverrides")]
public sealed class RemovePeerRestrictionOverrides : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("peer_restriction_overrides");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Removed overrides cannot be restored; recreate only the old schema.
        migrationBuilder.CreateTable(
            name: "peer_restriction_overrides",
            columns: table => new
            {
                restriction_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY"),
                override_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_peer_restriction_overrides", x => new { x.restriction_kind, x.username }));
        migrationBuilder.CreateIndex(
            name: "IX_peer_restriction_overrides_username_restriction_kind",
            table: "peer_restriction_overrides",
            columns: ["username", "restriction_kind"]);
    }
}
