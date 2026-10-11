using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantConversationTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "assistant_conversations",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "Conversation");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConversations_OwnerId_LastActiveAt",
                table: "assistant_conversations",
                columns: new[] { "OwnerId", "LastActiveAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantConversations_OwnerId_LastActiveAt",
                table: "assistant_conversations");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "assistant_conversations");
        }
    }
}
