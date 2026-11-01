using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261031010000_CompactSearchDirectories")]
public sealed class CompactSearchDirectories : Migration
{
    // Public refs occur once in the catalog. Versions/edges own compact keys;
    // read views expose their facts without repeating GUIDs and refs per version.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_directory_records(
             id INTEGER PRIMARY KEY,view_storage_id INTEGER NOT NULL REFERENCES search_views(storage_id) ON DELETE CASCADE,
             item_ref TEXT NOT NULL,username TEXT NOT NULL,folder_path TEXT NOT NULL,
             UNIQUE(view_storage_id,item_ref),UNIQUE(view_storage_id,username,folder_path));
            INSERT INTO search_view_directory_records
             SELECT d.id,v.storage_id,d.item_ref,d.username,d.folder_path
             FROM search_view_directories d JOIN search_views v ON v.id=d.view_id;
            CREATE TABLE search_view_directory_version_records(
             directory_id INTEGER NOT NULL REFERENCES search_view_directory_records(id) ON DELETE CASCADE,
             view_storage_id INTEGER NOT NULL,revision INTEGER NOT NULL,revision_id INTEGER NOT NULL,
             best_file_id INTEGER NOT NULL,public_matching_count INTEGER NOT NULL,locked_matching_count INTEGER NOT NULL,
                public_matching_bytes INTEGER NOT NULL,locked_matching_bytes INTEGER NOT NULL,is_fully_retrieved INTEGER NOT NULL,
                retrieved_file_count INTEGER,retrieved_bytes INTEGER,is_removed INTEGER NOT NULL,sort_high INTEGER NOT NULL,
                sort_upload_fast INTEGER NOT NULL,sort_mid INTEGER NOT NULL,sort_inferred INTEGER NOT NULL,sort_upload_medium INTEGER NOT NULL,
                sort_bitrate INTEGER NOT NULL,sort_tie INTEGER NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(directory_id,revision),
             FOREIGN KEY(view_storage_id,revision,revision_id) REFERENCES search_view_revision_records(view_storage_id,revision,
                storage_id) ON DELETE CASCADE,
             FOREIGN KEY(best_file_id) REFERENCES search_view_files(id) ON DELETE CASCADE) WITHOUT ROWID;
            INSERT INTO search_view_directory_version_records
             SELECT d.id,r.view_storage_id,v.revision,r.storage_id,v.best_file_id,v.public_matching_count,v.locked_matching_count,
                v.public_matching_bytes,v.locked_matching_bytes,v.is_fully_retrieved,v.retrieved_file_count,v.retrieved_bytes,v.is_removed,
                v.sort_high,v.sort_upload_fast,v.sort_mid,v.sort_inferred,v.sort_upload_medium,v.sort_bitrate,v.sort_tie,v.sequence
             FROM search_view_directory_versions v JOIN search_view_directories d ON d.view_id=v.view_id AND d.item_ref=v.item_ref
             JOIN search_view_revision_records r ON r.view_id=v.view_id AND r.revision=v.revision;
            CREATE INDEX ix_search_view_directory_rank ON search_view_directory_version_records(
             view_storage_id,sort_high DESC,sort_upload_fast DESC,sort_mid DESC,sort_inferred DESC,sort_upload_medium DESC,sort_bitrate DESC,
                sort_tie DESC,sequence,directory_id,revision) WHERE is_removed=0;
            CREATE INDEX ix_search_view_directory_version_file ON search_view_directory_version_records(best_file_id);
            CREATE INDEX ix_search_view_directory_version_revision ON search_view_directory_version_records(revision_id,view_storage_id,revision);
            CREATE TABLE search_view_album_records(
             id INTEGER PRIMARY KEY,view_storage_id INTEGER NOT NULL REFERENCES search_views(storage_id) ON DELETE CASCADE,
             item_ref TEXT NOT NULL,stable_username TEXT NOT NULL,stable_folder_path TEXT NOT NULL,query_json TEXT NOT NULL,
             UNIQUE(view_storage_id,item_ref),UNIQUE(view_storage_id,stable_username,stable_folder_path));
            INSERT INTO search_view_album_records
             SELECT a.rowid,v.storage_id,a.item_ref,a.stable_username,a.stable_folder_path,a.query_json
             FROM search_view_aggregate_albums a JOIN search_views v ON v.id=a.view_id;
            CREATE TABLE search_view_album_version_records(
             group_id INTEGER NOT NULL REFERENCES search_view_album_records(id) ON DELETE CASCADE,
             view_storage_id INTEGER NOT NULL,revision INTEGER NOT NULL,revision_id INTEGER NOT NULL,
             group_index INTEGER NOT NULL,share_count INTEGER NOT NULL,selectable_option_count INTEGER NOT NULL,
             representative_directory_id INTEGER NOT NULL REFERENCES search_view_directory_records(id) ON DELETE CASCADE,
             is_removed INTEGER NOT NULL,PRIMARY KEY(group_id,revision),
             FOREIGN KEY(view_storage_id,revision,revision_id) REFERENCES search_view_revision_records(view_storage_id,revision,
                storage_id) ON DELETE CASCADE) WITHOUT ROWID;
            INSERT INTO search_view_album_version_records
             SELECT a.id,r.view_storage_id,v.revision,r.storage_id,v.group_index,v.share_count,v.selectable_option_count,d.id,v.is_removed
             FROM search_view_aggregate_album_versions v JOIN search_view_album_records a ON a.view_storage_id=(SELECT storage_id FROM search_views WHERE id=v.view_id) AND a.item_ref=v.item_ref
             JOIN search_view_directory_records d ON d.view_storage_id=a.view_storage_id AND d.item_ref=v.representative_directory_ref
             JOIN search_view_revision_records r ON r.view_id=v.view_id AND r.revision=v.revision;
            CREATE INDEX ix_search_view_album_page ON search_view_album_version_records(view_storage_id,share_count DESC,group_index,group_id,
                revision) WHERE is_removed=0;
            CREATE INDEX ix_search_view_album_version_directory ON search_view_album_version_records(representative_directory_id);
            CREATE INDEX ix_search_view_album_version_revision ON search_view_album_version_records(revision_id,view_storage_id,revision);
            CREATE TABLE search_view_album_directory_records(
             group_id INTEGER NOT NULL REFERENCES search_view_album_records(id) ON DELETE CASCADE,
             directory_id INTEGER NOT NULL REFERENCES search_view_directory_records(id) ON DELETE CASCADE,
             view_storage_id INTEGER NOT NULL,revision INTEGER NOT NULL,revision_id INTEGER NOT NULL,is_present INTEGER NOT NULL,
             PRIMARY KEY(group_id,directory_id,revision),
             FOREIGN KEY(view_storage_id,revision,revision_id) REFERENCES search_view_revision_records(view_storage_id,revision,
                storage_id) ON DELETE CASCADE) WITHOUT ROWID;
            INSERT INTO search_view_album_directory_records
             SELECT a.id,d.id,r.view_storage_id,v.revision,r.storage_id,v.is_present
             FROM search_view_aggregate_album_directory_versions v JOIN search_views owner ON owner.id=v.view_id
             JOIN search_view_album_records a ON a.view_storage_id=owner.storage_id AND a.item_ref=v.group_ref
             JOIN search_view_directory_records d ON d.view_storage_id=owner.storage_id AND d.item_ref=v.directory_ref
             JOIN search_view_revision_records r ON r.view_id=v.view_id AND r.revision=v.revision;
            CREATE INDEX ix_search_view_album_directory_page ON search_view_album_directory_records(group_id,revision,directory_id);
            CREATE INDEX ix_search_view_album_directory_directory ON search_view_album_directory_records(directory_id);
            CREATE INDEX ix_search_view_album_directory_revision ON search_view_album_directory_records(revision_id,view_storage_id,revision);
            CREATE TABLE search_view_directory_retrievals(
             directory_id INTEGER PRIMARY KEY REFERENCES search_view_directory_records(id) ON DELETE CASCADE,observation_id TEXT NOT NULL);
            INSERT INTO search_view_directory_retrievals
             SELECT d.id,p.observation_id FROM search_view_retrieval_publications p
             JOIN search_view_directories d ON d.view_id=p.view_id AND d.item_ref=p.directory_ref;
            CREATE TABLE search_view_directory_files_rebuilt (
                directory_id INTEGER NOT NULL REFERENCES search_view_directory_records(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                admitted_revision INTEGER NOT NULL,relative_path TEXT NOT NULL,
                PRIMARY KEY(directory_id,file_id)) WITHOUT ROWID;
            INSERT INTO search_view_directory_files_rebuilt SELECT * FROM search_view_directory_files;
            DROP TABLE search_view_directory_files;
            DROP TABLE search_view_retrieval_publications;
            DROP TABLE search_view_aggregate_album_directory_versions;
            DROP TABLE search_view_aggregate_album_versions;
            DROP TABLE search_view_aggregate_albums;
            DROP TABLE search_view_directory_versions;
            DROP TABLE search_view_directories;
            ALTER TABLE search_view_directory_files_rebuilt RENAME TO search_view_directory_files;
            CREATE INDEX ix_search_view_directory_files_file ON search_view_directory_files(file_id,directory_id);
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(directory_id,admitted_revision,file_id,relative_path);
            CREATE INDEX ix_search_view_directory_files_path ON search_view_directory_files(directory_id,relative_path COLLATE BINARY,file_id);
            
            CREATE VIEW search_view_directories AS
             SELECT d.id,v.id AS view_id,d.item_ref,d.username,
                d.folder_path FROM search_view_directory_records d JOIN search_views v ON v.storage_id=d.view_storage_id;
            CREATE VIEW search_view_directory_versions AS
             SELECT owner.id AS view_id,d.item_ref,v.revision,v.best_file_id,v.public_matching_count,v.locked_matching_count,
                v.public_matching_bytes,v.locked_matching_bytes,v.is_fully_retrieved,v.retrieved_file_count,v.retrieved_bytes,v.is_removed,
                v.sort_high,v.sort_upload_fast,v.sort_mid,v.sort_inferred,v.sort_upload_medium,v.sort_bitrate,v.sort_tie,v.sequence
             FROM search_view_directory_version_records v JOIN search_view_directory_records d ON d.id=v.directory_id JOIN search_views owner ON owner.storage_id=v.view_storage_id;
            CREATE VIEW search_view_aggregate_albums AS
             SELECT a.id,owner.id AS view_id,a.item_ref,a.stable_username,a.stable_folder_path,a.query_json
             FROM search_view_album_records a JOIN search_views owner ON owner.storage_id=a.view_storage_id;
            CREATE VIEW search_view_aggregate_album_versions AS
             SELECT owner.id AS view_id,a.item_ref,v.revision,v.group_index,v.share_count,v.selectable_option_count,
                d.item_ref AS representative_directory_ref,v.is_removed
             FROM search_view_album_version_records v JOIN search_view_album_records a ON a.id=v.group_id
             JOIN search_view_directory_records d ON d.id=v.representative_directory_id JOIN search_views owner ON owner.storage_id=v.view_storage_id;
            CREATE VIEW search_view_aggregate_album_directory_versions AS
             SELECT owner.id AS view_id,a.item_ref AS group_ref,d.item_ref AS directory_ref,v.revision,v.is_present
             FROM search_view_album_directory_records v JOIN search_view_album_records a ON a.id=v.group_id
             JOIN search_view_directory_records d ON d.id=v.directory_id JOIN search_views owner ON owner.storage_id=v.view_storage_id;
            CREATE VIEW search_view_retrieval_publications AS
             SELECT owner.id AS view_id,d.item_ref AS directory_ref,p.observation_id
             FROM search_view_directory_retrievals p JOIN search_view_directory_records d ON d.id=p.directory_id JOIN search_views owner ON owner.storage_id=d.view_storage_id;
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE TABLE search_view_directories_restored (
                id INTEGER PRIMARY KEY,
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, username TEXT NOT NULL, folder_path TEXT NOT NULL,
                UNIQUE(view_id, item_ref), UNIQUE(view_id, username, folder_path),
                FOREIGN KEY(view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            INSERT INTO search_view_directories_restored(id,view_id,item_ref,username,folder_path) SELECT id,view_id,item_ref,username,
                folder_path FROM search_view_directories;
            CREATE TABLE search_view_directory_versions_restored (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL, best_file_id INTEGER NOT NULL,
                public_matching_count INTEGER NOT NULL, locked_matching_count INTEGER NOT NULL,
                public_matching_bytes INTEGER NOT NULL, locked_matching_bytes INTEGER NOT NULL,
                is_fully_retrieved INTEGER NOT NULL, retrieved_file_count INTEGER NULL,
                retrieved_bytes INTEGER NULL, is_removed INTEGER NOT NULL DEFAULT 0, sort_high INTEGER NOT NULL DEFAULT 0,
                sort_upload_fast INTEGER NOT NULL DEFAULT 0, sort_mid INTEGER NOT NULL DEFAULT 0, sort_inferred INTEGER NOT NULL DEFAULT 0,
                sort_upload_medium INTEGER NOT NULL DEFAULT 0, sort_bitrate INTEGER NOT NULL DEFAULT 0, sort_tie INTEGER NOT NULL DEFAULT 0,
                sequence INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(view_id, item_ref, revision),
                FOREIGN KEY(view_id, item_ref) REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY(best_file_id) REFERENCES search_view_files(id) ON DELETE CASCADE,
                FOREIGN KEY(view_id, revision) REFERENCES "search_view_revision_records"(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_directory_versions_restored(view_id,item_ref,revision,best_file_id,public_matching_count,
                locked_matching_count,public_matching_bytes,locked_matching_bytes,is_fully_retrieved,retrieved_file_count,retrieved_bytes,
                is_removed,sort_high,sort_upload_fast,sort_mid,sort_inferred,sort_upload_medium,sort_bitrate,sort_tie,sequence) SELECT view_id,
                item_ref,revision,best_file_id,public_matching_count,locked_matching_count,public_matching_bytes,locked_matching_bytes,
                is_fully_retrieved,retrieved_file_count,retrieved_bytes,is_removed,sort_high,sort_upload_fast,sort_mid,sort_inferred,
                sort_upload_medium,sort_bitrate,sort_tie,sequence FROM search_view_directory_versions;
            CREATE TABLE search_view_aggregate_albums_restored (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                stable_username TEXT NOT NULL, stable_folder_path TEXT NOT NULL,
                query_json TEXT NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, stable_username, stable_folder_path),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            INSERT INTO search_view_aggregate_albums_restored(view_id,item_ref,stable_username,stable_folder_path,query_json) SELECT view_id,
                item_ref,stable_username,stable_folder_path,query_json FROM search_view_aggregate_albums;
            CREATE TABLE search_view_aggregate_album_versions_restored (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                group_index INTEGER NOT NULL, share_count INTEGER NOT NULL,
                selectable_option_count INTEGER NOT NULL,
                representative_directory_ref TEXT NOT NULL,
                is_removed INTEGER NOT NULL,
                PRIMARY KEY (view_id, item_ref, revision),
                FOREIGN KEY (view_id, item_ref)
                    REFERENCES search_view_aggregate_albums(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, representative_directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES "search_view_revision_records"(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_aggregate_album_versions_restored(view_id,item_ref,revision,group_index,share_count,selectable_option_count,
                representative_directory_ref,is_removed) SELECT view_id,item_ref,revision,group_index,share_count,selectable_option_count,
                representative_directory_ref,is_removed FROM search_view_aggregate_album_versions;
            CREATE TABLE search_view_aggregate_album_directory_versions_restored (
                view_id TEXT NOT NULL, group_ref TEXT NOT NULL,
                directory_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                is_present INTEGER NOT NULL,
                PRIMARY KEY (view_id, group_ref, directory_ref, revision),
                FOREIGN KEY (view_id, group_ref)
                    REFERENCES search_view_aggregate_albums(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES "search_view_revision_records"(view_id, revision) ON DELETE CASCADE);
            INSERT INTO search_view_aggregate_album_directory_versions_restored(view_id,group_ref,directory_ref,revision,
                is_present) SELECT view_id,group_ref,directory_ref,revision,is_present FROM search_view_aggregate_album_directory_versions;
            CREATE TABLE search_view_retrieval_publications_restored (
                view_id TEXT NOT NULL,
                directory_ref TEXT NOT NULL,
                observation_id TEXT NOT NULL,
                PRIMARY KEY (view_id, directory_ref),
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE);
            INSERT INTO search_view_retrieval_publications_restored(view_id,directory_ref,observation_id) SELECT view_id,directory_ref,
                observation_id FROM search_view_retrieval_publications;
            CREATE TABLE search_view_directory_files_restored (
                directory_id INTEGER NOT NULL REFERENCES search_view_directories(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES search_view_files(id) ON DELETE CASCADE,
                admitted_revision INTEGER NOT NULL,relative_path TEXT NOT NULL,
                PRIMARY KEY(directory_id,file_id)) WITHOUT ROWID;
            INSERT INTO search_view_directory_files_restored(directory_id,file_id,admitted_revision,relative_path) SELECT directory_id,file_id,
                admitted_revision,relative_path FROM search_view_directory_files;
            DROP VIEW search_view_directories;
            DROP VIEW search_view_directory_versions;
            DROP VIEW search_view_aggregate_albums;
            DROP VIEW search_view_aggregate_album_versions;
            DROP VIEW search_view_aggregate_album_directory_versions;
            DROP VIEW search_view_retrieval_publications;
            DROP TABLE search_view_directory_files;
            DROP TABLE search_view_directory_retrievals;
            DROP TABLE search_view_album_directory_records;
            DROP TABLE search_view_album_version_records;
            DROP TABLE search_view_album_records;
            DROP TABLE search_view_directory_version_records;
            DROP TABLE search_view_directory_records;
            ALTER TABLE search_view_directories_restored RENAME TO search_view_directories;
            ALTER TABLE search_view_directory_versions_restored RENAME TO search_view_directory_versions;
            ALTER TABLE search_view_aggregate_albums_restored RENAME TO search_view_aggregate_albums;
            ALTER TABLE search_view_aggregate_album_versions_restored RENAME TO search_view_aggregate_album_versions;
            ALTER TABLE search_view_aggregate_album_directory_versions_restored RENAME TO search_view_aggregate_album_directory_versions;
            ALTER TABLE search_view_retrieval_publications_restored RENAME TO search_view_retrieval_publications;
            ALTER TABLE search_view_directory_files_restored RENAME TO search_view_directory_files;
            CREATE INDEX ix_search_view_aggregate_album_directories_page
                ON search_view_aggregate_album_directory_versions(
                    view_id, group_ref, revision, directory_ref);
            CREATE INDEX ix_search_view_directory_versions_file ON search_view_directory_versions(best_file_id);
            CREATE INDEX ix_search_view_directory_versions_relevance ON search_view_directory_versions(
                view_id, sort_high DESC, sort_upload_fast DESC, sort_mid DESC,
                sort_inferred DESC, sort_upload_medium DESC, sort_bitrate DESC,
                sort_tie DESC, sequence, item_ref, revision) WHERE is_removed = 0;
            CREATE INDEX ix_search_view_aggregate_album_versions_page
                ON search_view_aggregate_album_versions(
                    view_id, share_count DESC, group_index, item_ref, revision)
                WHERE is_removed = 0;
            CREATE INDEX ix_search_view_directory_files_file ON search_view_directory_files(file_id,directory_id);
            CREATE INDEX ix_search_view_directory_files_page ON search_view_directory_files(directory_id,admitted_revision,file_id,relative_path);
            CREATE INDEX ix_search_view_directory_files_path ON search_view_directory_files(directory_id,relative_path COLLATE BINARY,file_id);
            """);
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
}
