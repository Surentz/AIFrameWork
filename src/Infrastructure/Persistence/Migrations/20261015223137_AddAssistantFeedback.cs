using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionTurn = table.Column<int>(type: "integer", nullable: false),
                    AnswerTurn = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Path = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BestScore = table.Column<double>(type: "double precision", nullable: true),
                    Margin = table.Column<double>(type: "double precision", nullable: true),
                    Corrected = table.Column<bool>(type: "boolean", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_outcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assistant_outcomes_assistant_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "assistant_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_ratings",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Turn = table.Column<int>(type: "integer", nullable: false),
                    Rating = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_ratings", x => new { x.ConversationId, x.Turn });
                    table.ForeignKey(
                        name: "FK_assistant_ratings_assistant_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "assistant_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_outcomes_ConversationId",
                table: "assistant_outcomes",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOutcomes_At",
                table: "assistant_outcomes",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_outcomes");

            migrationBuilder.DropTable(
                name: "assistant_ratings");
        }
    }
}
