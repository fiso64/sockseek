using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261101090000_RemoveUnusedJobCreationIndexes")]
public sealed class RemoveUnusedJobCreationIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_jobs_created_at_utc_id", table: "jobs");
        migrationBuilder.DropIndex(name: "IX_jobs_workflow_id_created_at_utc_id", table: "jobs");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(name: "IX_jobs_created_at_utc_id", table: "jobs",
            columns: ["created_at_utc", "id"]);
        migrationBuilder.CreateIndex(name: "IX_jobs_workflow_id_created_at_utc_id", table: "jobs",
            columns: ["workflow_id", "created_at_utc", "id"]);
    }
}
