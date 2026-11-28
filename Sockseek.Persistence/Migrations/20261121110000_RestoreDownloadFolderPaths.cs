using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261121110000_RestoreDownloadFolderPaths")]
public sealed class RestoreDownloadFolderPaths : Migration
{
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            -- A short label can be expanded only if every member ends in
            -- /label/relative_path and supplies the same exact folder prefix.
            -- Slice bytes so embedded NULs and the peer's separators survive.
            WITH members AS (
                SELECT group_ref, username, remote_path, group_display_path, group_relative_path,
                       CAST(replace(remote_path, '/', '\') AS BLOB) AS normalized_path,
                       CAST('\' || group_display_path || '\' || replace(group_relative_path, '/', '\') AS BLOB) AS suffix
                FROM transfers
                WHERE direction = 'Download' AND group_kind = 'Directory' AND group_ref IS NOT NULL
            ), candidates AS (
                SELECT group_ref, username,
                       CASE WHEN group_display_path <> '' AND group_relative_path <> ''
                                 AND instr(group_display_path, '/') = 0 AND instr(group_display_path, '\') = 0
                                 AND substr(normalized_path, -length(suffix)) = suffix
                            THEN substr(CAST(remote_path AS BLOB), 1,
                                        length(normalized_path) - length(CAST(group_relative_path AS BLOB)) - 1)
                       END AS root
                FROM members
            ), agreed AS MATERIALIZED (
                SELECT group_ref, username, CAST(min(root) AS TEXT) AS root
                FROM candidates
                GROUP BY group_ref, username
                HAVING count(root) = count(*) AND min(root) = max(root)
            )
            UPDATE transfers AS t SET group_display_path = agreed.root, revision = t.revision + 1
            FROM agreed
            WHERE t.direction = 'Download' AND t.group_kind = 'Directory'
              AND t.group_ref = agreed.group_ref AND t.username = agreed.username;
            """);

    protected override void Down(MigrationBuilder migration) { }
}
