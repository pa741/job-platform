using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddFreehireSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanyNumEmployees",
                table: "JobPostings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperienceRange",
                table: "JobPostings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FakeFreshness",
                table: "JobPostings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreshnessClass",
                table: "JobPostings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PostingAgeDays",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepostCount",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "JobPostings",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SearchTerm_FreshnessClass",
                table: "JobPostings",
                columns: new[] { "SearchTerm", "FreshnessClass" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobPostings_SearchTerm_FreshnessClass",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "CompanyNumEmployees",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ExperienceRange",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "FakeFreshness",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "FreshnessClass",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "PostingAgeDays",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "RepostCount",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "JobPostings");
        }
    }
}
