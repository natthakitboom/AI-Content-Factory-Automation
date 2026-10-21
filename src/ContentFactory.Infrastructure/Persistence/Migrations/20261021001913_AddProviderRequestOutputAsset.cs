using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderRequestOutputAsset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "output_asset_id",
                schema: "app",
                table: "provider_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_requests_output_asset_id",
                schema: "app",
                table: "provider_requests",
                column: "output_asset_id");

            migrationBuilder.AddForeignKey(
                name: "FK_provider_requests_assets_output_asset_id",
                schema: "app",
                table: "provider_requests",
                column: "output_asset_id",
                principalSchema: "app",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_provider_requests_assets_output_asset_id",
                schema: "app",
                table: "provider_requests");

            migrationBuilder.DropIndex(
                name: "IX_provider_requests_output_asset_id",
                schema: "app",
                table: "provider_requests");

            migrationBuilder.DropColumn(
                name: "output_asset_id",
                schema: "app",
                table: "provider_requests");
        }
    }
}
