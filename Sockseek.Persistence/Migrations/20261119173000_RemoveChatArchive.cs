using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261119173000_RemoveChatArchive")]
public sealed class RemoveChatArchive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("ALTER TABLE chat_conversations DROP COLUMN archived_at_utc;");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<long>("archived_at_utc", "chat_conversations", type: "INTEGER", nullable: true);
}
