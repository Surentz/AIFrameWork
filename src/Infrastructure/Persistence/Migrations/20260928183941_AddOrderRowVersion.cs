using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Emits no SQL: xmin is a Postgres system column every row already has, and Npgsql
            // skips it when generating DDL. The operation exists only so the model and the
            // snapshot agree. See OrderConfiguration.ConfigureConcurrency.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "orders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "orders");
        }
    }
}
