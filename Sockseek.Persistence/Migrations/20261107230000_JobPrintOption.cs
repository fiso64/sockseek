using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261107230000_JobPrintOption")]
public sealed class JobPrintOption : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("print_option", "jobs", type: "INTEGER", nullable: false, defaultValue: 0);
        // Older history discarded this execution flag. Recover the requested
        // print mode from accepted intent; new writes retain each job's actual mode.
        migrationBuilder.Sql("""
            UPDATE jobs SET print_option = COALESCE((
                SELECT json_extract(json_extract(specification_json, '$.effectiveSettingsJson'), '$.printOption')
                FROM submission_specifications WHERE submission_id = jobs.submission_id
            ), 0);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("ALTER TABLE jobs DROP COLUMN print_option;");
}
