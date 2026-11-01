using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261025110000_ConsolidateSearchViewMembershipIndexes")]
public sealed class ConsolidateSearchViewMembershipIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, fileFirst: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => Rebuild(migrationBuilder, fileFirst: false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool fileFirst)
    {
        // The same uniqueness constraint can also serve file-to-directory lookup.
        // Keeping a second hash-keyed index rewrites most of its pages on each
        // arriving batch. Directory paging retains its two ordered indexes.
        string key = fileFirst ? "view_id, file_ref, directory_ref" : "view_id, directory_ref, file_ref";
        migrationBuilder.Sql($"""
            CREATE TABLE search_view_directory_files_rebuilt (
                view_id TEXT NOT NULL, directory_ref TEXT NOT NULL,
                file_ref TEXT NOT NULL, admitted_revision INTEGER NOT NULL,
                relative_path TEXT NOT NULL, removed_revision INTEGER NULL,
                PRIMARY KEY ({key}),
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, file_ref)
                    REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE);
            INSERT INTO search_view_directory_files_rebuilt
                SELECT view_id, directory_ref, file_ref, admitted_revision, relative_path, removed_revision
                FROM search_view_directory_files;
            DROP TABLE search_view_directory_files;
            ALTER TABLE search_view_directory_files_rebuilt RENAME TO search_view_directory_files;
            CREATE INDEX ix_search_view_directory_files_page
                ON search_view_directory_files(
                    view_id, directory_ref, admitted_revision, relative_path COLLATE BINARY, file_ref);
            CREATE INDEX ix_search_view_directory_files_path
                ON search_view_directory_files(
                    view_id, directory_ref, relative_path COLLATE BINARY, file_ref COLLATE BINARY);
            """);
        if (!fileFirst)
            migrationBuilder.Sql("""
                CREATE INDEX ix_search_view_directory_files_file
                    ON search_view_directory_files(
                        view_id, file_ref, directory_ref, admitted_revision, removed_revision);
                """);
    }
}
