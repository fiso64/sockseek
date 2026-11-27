using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261127200000_ClearAutomaticSourceDetail")]
public sealed class ClearAutomaticSourceDetail : Migration
{
    // The source detail names where a job came from. Song and album downloads
    // that had no source used to say so in words; they now leave it empty, and
    // a hand-picked one names its peer.
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            UPDATE jobs SET source_detail = NULL
            WHERE source_detail IN ('Automatic song download', 'Automatic album download');
            """);

    protected override void Down(MigrationBuilder migration) { }
}
