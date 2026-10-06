using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The export file moves from CSV text to bytes (ADR 0030). Ready exports hold CSV and cannot be
    /// served as the PDF the API now promises, so they are deleted: at most seven days old, and one
    /// click to ask for again. Requested exports are kept; their build now writes bytes.
    /// </summary>
    public partial class StoreOrderExportsAsBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM order_exports WHERE "Status" = 'Ready';""");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "order_exports");

            migrationBuilder.AddColumn<byte[]>(
                name: "Document",
                table: "order_exports",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Document",
                table: "order_exports");

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "order_exports",
                type: "text",
                nullable: true);
        }
    }
}
