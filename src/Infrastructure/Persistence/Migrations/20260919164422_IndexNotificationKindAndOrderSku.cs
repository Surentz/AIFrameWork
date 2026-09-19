using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexNotificationKindAndOrderSku : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_SourceMessageId_UserId",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Sku_UserId_PlacedAt",
                table: "orders",
                columns: new[] { "Sku", "UserId", "PlacedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SourceMessageId_UserId_Kind",
                table: "notifications",
                columns: new[] { "SourceMessageId", "UserId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Sku_UserId_PlacedAt",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_SourceMessageId_UserId_Kind",
                table: "notifications");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SourceMessageId_UserId",
                table: "notifications",
                columns: new[] { "SourceMessageId", "UserId" },
                unique: true);
        }
    }
}
