using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddScraperSearches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScraperSearches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OwnerSubjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SearchTerm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CountryIndeed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsRemote = table.Column<bool>(type: "bit", nullable: true),
                    HoursOld = table.Column<int>(type: "int", nullable: true),
                    ResultsWanted = table.Column<int>(type: "int", nullable: true),
                    JobType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperSearches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScraperSearchFilters",
                columns: table => new
                {
                    SearchId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperSearchFilters", x => new { x.SearchId, x.Key });
                    table.ForeignKey(
                        name: "FK_ScraperSearchFilters_ScraperSearches_SearchId",
                        column: x => x.SearchId,
                        principalTable: "ScraperSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScraperSearchSites",
                columns: table => new
                {
                    SearchId = table.Column<long>(type: "bigint", nullable: false),
                    Site = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScraperSearchSites", x => new { x.SearchId, x.Site });
                    table.ForeignKey(
                        name: "FK_ScraperSearchSites_ScraperSearches_SearchId",
                        column: x => x.SearchId,
                        principalTable: "ScraperSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScraperSearches_Enabled",
                table: "ScraperSearches",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ScraperSearches_OwnerSubjectId_Name",
                table: "ScraperSearches",
                columns: new[] { "OwnerSubjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScraperSearches_Slug",
                table: "ScraperSearches",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScraperSearchFilters");

            migrationBuilder.DropTable(
                name: "ScraperSearchSites");

            migrationBuilder.DropTable(
                name: "ScraperSearches");
        }
    }
}
