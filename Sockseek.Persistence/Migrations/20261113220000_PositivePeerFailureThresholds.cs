using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261113220000_PositivePeerFailureThresholds")]
public sealed class PositivePeerFailureThresholds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The score-to-failure-count change reversed these comparisons. Convert
        // retained thresholds once, including settings used when rerunning work.
        // Preserve their magnitudes; today's defaults must not replace accepted settings.
        foreach (var (table, column) in new[] { ("jobs", "payload_json"),
            ("submission_specifications", "specification_json") })
        {
            migrationBuilder.Sql($"""
                WITH RECURSIVE
                documents AS (
                    SELECT rowid AS row_id, {column} AS document FROM {table}
                    WHERE {column} IS NOT NULL AND json_valid({column})
                      AND (instr(lower({column}), 'downrankon') > 0 OR instr(lower({column}), 'ignoreon') > 0)
                ),
                settings AS (
                    SELECT d.row_id, j.fullkey, {Normalize("j.value")} AS value,
                           row_number() OVER (PARTITION BY d.row_id ORDER BY j.id) AS ordinal
                    FROM documents d, json_tree(d.document) j
                    WHERE j.type = 'text' AND json_valid(j.value)
                      AND (j.key IN ('effectiveSettingsJson', 'plannedEffectiveSettingsJson')
                           OR j.key = 'json' AND j.fullkey LIKE '$.sharedSettings[%].json')
                ),
                rewritten(row_id, document, ordinal) AS (
                    SELECT row_id, document, 0 FROM documents
                    UNION ALL
                    SELECT r.row_id, json_set(r.document, s.fullkey, s.value), s.ordinal
                    FROM rewritten r JOIN settings s ON s.row_id = r.row_id AND s.ordinal = r.ordinal + 1
                )
                UPDATE {table} SET {column} = (
                    SELECT {Normalize("r.document")} FROM rewritten r
                    WHERE r.row_id = {table}.rowid ORDER BY r.ordinal DESC LIMIT 1
                ) WHERE rowid IN (SELECT row_id FROM documents);
                """);
        }
    }

    // Walk structural JSON fields, not text replacements: titles, paths, and
    // strings that happen to contain threshold-looking text remain untouched.
    private static string Normalize(string document) => $"""
        (WITH RECURSIVE
         thresholds AS (
             SELECT fullkey, -value AS value, row_number() OVER (ORDER BY id) AS ordinal
             FROM json_tree({document})
             WHERE key IN ('downrankOn', 'ignoreOn', 'DownrankOn', 'IgnoreOn')
               AND type = 'integer' AND value < 0
         ),
         normalized(document, ordinal) AS (
             SELECT {document}, 0
             UNION ALL
             SELECT json_set(n.document, t.fullkey, t.value), t.ordinal
             FROM normalized n JOIN thresholds t ON t.ordinal = n.ordinal + 1
         )
         SELECT document FROM normalized ORDER BY ordinal DESC LIMIT 1)
        """;

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data normalization is irreversible; there is no schema change to undo.
    }
}
