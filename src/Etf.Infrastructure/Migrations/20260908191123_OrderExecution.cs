using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Etf.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrderExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "order_valid",
                table: "Orders");

            migrationBuilder.AddColumn<decimal>(
                name: "ExecutedAmount",
                table: "Orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExecutionPrice",
                table: "Orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalizedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "Orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderTransitions", x => x.Id);
                    table.CheckConstraint("transition_valid", "(\"FromStatus\" IS NULL AND \"ToStatus\" = 'PendingExecution')\nOR (\"FromStatus\" IS NOT NULL AND \"FromStatus\" = 'PendingExecution' AND \"ToStatus\" IN ('Executed', 'Rejected'))");
                    table.ForeignKey(
                        name: "FK_OrderTransitions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "order_result_valid",
                table: "Orders",
                sql: "(\"Status\" = 'PendingExecution' AND \"ExecutionPrice\" IS NULL AND \"ExecutedAmount\" IS NULL AND \"RejectionReason\" IS NULL AND \"FinalizedAt\" IS NULL)\nOR (\"Status\" = 'Executed' AND \"ExecutionPrice\" IS NOT NULL AND \"ExecutionPrice\" > 0 AND \"ExecutionPrice\" <= \"LimitPrice\"\n    AND \"ExecutedAmount\" IS NOT NULL AND \"ExecutedAmount\" = \"Quantity\" * \"ExecutionPrice\" AND \"RejectionReason\" IS NULL AND \"FinalizedAt\" IS NOT NULL)\nOR (\"Status\" = 'Rejected' AND \"ExecutionPrice\" IS NULL AND \"ExecutedAmount\" IS NULL AND \"RejectionReason\" IS NOT NULL\n    AND length(\"RejectionReason\") > 0 AND \"FinalizedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "order_valid",
                table: "Orders",
                sql: "\"Quantity\" > 0 AND \"LimitPrice\" > 0 AND \"Reservation\" = \"Quantity\" * \"LimitPrice\"");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransitions_OrderId",
                table: "OrderTransitions",
                column: "OrderId",
                unique: true,
                filter: "\"ToStatus\" <> 'PendingExecution'");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTransitions_OrderId_ToStatus",
                table: "OrderTransitions",
                columns: new[] { "OrderId", "ToStatus" },
                unique: true);
            // Stage-1 rows were all PendingExecution. Preserve their original timestamp and data.
            migrationBuilder.Sql("""
                INSERT INTO "OrderTransitions" ("Id", "OrderId", "FromStatus", "ToStatus", "OccurredAt")
                SELECT gen_random_uuid(), "Id", NULL, 'PendingExecution', "CreatedAt" FROM "Orders";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderTransitions");

            migrationBuilder.DropCheckConstraint(
                name: "order_result_valid",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "order_valid",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExecutedAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ExecutionPrice",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FinalizedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "Orders");

            migrationBuilder.AddCheckConstraint(
                name: "order_valid",
                table: "Orders",
                sql: "\"Quantity\" > 0 AND \"LimitPrice\" > 0 AND \"Reservation\" = \"Quantity\" * \"LimitPrice\" AND \"Status\" = 'PendingExecution'");
        }
    }
}
