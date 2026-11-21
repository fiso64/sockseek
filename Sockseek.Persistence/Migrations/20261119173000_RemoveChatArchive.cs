using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261119173000_RemoveChatArchive")]
public sealed class RemoveChatArchive : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            -- Delete chat used to clear the history and then archive the empty conversation.
            DELETE FROM chat_conversations
            WHERE archived_at_utc IS NOT NULL AND last_message_sequence = 0 AND last_read_sequence = 0
              AND NOT EXISTS (
                SELECT 1 FROM chat_messages m
                WHERE m.local_account_key = chat_conversations.local_account_key
                  AND m.target_id = chat_conversations.conversation_id AND m.target_kind = 'Direct'
              );
            ALTER TABLE chat_conversations DROP COLUMN archived_at_utc;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<long>("archived_at_utc", "chat_conversations", type: "INTEGER", nullable: true);
}
