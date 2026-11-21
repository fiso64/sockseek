using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261121150000_AttemptCancellationSource")]
public sealed class AttemptCancellationSource : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<string>("cancellation_source", "transfer_attempts", type: "TEXT", maxLength: 64,
            nullable: false, defaultValue: "None");
        migration.Sql("""
            UPDATE transfer_attempts AS a
            SET cancellation_source = CASE
                    WHEN state = 'Interrupted' OR failure_reason = 'DaemonShutdown' THEN 'DaemonShutdown'
                    WHEN failure_reason IN ('ManualSkip','Requested') THEN 'User'
                    WHEN failure_reason IN ('User','Peer','CatalogInvalidation') THEN failure_reason
                    ELSE COALESCE((SELECT cancellation_source FROM transfers t
                        WHERE t.id = a.transfer_id AND t.direction = 'Upload' AND t.terminal_outcome = 'Cancelled'
                            AND t.attempt_count = a.attempt_number
                            AND t.cancellation_source IN ('User','Peer','DaemonShutdown','CatalogInvalidation')), 'None')
                END,
                failure_reason = 'None'
            WHERE state IN ('Cancelled','Interrupted');

            UPDATE transfers SET failure_reason = 'None'
            WHERE terminal_outcome = 'Interrupted' AND failure_reason = 'Interrupted';
            """);
    }

    protected override void Down(MigrationBuilder migration)
        => migration.DropColumn("cancellation_source", "transfer_attempts");
}
