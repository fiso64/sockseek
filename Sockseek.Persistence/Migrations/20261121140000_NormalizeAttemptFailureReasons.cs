using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261121140000_NormalizeAttemptFailureReasons")]
public sealed class NormalizeAttemptFailureReasons : Migration
{
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            UPDATE transfer_attempts
            SET failure_reason = CASE failure_reason
                WHEN 'TimedOut' THEN 'TransferTimedOut'
                WHEN 'Stale' THEN 'TransferTimedOut'
                WHEN 'PeerFailure' THEN 'Unknown'
                WHEN 'Finalization' THEN 'Unknown'
                WHEN 'NotShared' THEN 'FileNoLongerShared'
                WHEN 'Unavailable' THEN 'FileUnavailable'
                WHEN 'InternalFailure' THEN 'InternalError'
            END
            WHERE state = 'Failed' AND failure_reason IN
                ('TimedOut','Stale','PeerFailure','Finalization','NotShared','Unavailable','InternalFailure');
            """);

    protected override void Down(MigrationBuilder migration) { }
}
