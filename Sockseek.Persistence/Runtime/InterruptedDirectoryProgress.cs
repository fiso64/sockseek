namespace Sockseek.Persistence.Runtime;

internal static class InterruptedDirectoryProgress
{
    // A backup can contain newer child outcomes than the last directory snapshot.
    // Rebuild only complete materialized manifests. Pruning can remove child rows,
    // in which case the last saved aggregate is the most complete record available.
    internal static string Update(bool currentRuntimeOnly) => $$"""
        WITH folders AS MATERIALIZED (
            SELECT j.id, m.attempt_number, m.entry_count, m.supplemental_job_ids_json,
                   m.entry_count + json_array_length(m.supplemental_job_ids_json) AS file_count,
                   m.total_known_bytes AS planned_bytes
            FROM jobs j
            JOIN directory_manifests m ON m.job_id = j.id
                AND m.attempt_number = CASE WHEN json_valid(j.payload_json)
                    THEN json_extract(j.payload_json, '$.Directory.AttemptNumber') END
            WHERE j.lifecycle_state = 'Terminal' AND j.failure_reason = 'Interrupted'
              AND j.kind IN ('Album', 'RemoteDirectory') AND m.children_materialized = 1
              {{(currentRuntimeOnly ? "AND j.last_runtime_id = {0}" : "")}}
        ), members AS (
            SELECT f.id, e.child_job_id, 0 AS supplemental, json_extract(e.target_json, '$.Size') IS NULL AS unknown
            FROM folders f JOIN directory_manifest_entries e
              ON e.job_id = f.id AND e.attempt_number = f.attempt_number AND e.ordinal < f.entry_count
            UNION ALL
            SELECT f.id, upper(s.value), 1, 0 FROM folders f, json_each(f.supplemental_job_ids_json) s
        ), files AS (
            SELECT m.id, m.supplemental, m.unknown, j.terminal_outcome AS outcome,
                   j.terminal_outcome = 'Succeeded' OR
                       (j.terminal_outcome = 'Skipped' AND j.skip_reason = 'AlreadyExists') AS satisfied,
                   coalesce(json_extract(j.payload_json, '$.File.Reused'), 0) AS reused,
                   json_extract(j.payload_json, '$.File.BytesTransferred') AS bytes,
                   json_extract(j.payload_json, '$.File.FileSize') AS size
            FROM members m CROSS JOIN jobs j ON j.id = m.child_job_id
            WHERE j.lifecycle_state = 'Terminal' AND j.kind IN ('RemoteFile', 'Song')
              AND CASE WHEN json_valid(j.payload_json) THEN json_type(j.payload_json, '$.File') END = 'object'
        ), totals AS (
            SELECT id, count(*) AS finished, sum(satisfied) AS successful,
                   sum(outcome NOT IN ('Skipped', 'Succeeded')) AS failed,
                   sum(outcome = 'Skipped') AS skipped,
                   sum(outcome = 'Succeeded' AND reused = 0) AS downloaded,
                   sum(outcome = 'Succeeded' AND reused = 1) AS reused,
                   sum(bytes) AS transferred, sum(iif(satisfied, coalesce(size, bytes), bytes)) AS credited,
                   sum(iif(supplemental, coalesce(size, 0), 0)) AS extra_bytes,
                   sum(unknown OR (supplemental AND size IS NULL)) AS unknown_sizes
            FROM files GROUP BY id
        )
        UPDATE jobs AS j SET payload_json = json_set(j.payload_json,
            '$.Directory.FileCount', t.finished, '$.Directory.TerminalFileCount', t.finished,
            '$.Directory.SuccessfulFileCount', t.successful, '$.Directory.FailedFileCount', t.failed,
            '$.Directory.SkippedFileCount', t.skipped, '$.Directory.DownloadedFileCount', t.downloaded,
            '$.Directory.ReusedFileCount', t.reused, '$.Directory.BytesTransferred', t.transferred,
            '$.Directory.TotalKnownBytes', f.planned_bytes + t.extra_bytes, '$.Directory.UnknownFileSizeCount', t.unknown_sizes,
            '$.Directory.Progress', CASE WHEN t.finished = t.successful THEN 1.0
                ELSE coalesce(min(1.0, 1.0 * t.credited / nullif(f.planned_bytes + t.extra_bytes, 0)), 0.0) END)
        FROM folders f JOIN totals t ON t.id = f.id AND t.finished = f.file_count
        WHERE j.id = f.id;
        """;
}
