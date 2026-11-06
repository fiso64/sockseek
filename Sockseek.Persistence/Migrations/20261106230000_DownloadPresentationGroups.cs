using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

public partial class DownloadPresentationGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "group_kind", table: "transfers", type: "TEXT", nullable: false, defaultValue: "Directory");
        migrationBuilder.Sql("UPDATE transfers SET group_kind = 'Files' WHERE direction = 'Upload';");
        // Match DownloadEvents' exact folder identity and submission scope once
        // for retained files. No path case folding or separator normalization is
        // applied to the identity; the normalized tail only locates separators.
        migrationBuilder.Sql("""
            WITH RECURSIVE parts(id, scope, path, tail, offset) AS (
                SELECT t.id, lower(coalesce(j.submission_id, t.workflow_id)),
                       t.remote_path, replace(t.remote_path, '/', char(92)), 0
                FROM transfers t LEFT JOIN jobs j ON j.id = t.job_id
                WHERE t.direction = 'Download' AND t.source = 'SoulseekPeer'
                  AND t.group_ref IS NULL AND t.remote_path IS NOT NULL
                  AND coalesce(j.submission_id, t.workflow_id) IS NOT NULL
                UNION ALL
                SELECT id, scope, path, substr(tail, instr(tail, char(92)) + 1),
                       offset + instr(tail, char(92))
                FROM parts WHERE instr(tail, char(92)) > 0
            ), groups AS (
                SELECT id, 'files:' || scope || ':' || substr(path, 1, offset - 1) AS ref,
                       substr(path, 1, offset - 1) AS folder, substr(path, offset + 1) AS filename
                FROM parts WHERE instr(tail, char(92)) = 0 AND offset > 1
            )
            UPDATE transfers SET group_ref = groups.ref, group_display_path = groups.folder,
                                 group_relative_path = groups.filename, group_kind = 'Files'
            FROM groups WHERE transfers.id = groups.id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE transfers SET group_ref = NULL, group_display_path = NULL, group_relative_path = NULL
            WHERE direction = 'Download' AND group_kind = 'Files';
            """);
        migrationBuilder.DropColumn(name: "group_kind", table: "transfers");
    }
}
