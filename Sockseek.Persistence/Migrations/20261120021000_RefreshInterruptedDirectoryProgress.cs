using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sockseek.Persistence.Runtime;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261120021000_RefreshInterruptedDirectoryProgress")]
public sealed class RefreshInterruptedDirectoryProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql(InterruptedDirectoryProgress.Update(currentRuntimeOnly: false));

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
