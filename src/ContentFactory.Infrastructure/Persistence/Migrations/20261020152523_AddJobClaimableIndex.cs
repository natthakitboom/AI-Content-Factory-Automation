using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobClaimableIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_eligible",
                schema: "app",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_claimable",
                schema: "app",
                table: "jobs",
                columns: new[] { "lane", "priority", "run_after", "created_at" },
                filter: "status IN ('queued', 'retry_wait')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_claimable",
                schema: "app",
                table: "jobs");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_eligible",
                schema: "app",
                table: "jobs",
                columns: new[] { "lane", "priority", "run_after", "created_at" },
                filter: "status = 'queued'");
        }
    }
}
