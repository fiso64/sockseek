using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261114160000_OperationRetention")]
public sealed class OperationRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            -- Automatic discovery keeps its compact job payload, not raw alternatives.
            -- Deleting the search owner cascades through observation sources and facts.
            DELETE FROM search_jobs WHERE job_id IN (SELECT id FROM jobs WHERE kind <> 'Search');

            CREATE TEMP TABLE print_submissions AS
                SELECT DISTINCT submission_id AS id FROM jobs
                WHERE submission_id IS NOT NULL AND (print_option & 27) <> 0;

            -- Print flags are Jobs, Results, Link and Json. Full alone is a modifier.
            DELETE FROM jobs WHERE (print_option & 27) <> 0;

            DELETE FROM print_submissions WHERE EXISTS (
                SELECT 1 FROM jobs WHERE jobs.submission_id = print_submissions.id);
            UPDATE submissions SET rerun_of_submission_id = NULL
                WHERE rerun_of_submission_id IN (SELECT id FROM print_submissions);
            DELETE FROM submissions WHERE id IN (SELECT id FROM print_submissions);
            DROP TABLE print_submissions;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Discarded print history and automatic search observations cannot be restored.
    }
}
