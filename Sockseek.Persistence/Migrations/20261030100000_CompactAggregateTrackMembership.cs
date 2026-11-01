using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261030100000_CompactAggregateTrackMembership")]
public sealed class CompactAggregateTrackMembership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Parent keys are rebuilt together with their dependants. Retain every
        // public ref, revision and membership; do not repair old layouts on reads.
        // Versions retain view_id to enforce both group ownership and the FK to
        // a published revision. Member edges need only their parent group's key.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_aggregate_tracks_rebuilt (
                id INTEGER PRIMARY KEY,
                view_id TEXT NOT NULL REFERENCES search_views(id) ON DELETE CASCADE,
                item_ref TEXT NOT NULL, group_index INTEGER NOT NULL, query_json TEXT NOT NULL,
                UNIQUE(view_id, item_ref), UNIQUE(view_id, group_index), UNIQUE(view_id, id));
            INSERT INTO search_view_aggregate_tracks_rebuilt
                SELECT rowid, view_id, item_ref, group_index, query_json FROM search_view_aggregate_tracks;
            DROP TABLE search_view_aggregate_tracks;
            ALTER TABLE search_view_aggregate_tracks_rebuilt RENAME TO search_view_aggregate_tracks;
            CREATE TABLE search_view_aggregate_track_versions_rebuilt (
                view_id TEXT NOT NULL, group_id INTEGER NOT NULL, revision INTEGER NOT NULL,
                share_count INTEGER NOT NULL, selectable_option_count INTEGER NOT NULL,
                representative_file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                PRIMARY KEY(group_id, revision),
                FOREIGN KEY(view_id, group_id) REFERENCES search_view_aggregate_tracks(view_id, id) ON DELETE CASCADE,
                FOREIGN KEY(view_id, revision) REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE) WITHOUT ROWID;
            INSERT INTO search_view_aggregate_track_versions_rebuilt
                SELECT version.view_id, aggregate.id, version.revision, version.share_count,
                       version.selectable_option_count, version.representative_file_id
                FROM search_view_aggregate_track_versions version
                JOIN search_view_aggregate_tracks aggregate
                  ON aggregate.view_id=version.view_id AND aggregate.item_ref=version.item_ref;
            DROP TABLE search_view_aggregate_track_versions;
            ALTER TABLE search_view_aggregate_track_versions_rebuilt RENAME TO search_view_aggregate_track_versions;
            CREATE INDEX ix_search_view_aggregate_track_versions_revision
                ON search_view_aggregate_track_versions(view_id, revision);
            CREATE INDEX ix_search_view_aggregate_track_versions_file
                ON search_view_aggregate_track_versions(representative_file_id);
            CREATE TABLE search_view_aggregate_track_files_rebuilt (
                group_id INTEGER NOT NULL REFERENCES search_view_aggregate_tracks(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                admitted_revision INTEGER NOT NULL, PRIMARY KEY(group_id, file_id)) WITHOUT ROWID;
            INSERT INTO search_view_aggregate_track_files_rebuilt
                SELECT aggregate.id, membership.file_id, membership.admitted_revision
                FROM search_view_aggregate_track_files membership
                JOIN search_view_aggregate_tracks aggregate
                  ON aggregate.view_id=membership.view_id AND aggregate.item_ref=membership.group_ref;
            DROP TABLE search_view_aggregate_track_files;
            ALTER TABLE search_view_aggregate_track_files_rebuilt RENAME TO search_view_aggregate_track_files;
            CREATE INDEX ix_search_view_aggregate_track_files_page
                ON search_view_aggregate_track_files(group_id, admitted_revision, file_id);
            CREATE INDEX ix_search_view_aggregate_track_files_file ON search_view_aggregate_track_files(file_id);
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Parent keys are rebuilt together with their dependants. Retain every
        // public ref, revision and membership; do not repair old layouts on reads.
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_aggregate_track_files_rebuilt (
                view_id TEXT NOT NULL, group_ref TEXT NOT NULL, file_id INTEGER NOT NULL,
                admitted_revision INTEGER NOT NULL, PRIMARY KEY(view_id, group_ref, file_id),
                FOREIGN KEY(view_id, group_ref) REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY(file_id) REFERENCES search_view_files(id) ON DELETE CASCADE) WITHOUT ROWID;
            INSERT INTO search_view_aggregate_track_files_rebuilt
                SELECT aggregate.view_id, aggregate.item_ref, membership.file_id, membership.admitted_revision
                FROM search_view_aggregate_track_files membership
                JOIN search_view_aggregate_tracks aggregate ON aggregate.id=membership.group_id;
            DROP TABLE search_view_aggregate_track_files;
            ALTER TABLE search_view_aggregate_track_files_rebuilt RENAME TO search_view_aggregate_track_files;
            CREATE INDEX ix_search_view_aggregate_track_files_page
                ON search_view_aggregate_track_files(view_id, group_ref, admitted_revision, file_id);
            CREATE INDEX ix_search_view_aggregate_track_files_file ON search_view_aggregate_track_files(file_id);
            CREATE TABLE search_view_aggregate_track_versions_rebuilt (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                share_count INTEGER NOT NULL, selectable_option_count INTEGER NOT NULL,
                representative_file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                PRIMARY KEY(view_id, item_ref, revision),
                FOREIGN KEY(view_id, item_ref) REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY(view_id, revision) REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_aggregate_track_versions_rebuilt
                SELECT version.view_id, aggregate.item_ref, version.revision, version.share_count,
                       version.selectable_option_count, version.representative_file_id
                FROM search_view_aggregate_track_versions version
                JOIN search_view_aggregate_tracks aggregate ON aggregate.id=version.group_id;
            DROP TABLE search_view_aggregate_track_versions;
            ALTER TABLE search_view_aggregate_track_versions_rebuilt RENAME TO search_view_aggregate_track_versions;
            CREATE INDEX ix_search_view_aggregate_track_versions_page
                ON search_view_aggregate_track_versions(view_id, revision, share_count DESC, item_ref);
            CREATE INDEX ix_search_view_aggregate_track_versions_file ON search_view_aggregate_track_versions(representative_file_id);
            CREATE TABLE search_view_aggregate_tracks_rebuilt (
                view_id TEXT NOT NULL REFERENCES search_views(id) ON DELETE CASCADE,
                item_ref TEXT NOT NULL, group_index INTEGER NOT NULL, query_json TEXT NOT NULL,
                PRIMARY KEY(view_id, item_ref), UNIQUE(view_id, group_index));
            INSERT INTO search_view_aggregate_tracks_rebuilt
                SELECT view_id, item_ref, group_index, query_json FROM search_view_aggregate_tracks;
            DROP TABLE search_view_aggregate_tracks;
            ALTER TABLE search_view_aggregate_tracks_rebuilt RENAME TO search_view_aggregate_tracks;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

}
