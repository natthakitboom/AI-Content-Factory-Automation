using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoVersionOutputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "subtitles_asset_id",
                schema: "app",
                table: "video_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "thumbnail_asset_id",
                schema: "app",
                table: "video_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_video_versions_subtitles_asset_id",
                schema: "app",
                table: "video_versions",
                column: "subtitles_asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_video_versions_thumbnail_asset_id",
                schema: "app",
                table: "video_versions",
                column: "thumbnail_asset_id");

            migrationBuilder.AddForeignKey(
                name: "FK_video_versions_assets_subtitles_asset_id",
                schema: "app",
                table: "video_versions",
                column: "subtitles_asset_id",
                principalSchema: "app",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_video_versions_assets_thumbnail_asset_id",
                schema: "app",
                table: "video_versions",
                column: "thumbnail_asset_id",
                principalSchema: "app",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_video_versions_assets_subtitles_asset_id",
                schema: "app",
                table: "video_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_video_versions_assets_thumbnail_asset_id",
                schema: "app",
                table: "video_versions");

            migrationBuilder.DropIndex(
                name: "IX_video_versions_subtitles_asset_id",
                schema: "app",
                table: "video_versions");

            migrationBuilder.DropIndex(
                name: "IX_video_versions_thumbnail_asset_id",
                schema: "app",
                table: "video_versions");

            migrationBuilder.DropColumn(
                name: "subtitles_asset_id",
                schema: "app",
                table: "video_versions");

            migrationBuilder.DropColumn(
                name: "thumbnail_asset_id",
                schema: "app",
                table: "video_versions");
        }
    }
}
