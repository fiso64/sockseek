using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261127210000_SongAndAlbumSourceDetailIsThePeer")]
public sealed class SongAndAlbumSourceDetailIsThePeer : Migration
{
    // The source detail follows a job's type where it is listed. For a song or
    // album it now names only the peer a hand-picked download came from. Earlier
    // rows held "Automatic album download" or a position such as "CSV row 2";
    // none of them is a peer, so they are cleared.
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("UPDATE jobs SET source_detail = NULL WHERE kind IN ('Song', 'Album') AND source_detail IS NOT NULL;");

    protected override void Down(MigrationBuilder migration) { }
}
