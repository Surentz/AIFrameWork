using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignInEventsAndLastSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sign_in_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsernameAttempted = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sign_in_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_LastSeenAt",
                table: "users",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_SignInEvents_At_Desc",
                table: "sign_in_events",
                column: "At",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_SignInEvents_Outcome_At_Desc",
                table: "sign_in_events",
                columns: new[] { "Outcome", "At" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sign_in_events");

            migrationBuilder.DropIndex(
                name: "IX_Users_LastSeenAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "users");
        }
    }
}
