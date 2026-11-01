using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260923120000_AddSearchViewDirectoryPathIndex")]
public sealed class AddSearchViewDirectoryPathIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE INDEX ix_search_view_directory_files_path
                ON search_view_directory_files(
                    view_id, directory_ref,
                    relative_path COLLATE BINARY, file_ref COLLATE BINARY);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP INDEX ix_search_view_directory_files_path;");
}
