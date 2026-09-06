using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column is NOT NULL and there is no owner to infer for rows that predate it.
            // These are throwaway development rows - see the spec - and nothing is deployed.
            // Down cannot restore them; that is accepted.
            migrationBuilder.Sql("DELETE FROM orders;");

            migrationBuilder.DropIndex(
                name: "IX_Orders_PlacedAt_Id_Desc",
                table: "orders");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "orders",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_UserId_PlacedAt_Id_Desc",
                table: "orders",
                columns: new[] { "UserId", "PlacedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_UserId_PlacedAt_Id_Desc",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PlacedAt_Id_Desc",
                table: "orders",
                columns: new[] { "PlacedAt", "Id" },
                descending: new bool[0]);
        }
    }
}
