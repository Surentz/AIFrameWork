using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrafficBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "traffic_buckets",
                columns: table => new
                {
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Succeeded = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Faulted = table.Column<int>(type: "integer", nullable: false),
                    DurationMsTotal = table.Column<long>(type: "bigint", nullable: false),
                    Bucket0 = table.Column<int>(type: "integer", nullable: false),
                    Bucket1 = table.Column<int>(type: "integer", nullable: false),
                    Bucket2 = table.Column<int>(type: "integer", nullable: false),
                    Bucket3 = table.Column<int>(type: "integer", nullable: false),
                    Bucket4 = table.Column<int>(type: "integer", nullable: false),
                    Bucket5 = table.Column<int>(type: "integer", nullable: false),
                    Bucket6 = table.Column<int>(type: "integer", nullable: false),
                    Bucket7 = table.Column<int>(type: "integer", nullable: false),
                    Bucket8 = table.Column<int>(type: "integer", nullable: false),
                    Bucket9 = table.Column<int>(type: "integer", nullable: false),
                    Bucket10 = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traffic_buckets", x => new { x.BucketStart, x.Kind, x.Name, x.InstanceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrafficBuckets_BucketStart_Desc",
                table: "traffic_buckets",
                column: "BucketStart",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "traffic_buckets");
        }
    }
}
