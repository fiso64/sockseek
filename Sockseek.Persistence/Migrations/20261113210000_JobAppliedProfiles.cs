using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261113210000_JobAppliedProfiles")]
public sealed class JobAppliedProfiles : Migration
{
    // Older job rows did not retain profile history. It cannot be reconstructed
    // from today's catalog or a root's settings for individually composed children.
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>("applied_profiles_json", "jobs", type: "TEXT", nullable: false, defaultValue: "[]");

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("ALTER TABLE jobs DROP COLUMN applied_profiles_json;");
}
