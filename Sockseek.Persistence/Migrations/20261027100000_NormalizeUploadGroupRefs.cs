using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261027100000_NormalizeUploadGroupRefs")]
public sealed class NormalizeUploadGroupRefs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            -- Path-based groups did not record admission-run boundaries. Give
            -- ambiguous historical entries individual identities rather than
            -- combining unrelated uploads or guessing membership at read time.
            UPDATE transfers
            SET group_display_path = coalesce(group_display_path, group_ref),
                group_ref = 'upload-run:' || lower(id)
            WHERE direction = 'Upload' COLLATE NOCASE AND group_ref IS NOT NULL
                AND (group_display_path IS NULL OR group_ref = group_display_path);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data normalization is irreversible: a display path is not a run ID.
    }
}
