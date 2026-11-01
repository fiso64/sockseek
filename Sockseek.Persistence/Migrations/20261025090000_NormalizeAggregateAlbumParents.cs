using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025090000_NormalizeAggregateAlbumParents")]
public sealed class NormalizeAggregateAlbumParents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            UPDATE jobs AS album
            SET parent_job_id = execution.parent_job_id
            FROM jobs AS execution
            JOIN jobs AS owner ON owner.id = execution.parent_job_id
            WHERE album.parent_job_id = execution.id
              AND album.kind = 'Album'
              AND execution.kind = 'JobList'
              AND owner.kind = 'AlbumAggregate'
              AND album.workflow_id = owner.workflow_id
              AND execution.workflow_id = owner.workflow_id;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A data correction, not a schema change. Keep the semantic parent on
        // downgrade rather than inventing an ambiguous execution-list membership.
    }
}
