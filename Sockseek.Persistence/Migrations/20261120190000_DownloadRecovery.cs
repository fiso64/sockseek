using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261120190000_DownloadRecovery")]
public sealed class DownloadRecovery : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<bool>("recovery_pending", "transfers", nullable: false, defaultValue: false);
        migration.AddColumn<Guid>("retry_job_id", "transfers", nullable: true);
        migration.CreateIndex("IX_transfers_recovery_pending", "transfers", "recovery_pending", filter: "recovery_pending = 1");
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropIndex("IX_transfers_recovery_pending", "transfers");
        migration.DropColumn("retry_job_id", "transfers");
        migration.DropColumn("recovery_pending", "transfers");
    }
}
