using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261024090000_AddSearchViewFileMembershipIndex")]
public sealed class AddSearchViewFileMembershipIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE INDEX ix_search_view_directory_files_file
                ON search_view_directory_files(
                    view_id, file_ref, directory_ref, admitted_revision, removed_revision);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP INDEX ix_search_view_directory_files_file;");
}
