using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261001090000_AddTransferGroupIndex")]
public sealed class AddTransferGroupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateIndex(
            name: "IX_transfers_group_ref_username_direction_archived_at_utc",
            table: "transfers",
            columns: ["group_ref", "username", "direction", "archived_at_utc"]);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropIndex(
            name: "IX_transfers_group_ref_username_direction_archived_at_utc",
            table: "transfers");
}
