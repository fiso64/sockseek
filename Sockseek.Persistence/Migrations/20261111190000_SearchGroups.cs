using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261111190000_SearchGroups")]
public sealed class SearchGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rename(migrationBuilder, reverse: false);
    protected override void Down(MigrationBuilder migrationBuilder) => Rename(migrationBuilder, reverse: true);

    private static void Rename(MigrationBuilder migrationBuilder, bool reverse)
    {
        (string Old, string New)[] members = [
            ("isAggregate", "groupResults"),
            ("aggregatePresentation", "groupResults"),
            ("searchAggregatePresentation", "searchGroupResults"),
            ("minSharesAggregate", "minGroupShares"),
            ("defaultMinSharesAggregate", "defaultMinGroupShares"),
            ("aggregateLengthTol", "groupLengthTol"),
            ("aggregateLengthTolerance", "groupLengthTolerance"),
            ("aggregate", "songGroups"),
            ("albumAggregate", "albumGroups"),
        ];
        // Retained execution payloads use PascalCase; specifications/settings use camelCase.
        var replacements = members.SelectMany(pair => new[] { pair,
            (Old: char.ToUpperInvariant(pair.Old[0]) + pair.Old[1..],
             New: char.ToUpperInvariant(pair.New[0]) + pair.New[1..]) })
            .Select(pair => ($"\"{pair.Old}\":", $"\"{pair.New}\":"))
            .ToList();
        foreach (string kindKey in new[] { "kind", "Kind" })
            foreach (var (oldKind, newKind) in new[] { ("Aggregate", "SongGroups"),
                ("AlbumAggregate", "AlbumGroups"), ("aggregate", "song-groups"), ("album-aggregate", "album-groups") })
                replacements.Add(($"\"{kindKey}\":\"{oldKind}\"", $"\"{kindKey}\":\"{newKind}\""));
        if (reverse) replacements = replacements.Select(pair => (pair.Item2, pair.Item1)).ToList();

        // Replace structural tokens only, never bare words in titles, queries or paths.
        // json() canonicalizes spacing; quoted tokens cannot match escaped user text.
        string Rewrite(string expression, bool settings = false)
        {
            foreach (var (oldToken, newToken) in replacements)
            {
                // GroupResults occurs in two distinct contracts: settings enable
                // automatic grouping, while definitions choose a results presentation.
                if (reverse && (settings ? newToken.Contains("aggregatePresentation", StringComparison.OrdinalIgnoreCase)
                        : newToken.Contains("isAggregate", StringComparison.OrdinalIgnoreCase))) continue;
                expression = $"replace({expression}, '{oldToken}', '{newToken}')";
            }
            return expression;
        }

        migrationBuilder.Sql(reverse
            ? "UPDATE jobs SET kind = CASE kind WHEN 'SongGroups' THEN 'Aggregate' ELSE 'AlbumAggregate' END WHERE kind IN ('SongGroups', 'AlbumGroups');"
            : "UPDATE jobs SET kind = CASE kind WHEN 'Aggregate' THEN 'SongGroups' ELSE 'AlbumGroups' END WHERE kind IN ('Aggregate', 'AlbumAggregate');");
        foreach (var (table, column) in new[] { ("jobs", "payload_json"),
            ("submission_specifications", "specification_json"), ("submissions", "commit_receipt_json") })
        {
            // Settings can also be JSON strings inside a specification: root settings,
            // inline child settings and interned settings. Decode those strings before
            // renaming their members, then put them back without changing their values.
            migrationBuilder.Sql($"""
                WITH RECURSIVE
                documents AS (
                    SELECT rowid AS row_id, json({column}) AS document FROM {table}
                    WHERE {column} IS NOT NULL AND json_valid({column})
                      AND instr(lower({column}), '{(reverse ? "group" : "aggregate")}') > 0
                ),
                settings AS (
                    SELECT d.row_id, j.fullkey, {Rewrite("json(j.value)", settings: true)} AS value,
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
                    SELECT {Rewrite("r.document")} FROM rewritten r
                    WHERE r.row_id = {table}.rowid ORDER BY r.ordinal DESC LIMIT 1
                ) WHERE rowid IN (SELECT row_id FROM documents);
                """);
        }
    }
}
