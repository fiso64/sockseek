using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261101130000_RemoveTemporaryArtifactPins")]
public sealed class RemoveTemporaryArtifactPins : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("input_artifact_pins");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "input_artifact_pins",
            columns: table => new
            {
                artifact_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                owner_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                owner_id = table.Column<Guid>(type: "TEXT", nullable: false),
                created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_input_artifact_pins",
                    x => new { x.artifact_id, x.owner_kind, x.owner_id });
                table.ForeignKey(
                    name: "FK_input_artifact_pins_input_artifacts_artifact_id",
                    column: x => x.artifact_id,
                    principalTable: "input_artifacts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_input_artifact_pins_owner_kind_owner_id",
            table: "input_artifact_pins",
            columns: ["owner_kind", "owner_id"]);
    }
}
