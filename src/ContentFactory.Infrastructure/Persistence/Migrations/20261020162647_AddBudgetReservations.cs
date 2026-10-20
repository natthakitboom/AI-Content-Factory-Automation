using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_reservations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    price_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reserved_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    settled_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    allocations_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    release_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_reservations", x => x.id);
                    table.CheckConstraint("ck_budget_reservations_amounts", "reserved_usd >= 0 AND (settled_usd IS NULL OR settled_usd >= 0)");
                    table.CheckConstraint("ck_budget_reservations_status", "status IN ('reserved','unknown','settled','released')");
                    table.ForeignKey(
                        name: "FK_budget_reservations_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalSchema: "app",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_budget_reservations_price_snapshots_price_snapshot_id",
                        column: x => x.price_snapshot_id,
                        principalSchema: "app",
                        principalTable: "price_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cost_entries_reservation",
                schema: "app",
                table: "cost_entries",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_budget_reservations_approval_status",
                schema: "app",
                table: "budget_reservations",
                columns: new[] { "approval_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_budget_reservations_campaign",
                schema: "app",
                table: "budget_reservations",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "IX_budget_reservations_price_snapshot_id",
                schema: "app",
                table: "budget_reservations",
                column: "price_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ux_budget_reservations_operation",
                schema: "app",
                table: "budget_reservations",
                column: "operation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_reservations",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "ix_cost_entries_reservation",
                schema: "app",
                table: "cost_entries");
        }
    }
}
