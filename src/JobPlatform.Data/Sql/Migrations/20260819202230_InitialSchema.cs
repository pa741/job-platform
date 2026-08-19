using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScrapeRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BlobPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    BlobETag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BlobSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SearchTerm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ScrapedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IngestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScrapeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    ParsedCount = table.Column<int>(type: "int", nullable: false),
                    InvalidCount = table.Column<int>(type: "int", nullable: false),
                    NewCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedCount = table.Column<int>(type: "int", nullable: false),
                    UnchangedCount = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapeRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobPostings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Site = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LocationRaw = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    LocationRegion = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    LocationCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsRemote = table.Column<bool>(type: "bit", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    DatePosted = table.Column<DateOnly>(type: "date", nullable: true),
                    MinAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    SalaryInterval = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SalarySource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    JobLevel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobFunction = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CompanyIndustry = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    JobUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    JobUrlDirect = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompanyUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionLength = table.Column<int>(type: "int", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FirstSeenRunId = table.Column<int>(type: "int", nullable: false),
                    LastSeenRunId = table.Column<int>(type: "int", nullable: false),
                    SeenCount = table.Column<int>(type: "int", nullable: false),
                    SearchTerm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobPostings_ScrapeRuns_FirstSeenRunId",
                        column: x => x.FirstSeenRunId,
                        principalTable: "ScrapeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobPostings_ScrapeRuns_LastSeenRunId",
                        column: x => x.LastSeenRunId,
                        principalTable: "ScrapeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Company",
                table: "JobPostings",
                column: "Company");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_ContentHash",
                table: "JobPostings",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_FirstSeenRunId",
                table: "JobPostings",
                column: "FirstSeenRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_LastSeenRunId",
                table: "JobPostings",
                column: "LastSeenRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SearchTerm_FirstSeenUtc",
                table: "JobPostings",
                columns: new[] { "SearchTerm", "FirstSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SearchTerm_LastSeenUtc",
                table: "JobPostings",
                columns: new[] { "SearchTerm", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SourceKey",
                table: "JobPostings",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeRuns_BlobPath",
                table: "ScrapeRuns",
                column: "BlobPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeRuns_SearchTerm_ScrapeDate",
                table: "ScrapeRuns",
                columns: new[] { "SearchTerm", "ScrapeDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobPostings");

            migrationBuilder.DropTable(
                name: "ScrapeRuns");
        }
    }
}
