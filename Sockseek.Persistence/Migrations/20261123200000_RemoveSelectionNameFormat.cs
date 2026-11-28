using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20261123200000_RemoveSelectionNameFormat")]
public sealed class RemoveSelectionNameFormat : Migration
{
    // Interactive selections used to store "{foldername}/{filename}" plus a
    // hidden conflict flag. The empty format now means the built-in layout in
    // every mode and selections derive their conflict policy from the job.
    // Retained settings are escaped JSON strings inside the specification.
    protected override void Up(MigrationBuilder migration)
        => migration.Sql("""
            UPDATE submission_specifications
            SET specification_json = replace(replace(specification_json,
                    ',\u0022preserveSelectionConflicts\u0022:true', ''),
                    '\u0022nameFormat\u0022:\u0022{foldername}/{filename}\u0022', '\u0022nameFormat\u0022:\u0022\u0022')
            WHERE specification_json LIKE '%preserveSelectionConflicts%';
            """);

    protected override void Down(MigrationBuilder migration) { }
}
