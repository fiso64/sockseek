using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransferDirectionTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_transfers_archived_at_utc_direction_created_at_utc_id",
                table: "transfers",
                columns: new[] { "archived_at_utc", "direction", "created_at_utc", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transfers_archived_at_utc_direction_created_at_utc_id",
                table: "transfers");
        }
    }
}
