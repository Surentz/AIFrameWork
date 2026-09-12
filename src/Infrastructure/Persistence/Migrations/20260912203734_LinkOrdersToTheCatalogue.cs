using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkOrdersToTheCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "orders",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Orders predate the catalogue, so most rows match nothing and keep their nulls - the
            // UI renders those as the bare sku, which is honest: we do not know what they cost,
            // and copying today's price would assert something false. A row that DOES match has
            // its sku rewritten to the catalogue's normalized form too, so that sku and snapshot
            // agree for every row carrying one.
            //
            // Additive and null-tolerant on purpose: per ADR 0010 the previous generation's pods
            // keep inserting orders that set none of these columns for the length of the rollout.
            migrationBuilder.Sql(
                """
                UPDATE orders o
                SET "ProductId"   = p."Id",
                    "ProductName" = p."Name",
                    "UnitPrice"   = p."Price",
                    "Sku"         = p."Sku"
                FROM products p
                WHERE upper(trim(o."Sku")) = p."Sku";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ProductName",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "orders");
        }
    }
}
