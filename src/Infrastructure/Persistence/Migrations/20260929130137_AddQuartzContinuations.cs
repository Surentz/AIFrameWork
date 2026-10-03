using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quartz 4.2's continuation columns on <c>quartz.qrtz_triggers</c>. A 4.2 node refuses to
    /// start without them (SchemaProvisioning.Validate). ADR 0017.
    /// </summary>
    /// <remarks>
    /// Taken from Quartz's <c>database/migrations/4.2/add_continuations_postgres.sql</c> at tag
    /// v4.2.1, with two deliberate differences. The table is schema-qualified: the upstream script
    /// names an unqualified <c>qrtz_triggers</c>, and its existence checks read
    /// information_schema by table name alone, which would match any schema. And each
    /// <c>DO $$ IF NOT EXISTS ... $$</c> block is written as Postgres's own
    /// <c>ADD COLUMN IF NOT EXISTS</c>, which is the same guard: a database where someone already
    /// ran Quartz's script by hand, as Quartz's startup error tells them to, migrates cleanly.
    /// All three columns are nullable with no default, so every existing row stays valid and a
    /// 4.1 worker still running mid-rollout never reads them. The optional execution-history
    /// tables (<c>add_execution_history_postgres.sql</c>) are not added: history is off here, and
    /// Quartz validates those tables only when it is on.
    /// </remarks>
    public partial class AddQuartzContinuations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_name text null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_group text null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continuation_condition integer null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continuation_condition;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continues_trigger_group;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continues_trigger_name;
                """);
        }
    }
}
