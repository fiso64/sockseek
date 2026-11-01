using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261016090000_SeparateSubmissionSpecifications")]
public sealed class SeparateSubmissionSpecifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "submission_specifications",
            columns: table => new
            {
                submission_id = table.Column<Guid>(type: "TEXT", nullable: false),
                specification_json = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_submission_specifications", x => x.submission_id);
                table.ForeignKey("FK_submission_specifications_submissions_submission_id",
                    x => x.submission_id, "submissions", "id", onDelete: ReferentialAction.Cascade);
            });
        // Copy verbatim, including intent from unknown schema versions. The
        // migration transaction owns both the copy and removal of the old column.
        // Native DROP COLUMN preserves existing indexes and rerun foreign keys;
        // our packaged SQLite supports it without disabling foreign-key checks.
        migrationBuilder.Sql("""
            INSERT INTO submission_specifications (submission_id, specification_json)
            SELECT id, specification_json FROM submissions;
            ALTER TABLE submissions DROP COLUMN specification_json;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "specification_json", table: "submissions",
            type: "TEXT", nullable: false, defaultValue: "{}");
        migrationBuilder.Sql("""
            UPDATE submissions SET specification_json = (
                SELECT specification_json FROM submission_specifications
                WHERE submission_id = submissions.id);
            """);
        migrationBuilder.DropTable(name: "submission_specifications");
    }
}
