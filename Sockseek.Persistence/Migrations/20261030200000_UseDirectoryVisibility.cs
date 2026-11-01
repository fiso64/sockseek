using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030200000_UseDirectoryVisibility")]
public sealed class UseDirectoryVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Old Aggregate Album Views published folders independently of group
        // visibility, so their directory histories lack group-hidden intervals.
        // Expire that derived cache once; raw source observations and accepted
        // jobs are separate resources and remain available. Other View histories
        // already retire whole directories and retain their exact references.
        migrationBuilder.Sql("""
            DELETE FROM search_views WHERE projection_kind='AggregateAlbums';
            DROP INDEX ix_search_view_directory_files_page;
            ALTER TABLE search_view_directory_files DROP COLUMN removed_revision;
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(
                directory_id, admitted_revision, file_id, relative_path);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore the old physical shape for schema downgrades. Directory version
        // history remains authoritative; old binaries cannot represent restoration
        // gaps in every reader and require their derived Views to be recreated.
        migrationBuilder.Sql("""
            DROP INDEX ix_search_view_directory_files_page;
            ALTER TABLE search_view_directory_files ADD COLUMN removed_revision INTEGER NULL;
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(
                directory_id, admitted_revision, file_id, removed_revision, relative_path);
            """);
    }
}
