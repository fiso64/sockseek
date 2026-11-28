using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Write;

/// <summary>
/// One writer batch reads manifest revisions together and prepares only needed
/// payloads before BEGIN. Prepared statements share the job transaction; the
/// caller retains the exact mutation being written for per-entry rejection.
/// </summary>
internal sealed class DirectoryManifests(SqliteConnection connection) : IDisposable
{
    private readonly Dictionary<(Guid Job, int Attempt), Stored> stored = [];
    private SqliteWriteStatement? metadata, entries, children, supplemental;
    private sealed record Stored(bool Materialized, long Revision, int SupplementalTargets);

    public static async Task<DirectoryManifests> LoadAsync(SockseekDbContext context,
        IReadOnlyList<JobPersistenceMutation> jobs, CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var batch = new DirectoryManifests(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest.job_id, manifest.attempt_number,
                   manifest.children_materialized, manifest.job_revision,
                   CASE WHEN json_extract(requested.value, '$.Supplemental')
                   THEN (SELECT COUNT(*) FROM directory_manifest_entries entry
                    WHERE entry.job_id = manifest.job_id
                      AND entry.attempt_number = manifest.attempt_number
                      AND entry.ordinal >= manifest.entry_count)
                   ELSE 0 END
            FROM json_each($jobs) requested
            JOIN directory_manifests manifest
              ON manifest.job_id = json_extract(requested.value, '$.Job')
             AND manifest.attempt_number = json_extract(requested.value, '$.Attempt');
            """;
        command.Parameters.AddWithValue("$jobs", JsonSerializer.Serialize(jobs.Select(job => new
        {
            Job = job.JobId.ToString("D").ToUpperInvariant(),
            Attempt = job.DirectoryManifest!.AttemptNumber,
            // Ordinary materialization needs no entry-page reads. Do not fill
            // the write cache counting supplemental work that is not requested.
            Supplemental = job.DirectoryManifest.SupplementalTargets?.Any(target => target != null) == true,
        })));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            batch.stored.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1)),
                new(reader.GetInt32(2) != 0, reader.GetInt64(3), reader.GetInt32(4)));
        return batch;
    }

    // These batch-owned buffers remain UTF-8 through binding. Keeping every
    // large directory's JSON as UTF-16 would double its prepared payload memory.
    public sealed record Prepared(JobPersistenceMutation Mutation,
        string SupplementalIds, byte[]? Entries, byte[]? Children, byte[]? SupplementalEntries);

    public Prepared? Prepare(JobPersistenceMutation job)
    {
        var manifest = job.DirectoryManifest!;
        bool isNew = !stored.TryGetValue((job.JobId, manifest.AttemptNumber), out var prior);
        if (!isNew && prior!.Revision >= job.Revision) return null;
        byte[]? entryJson = null, childJson = null, supplementalJson = null;
        if (isNew)
            entryJson = JsonSerializer.SerializeToUtf8Bytes(manifest.Plan.Entries.Select((planned, index) => new
            {
                planned.Target,
                Relative = planned.RelativeDirectoryComponents,
                ChildId = manifest.ChildrenMaterialized && index < manifest.EntryJobIds.Count
                    ? manifest.EntryJobIds[index].ToString("D").ToUpperInvariant() : null,
            }));
        else if (manifest.ChildrenMaterialized && !prior!.Materialized)
            childJson = JsonSerializer.SerializeToUtf8Bytes(manifest.EntryJobIds.Select(id => id.ToString("D").ToUpperInvariant()));
        if (manifest.SupplementalTargets is { } targets
            && targets.Count(target => target != null) > (prior?.SupplementalTargets ?? 0))
            supplementalJson = JsonSerializer.SerializeToUtf8Bytes(targets.Take(manifest.SupplementalJobIds.Count).Select((target, index) => new
            {
                Target = target,
                ChildId = manifest.SupplementalJobIds[index].ToString("D").ToUpperInvariant(),
            }));
        return new(job, JsonSerializer.Serialize(manifest.SupplementalJobIds),
            entryJson, childJson, supplementalJson);
    }

    public void Write(Prepared prepared, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var job = prepared.Mutation;
        var manifest = job.DirectoryManifest!;
        metadata ??= new(connection, transaction, """
            INSERT INTO directory_manifests (
                job_id, attempt_number, display_root, entry_count, total_known_bytes,
                children_materialized, supplemental_job_ids_json, job_revision)
            VALUES ($job, $attempt, $root, $count, $bytes, $materialized, $supplemental, $revision)
            ON CONFLICT(job_id, attempt_number) DO UPDATE SET
                children_materialized = excluded.children_materialized,
                supplemental_job_ids_json = excluded.supplemental_job_ids_json,
                job_revision = excluded.job_revision;
            """);
        metadata.Bind(0, job.JobId);
        metadata.Bind(1, manifest.AttemptNumber);
        metadata.Bind(2, manifest.Plan.DisplayRoot);
        metadata.Bind(3, manifest.Plan.Entries.Count);
        metadata.Bind(4, manifest.Plan.TotalKnownBytes);
        metadata.Bind(5, manifest.ChildrenMaterialized ? 1 : 0);
        metadata.Bind(6, prepared.SupplementalIds);
        metadata.Bind(7, job.Revision);
        metadata.Execute(cancellationToken);
        if (prepared.Entries is { } entryJson)
        {
            // Decode wide object rows once, while json_extract keeps stored
            // target/relative documents as TEXT. The same statement retains
            // the accepted prefix and exact targets.
            entries ??= new(connection, transaction, """
                INSERT INTO directory_manifest_entries (
                    job_id, attempt_number, ordinal, target_json,
                    relative_directory_components_json, child_job_id)
                SELECT $job, $attempt, CAST(key AS INTEGER),
                    json_extract(value, '$.Target'), json_extract(value, '$.Relative'),
                    json_extract(value, '$.ChildId')
                FROM jsonb_each(jsonb($entries));
                """);
            entries.Bind(0, job.JobId);
            entries.Bind(1, manifest.AttemptNumber);
            entries.BindUtf8Text(2, entryJson);
            entries.Execute(cancellationToken);
        }
        else if (prepared.Children is { } childJson)
        {
            children ??= new(connection, transaction, """
                UPDATE directory_manifest_entries SET child_job_id = child.value
                FROM json_each($children) child
                WHERE directory_manifest_entries.rowid = (
                    SELECT entry.rowid FROM directory_manifest_entries entry
                    WHERE entry.job_id = $job AND entry.attempt_number = $attempt
                      AND entry.ordinal = CAST(child.key AS INTEGER));
                """);
            // $children precedes the key. Each ordinal seeks the composite
            // key; never rescan JSON per row.
            children.BindUtf8Text(0, childJson);
            children.Bind(1, job.JobId);
            children.Bind(2, manifest.AttemptNumber);
            children.Execute(cancellationToken);
        }
        if (prepared.SupplementalEntries is { } supplementalJson)
        {
            supplemental ??= new(connection, transaction, """
                INSERT OR IGNORE INTO directory_manifest_entries (
                    job_id, attempt_number, ordinal, target_json,
                    relative_directory_components_json, child_job_id)
                SELECT $job, $attempt, $offset + CAST(key AS INTEGER),
                       json_extract(value, '$.Target'), '[]', json_extract(value, '$.ChildId')
                FROM jsonb_each(jsonb($entries))
                WHERE json_type(value, '$.Target') = 'object';
                """);
            supplemental.Bind(0, job.JobId);
            supplemental.Bind(1, manifest.AttemptNumber);
            supplemental.Bind(2, manifest.Plan.Entries.Count);
            supplemental.BindUtf8Text(3, supplementalJson);
            supplemental.Execute(cancellationToken);
        }
    }

    public void Dispose()
    {
        metadata?.Dispose();
        entries?.Dispose();
        children?.Dispose();
        supplemental?.Dispose();
    }
}
