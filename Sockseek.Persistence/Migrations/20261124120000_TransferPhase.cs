using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261124120000_TransferPhase")]
public sealed class TransferPhase : Migration
{
    // Transfer state was free-form text mixing Soulseek.NET flag names with
    // daemon-owned labels. It now stores one TransferPhase name; the result
    // of an ended transfer is its terminal outcome.
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            UPDATE transfers
            SET state = CASE
                WHEN terminal_outcome <> 'None' THEN 'Ended'
                WHEN state = 'Queued, Remotely' THEN 'QueuedRemotely'
                WHEN state IN ('Queued, Locally', 'Queued') THEN 'QueuedLocally'
                WHEN state IN ('Requested', 'Initializing', 'InProgress') THEN state
                WHEN state = 'AttemptCompleted' THEN 'Finalizing'
                WHEN state IN ('AttemptFailed', 'AttemptCancelled') THEN 'Retrying'
                ELSE 'Starting'
            END;
            """);

    protected override void Down(MigrationBuilder migration) { }
}
