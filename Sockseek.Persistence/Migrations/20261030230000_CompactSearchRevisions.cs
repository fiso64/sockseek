using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030230000_CompactSearchRevisions")]
public sealed class CompactSearchRevisions : Migration
{
    private const string RevisionColumns = """
        view_id,revision,source_revision,consumed_sequence,is_complete,retention_state,
        public_file_count,locked_file_count,public_bytes,locked_bytes,observed_peer_count,
        projected_file_count,projected_public_file_count,projected_locked_file_count,
        preferred_file_count,other_file_count,top_level_item_count,selectable_option_count
        """;
    private const string RevisionFacts = """
        source_revision INTEGER NOT NULL,consumed_sequence INTEGER NOT NULL,
        is_complete INTEGER NOT NULL,retention_state TEXT NOT NULL,
        public_file_count INTEGER NOT NULL,locked_file_count INTEGER NOT NULL,
        public_bytes INTEGER NOT NULL,locked_bytes INTEGER NOT NULL,observed_peer_count INTEGER NOT NULL,
        projected_file_count INTEGER NOT NULL,projected_public_file_count INTEGER NOT NULL,
        projected_locked_file_count INTEGER NOT NULL,preferred_file_count INTEGER NOT NULL,
        other_file_count INTEGER NOT NULL,top_level_item_count INTEGER NOT NULL,selectable_option_count INTEGER NOT NULL
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql($"""
            CREATE UNIQUE INDEX ix_search_views_storage_identity ON search_views(storage_id,id);
            ALTER TABLE search_view_revisions RENAME TO search_view_revision_records;
            CREATE TABLE search_view_revision_records_rebuilt(
                storage_id INTEGER PRIMARY KEY,view_storage_id INTEGER NOT NULL,
                view_id TEXT NOT NULL,revision INTEGER NOT NULL,{RevisionFacts},
                UNIQUE(view_id,revision),UNIQUE(view_storage_id,revision,storage_id),
                FOREIGN KEY(view_storage_id,view_id) REFERENCES search_views(storage_id,id) ON DELETE CASCADE);
            INSERT INTO search_view_revision_records_rebuilt(storage_id,view_storage_id,{RevisionColumns})
                SELECT revision.rowid,owner.storage_id,{Qualified(RevisionColumns, "revision")}
                FROM search_view_revision_records revision JOIN search_views owner ON owner.id=revision.view_id;
            DROP TABLE search_view_revision_records;
            ALTER TABLE search_view_revision_records_rebuilt RENAME TO search_view_revision_records;
            CREATE VIEW search_view_revisions AS SELECT {RevisionColumns} FROM search_view_revision_records;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql($"""
            DROP VIEW search_view_revisions;
            CREATE TABLE search_view_revision_records_rebuilt(
                view_id TEXT NOT NULL,revision INTEGER NOT NULL,{RevisionFacts},
                PRIMARY KEY(view_id,revision),FOREIGN KEY(view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            INSERT INTO search_view_revision_records_rebuilt(rowid,{RevisionColumns})
                SELECT storage_id,{RevisionColumns} FROM search_view_revision_records;
            DROP TABLE search_view_revision_records;
            ALTER TABLE search_view_revision_records_rebuilt RENAME TO search_view_revision_records;
            ALTER TABLE search_view_revision_records RENAME TO search_view_revisions;
            DROP INDEX ix_search_views_storage_identity;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    private static string Qualified(string columns, string alias)
        => string.Join(",", columns.Split(',').Select(column => alias + "." + column.Trim()));
}
