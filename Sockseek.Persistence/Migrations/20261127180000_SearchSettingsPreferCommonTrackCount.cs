using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261127180000_SearchSettingsPreferCommonTrackCount")]
public sealed class SearchSettingsPreferCommonTrackCount : Migration
{
    // The settings captured with each search gained PreferCommonTrackCount. Searches
    // stored before it were ranked without that preference, so they record it as
    // off. Job payloads spell members in Pascal case and specifications in camel
    // case; a specification holds a definition for the search and for each command
    // at any depth. Every settings object lacking the member receives it.
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql(AddMember("jobs", "payload_json", "ProjectionSettings", "PreferCommonTrackCount"));
        migration.Sql(AddMember("submission_specifications", "specification_json", "projectionSettings", "preferCommonTrackCount"));
    }

    protected override void Down(MigrationBuilder migration) { }

    internal static string AddMember(string table, string column, string settings, string member) => $"""
        UPDATE {table} AS stored
        SET {column} = (
            WITH missing(position, path) AS (
                SELECT row_number() OVER (ORDER BY node.id), node.fullkey
                FROM json_tree(stored.{column}) AS node
                WHERE node.key = '{settings}' AND node.type = 'object'
                  AND json_type(stored.{column}, node.fullkey || '.{member}') IS NULL),
            added(position, document) AS (
                SELECT 0, stored.{column}
                UNION ALL
                SELECT added.position + 1, json_set(added.document, missing.path || '.{member}', json('false'))
                FROM added JOIN missing ON missing.position = added.position + 1)
            SELECT document FROM added ORDER BY position DESC LIMIT 1)
        WHERE instr(stored.{column}, '"{settings}"') > 0 AND json_valid(stored.{column});
        """;
}
