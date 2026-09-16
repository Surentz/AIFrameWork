using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quartz 4.1.0's own Postgres schema, in a `quartz` schema, prefix `qrtz_`. ADR 0017.
    /// </summary>
    /// <remarks>
    /// The SQL is COPIED from Quartz.Impl.AdoJobStore.Schema.create_postgres.sql in the 4.1.0
    /// package, with {0} = quartz.qrtz_ and {1} = qrtz_. It is copied rather than read from the
    /// package at run time on purpose: an applied migration must never change, and reading the
    /// resource would make a Quartz upgrade silently change what this migration does on a fresh
    /// database. A Quartz upgrade that changes the schema is a NEW migration, taken from Quartz's
    /// upgrade script for that version; the worker's SchemaProvisioning.Validate is what catches
    /// forgetting to write one.
    /// </remarks>
    public partial class AddQuartzSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS quartz;");
            migrationBuilder.Sql(QuartzSchema);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS quartz CASCADE;");
        }

        // Copied verbatim from Quartz 4.1.0's Quartz.Impl.AdoJobStore.Schema.create_postgres.sql,
        // with {0} = quartz.qrtz_ and {1} = qrtz_ substituted (see the class remarks above). The
        // `--;;` statement separators are left in as SQL comments; the whole script runs as one
        // command, and every statement is its own CREATE ... IF NOT EXISTS, so no comment strip
        // or split is needed.
        private const string QuartzSchema = """
            --
            -- Quartz.NET schema -- PostgreSQL
            --
            -- GENERATED FILE. Describe the schema in build/Build.DatabaseSchema.cs and run
            -- 'dotnet fallout GenerateSchema'; edits made here are overwritten.
            --
            -- This is what AdoJobStore runs for itself when SchemaProvisioning.CreateIfMissing is
            -- configured. It is not the script to run by hand -- use
            -- database/tables/tables_postgres.sql for that, which is written for a person with a
            -- database client and drops an existing schema before it recreates one.
            --
            -- Every statement creates only what is missing, and nothing here ever drops anything.
            -- So it is safe to run against a schema that already exists, and safe to run twice.
            --
            -- 'quartz.qrtz_' is the configured table prefix and 'qrtz_' is the same prefix with any schema
            -- qualifier removed, for the identifiers that cannot carry one -- index, constraint and
            -- catalog-lookup names. They are substituted at runtime, so a schema provisioned under a
            -- prefix of its own collides with nothing.
            --
            -- Statements are separated by a line reading exactly '--;;'. The job store splits on
            -- it and sends each piece to the provider as one command, which is why no dialect's batch
            -- separator appears: no GO, no lone '/', no SET TERM.
            --
            --;;
            -- quartz.qrtz_JOB_DETAILS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_job_details (
              sched_name text not null,
              job_name text not null,
              job_group text not null,
              description text null,
              job_class_name text not null,
              is_durable bool not null,
              is_nonconcurrent bool not null,
              is_update_data bool not null,
              requests_recovery bool not null,
              job_data bytea null,
              primary key (sched_name,job_name,job_group)
            );
            --;;
            -- quartz.qrtz_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_triggers (
              sched_name text not null,
              trigger_name text not null,
              trigger_group text not null,
              job_name text not null,
              job_group text not null,
              description text null,
              next_fire_time bigint null,
              prev_fire_time bigint null,
              priority integer null,
              trigger_state text not null,
              trigger_type text not null,
              start_time bigint not null,
              end_time bigint null,
              calendar_name text null,
              misfire_instr smallint null,
              misfire_orig_fire_time bigint null,
              execution_group varchar(200) null,
              preferred_node varchar(200) null,
              preferred_node_auto bool not null default false,
              retry_policy varchar(250) null,
              retry_attempt integer null,
              job_data bytea null,
              primary key (sched_name,trigger_name,trigger_group),
              foreign key (sched_name,job_name,job_group) references quartz.qrtz_job_details (sched_name,job_name,job_group)
            );
            --;;
            -- quartz.qrtz_SIMPLE_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_simple_triggers (
              sched_name text not null,
              trigger_name text not null,
              trigger_group text not null,
              repeat_count bigint not null,
              repeat_interval bigint not null,
              times_triggered bigint not null,
              primary key (sched_name,trigger_name,trigger_group),
              foreign key (sched_name,trigger_name,trigger_group) references quartz.qrtz_triggers (sched_name,trigger_name,trigger_group) on delete cascade
            );
            --;;
            -- quartz.qrtz_CRON_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_cron_triggers (
              sched_name text not null,
              trigger_name text not null,
              trigger_group text not null,
              cron_expression text not null,
              time_zone_id text,
              primary key (sched_name,trigger_name,trigger_group),
              foreign key (sched_name,trigger_name,trigger_group) references quartz.qrtz_triggers (sched_name,trigger_name,trigger_group) on delete cascade
            );
            --;;
            -- quartz.qrtz_SIMPROP_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_simprop_triggers (
              sched_name text not null,
              trigger_name text not null,
              trigger_group text not null,
              str_prop_1 text null,
              str_prop_2 text null,
              str_prop_3 text null,
              int_prop_1 integer null,
              int_prop_2 integer null,
              long_prop_1 bigint null,
              long_prop_2 bigint null,
              dec_prop_1 numeric null,
              dec_prop_2 numeric null,
              bool_prop_1 bool null,
              bool_prop_2 bool null,
              time_zone_id text null,
              primary key (sched_name,trigger_name,trigger_group),
              foreign key (sched_name,trigger_name,trigger_group) references quartz.qrtz_triggers (sched_name,trigger_name,trigger_group) on delete cascade
            );
            --;;
            -- quartz.qrtz_BLOB_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_blob_triggers (
              sched_name text not null,
              trigger_name text not null,
              trigger_group text not null,
              blob_data bytea null,
              primary key (sched_name,trigger_name,trigger_group),
              foreign key (sched_name,trigger_name,trigger_group) references quartz.qrtz_triggers (sched_name,trigger_name,trigger_group) on delete cascade
            );
            --;;
            -- quartz.qrtz_CALENDARS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_calendars (
              sched_name text not null,
              calendar_name text not null,
              calendar bytea not null,
              primary key (sched_name,calendar_name)
            );
            --;;
            -- quartz.qrtz_PAUSED_TRIGGER_GRPS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_paused_trigger_grps (
              sched_name text not null,
              trigger_group text not null,
              primary key (sched_name,trigger_group)
            );
            --;;
            -- quartz.qrtz_PAUSED_JOB_GRPS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_paused_job_grps (
              sched_name text not null,
              job_group text not null,
              primary key (sched_name,job_group)
            );
            --;;
            -- quartz.qrtz_FIRED_TRIGGERS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_fired_triggers (
              sched_name text not null,
              entry_id text not null,
              trigger_name text not null,
              trigger_group text not null,
              instance_name text not null,
              fired_time bigint not null,
              sched_time bigint not null,
              priority integer not null,
              state text not null,
              job_name text null,
              job_group text null,
              is_nonconcurrent bool not null,
              requests_recovery bool null,
              execution_group varchar(200) null,
              primary key (sched_name,entry_id)
            );
            --;;
            -- quartz.qrtz_SCHEDULER_STATE
            CREATE TABLE IF NOT EXISTS quartz.qrtz_scheduler_state (
              sched_name text not null,
              instance_name text not null,
              last_checkin_time bigint not null,
              checkin_interval bigint not null,
              primary key (sched_name,instance_name)
            );
            --;;
            -- quartz.qrtz_LOCKS
            CREATE TABLE IF NOT EXISTS quartz.qrtz_locks (
              sched_name text not null,
              lock_name text not null,
              primary key (sched_name,lock_name)
            );
            --;;
            -- IDX_qrtz_J_G_N
            CREATE INDEX IF NOT EXISTS idx_qrtz_j_g_n ON quartz.qrtz_job_details (sched_name, job_group, job_name);
            --;;
            -- IDX_qrtz_T_J
            CREATE INDEX IF NOT EXISTS idx_qrtz_t_j ON quartz.qrtz_triggers (sched_name, job_name, job_group);
            --;;
            -- IDX_qrtz_T_G_N
            CREATE INDEX IF NOT EXISTS idx_qrtz_t_g_n ON quartz.qrtz_triggers (sched_name, trigger_group, trigger_name);
            --;;
            -- IDX_qrtz_T_C
            CREATE INDEX IF NOT EXISTS idx_qrtz_t_c ON quartz.qrtz_triggers (sched_name, calendar_name);
            --;;
            -- IDX_qrtz_T_NFT_ST
            CREATE INDEX IF NOT EXISTS idx_qrtz_t_nft_st ON quartz.qrtz_triggers (sched_name, trigger_state, next_fire_time asc, priority desc, misfire_instr);
            --;;
            -- IDX_qrtz_FT_INST_JOB_REQ_RCVRY
            CREATE INDEX IF NOT EXISTS idx_qrtz_ft_inst_job_req_rcvry ON quartz.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);
            --;;
            -- IDX_qrtz_FT_J_G
            CREATE INDEX IF NOT EXISTS idx_qrtz_ft_j_g ON quartz.qrtz_fired_triggers (sched_name, job_name, job_group);
            --;;
            -- IDX_qrtz_FT_T_G
            CREATE INDEX IF NOT EXISTS idx_qrtz_ft_t_g ON quartz.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
            """;
    }
}
