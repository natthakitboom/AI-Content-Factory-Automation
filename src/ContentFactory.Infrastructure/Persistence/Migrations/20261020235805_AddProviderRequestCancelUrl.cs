using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderRequestCancelUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancel_url",
                schema: "app",
                table: "provider_requests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancel_url",
                schema: "app",
                table: "provider_requests");
        }
    }
}
