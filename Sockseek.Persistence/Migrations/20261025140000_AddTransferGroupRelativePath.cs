using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025140000_AddTransferGroupRelativePath")]
public sealed class AddTransferGroupRelativePath : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("group_relative_path", "transfers", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("ALTER TABLE transfers DROP COLUMN group_relative_path;");
}
