using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quartz 4.3's fire-progress, overlap-policy and pause-reason columns across four
    /// <c>quartz.qrtz_*</c> tables. A 4.3 node refuses to start without them
    /// (SchemaProvisioning.Validate). ADR 0017.
    /// </summary>
    /// <remarks>
    /// Taken from Quartz's <c>database/migrations/4.3/</c> at tag v4.3.0 -
    /// <c>add_fire_progress_postgres.sql</c>, <c>add_overlap_policy_postgres.sql</c> and
    /// <c>add_pause_reason_postgres.sql</c>, the three that release marks REQUIRED - with the same
    /// two deliberate differences as <see cref="AddQuartzContinuations"/>: every table is
    /// schema-qualified, and each <c>DO $$ IF NOT EXISTS ... $$</c> block is Postgres's own
    /// <c>ADD COLUMN IF NOT EXISTS</c>, so a database where someone already ran Quartz's scripts by
    /// hand migrates cleanly. Column types are Quartz's own. Every column is nullable with no
    /// default, so existing rows stay valid and a 4.2 worker still running mid-rollout never names
    /// them. The scripts' "must be added together" groups land in one migration, so they do.
    /// <c>add_execution_log_postgres.sql</c> and <c>add_misfire_reason_postgres.sql</c> are not
    /// applied: both alter the optional execution-history tables, which do not exist here because
    /// history is off.
    /// </remarks>
    public partial class AddQuartz43Columns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_fired_triggers ADD COLUMN IF NOT EXISTS progress integer null;
                ALTER TABLE quartz.qrtz_fired_triggers ADD COLUMN IF NOT EXISTS progress_message varchar(250) null;

                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS overlap_policy integer null;

                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS pause_reason varchar(250) null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS paused_by varchar(200) null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS paused_at bigint null;
                ALTER TABLE quartz.qrtz_paused_trigger_grps ADD COLUMN IF NOT EXISTS pause_reason varchar(250) null;
                ALTER TABLE quartz.qrtz_paused_trigger_grps ADD COLUMN IF NOT EXISTS paused_by varchar(200) null;
                ALTER TABLE quartz.qrtz_paused_trigger_grps ADD COLUMN IF NOT EXISTS paused_at bigint null;
                ALTER TABLE quartz.qrtz_paused_job_grps ADD COLUMN IF NOT EXISTS pause_reason varchar(250) null;
                ALTER TABLE quartz.qrtz_paused_job_grps ADD COLUMN IF NOT EXISTS paused_by varchar(200) null;
                ALTER TABLE quartz.qrtz_paused_job_grps ADD COLUMN IF NOT EXISTS paused_at bigint null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_paused_job_grps DROP COLUMN IF EXISTS paused_at;
                ALTER TABLE quartz.qrtz_paused_job_grps DROP COLUMN IF EXISTS paused_by;
                ALTER TABLE quartz.qrtz_paused_job_grps DROP COLUMN IF EXISTS pause_reason;
                ALTER TABLE quartz.qrtz_paused_trigger_grps DROP COLUMN IF EXISTS paused_at;
                ALTER TABLE quartz.qrtz_paused_trigger_grps DROP COLUMN IF EXISTS paused_by;
                ALTER TABLE quartz.qrtz_paused_trigger_grps DROP COLUMN IF EXISTS pause_reason;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS paused_at;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS paused_by;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS pause_reason;

                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS overlap_policy;

                ALTER TABLE quartz.qrtz_fired_triggers DROP COLUMN IF EXISTS progress_message;
                ALTER TABLE quartz.qrtz_fired_triggers DROP COLUMN IF EXISTS progress;
                """);
        }
    }
}
