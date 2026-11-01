using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261031020000_ExpireObsoleteConditionFacts")]
public sealed class ExpireObsoleteConditionFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            -- Aggregate ranking previously erased configured string preferences;
            -- native sorting also differed in slash-containing query preprocessing.
            -- These caches cannot supply corrected tiers/explanations. Expire them
            -- once, preserving raw observations, search history and accepted work.
            DELETE FROM search_views
            WHERE CASE WHEN json_valid(definition_json)
              THEN json_extract(definition_json, '$.conditionFactsVersion') IS NOT 2
              ELSE 1 END;
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Derived facts can be recreated from retained source observations.
    }
}
