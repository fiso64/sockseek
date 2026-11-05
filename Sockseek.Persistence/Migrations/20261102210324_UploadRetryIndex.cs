using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UploadRetryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing terminal uploads follow the same visible-latest policy.
            // Their attempts and accounting remain retained.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id, row_number() OVER (
                        PARTITION BY username, remote_path
                        ORDER BY created_at_utc DESC, last_sequence DESC, id DESC) AS ordinal
                    FROM transfers WHERE direction = 'Upload' AND archived_at_utc IS NULL)
                UPDATE transfers SET archived_at_utc = COALESCE(completed_at_utc, created_at_utc)
                WHERE terminal_outcome <> 'None' AND id IN (SELECT id FROM ranked WHERE ordinal > 1);
                """);
            migrationBuilder.CreateIndex(
                name: "IX_transfers_username_remote_path",
                table: "transfers",
                columns: new[] { "username", "remote_path" },
                filter: "direction = 'Upload' AND archived_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transfers_username_remote_path",
                table: "transfers");
        }
    }
}
