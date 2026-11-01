using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261101110000_CompactSearchAttributes")]
public sealed class CompactSearchAttributes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            UPDATE search_observations SET attributes_json = (
                SELECT json_group_array(json(item)) FROM (
                    SELECT json_array(COALESCE(json_extract(value, '$.Code'), 0),
                        json_extract(value, '$.Name'), COALESCE(json_extract(value, '$.Value'), 0)) AS item
                    FROM json_each(search_observations.attributes_json) ORDER BY CAST(key AS INTEGER)
                )
            ) WHERE attributes_json IS NOT NULL AND json_type(attributes_json) = 'array';
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            UPDATE search_observations SET attributes_json = (
                SELECT json_group_array(json(item)) FROM (
                    SELECT json_object('Code', json_extract(value, '$[0]'),
                        'Name', json_extract(value, '$[1]'), 'Value', json_extract(value, '$[2]')) AS item
                    FROM json_each(search_observations.attributes_json) ORDER BY CAST(key AS INTEGER)
                )
            ) WHERE attributes_json IS NOT NULL AND json_type(attributes_json) = 'array';
            """);
}
