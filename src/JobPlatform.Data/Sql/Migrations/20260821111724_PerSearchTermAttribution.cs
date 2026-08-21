using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class PerSearchTermAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobPostingSearchTerms",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    SearchTerm = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstSeenRunId = table.Column<int>(type: "int", nullable: false),
                    LastSeenRunId = table.Column<int>(type: "int", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SeenCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostingSearchTerms", x => new { x.PostingId, x.SearchTerm });
                    table.ForeignKey(
                        name: "FK_JobPostingSearchTerms_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobPostingSearchTerms_ScrapeRuns_FirstSeenRunId",
                        column: x => x.FirstSeenRunId,
                        principalTable: "ScrapeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobPostingSearchTerms_ScrapeRuns_LastSeenRunId",
                        column: x => x.LastSeenRunId,
                        principalTable: "ScrapeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSearchTerms_FirstSeenRunId",
                table: "JobPostingSearchTerms",
                column: "FirstSeenRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSearchTerms_LastSeenRunId",
                table: "JobPostingSearchTerms",
                column: "LastSeenRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSearchTerms_SearchTerm_FirstSeenUtc",
                table: "JobPostingSearchTerms",
                columns: new[] { "SearchTerm", "FirstSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingSearchTerms_SearchTerm_LastSeenUtc",
                table: "JobPostingSearchTerms",
                columns: new[] { "SearchTerm", "LastSeenUtc" });
            // Between creating the table and dropping the column, and it has to stay
            // there: EF scaffolds the drops first, which would throw every existing
            // posting's attribution away before there was anywhere to put it.
            migrationBuilder.Sql("""
                INSERT INTO JobPostingSearchTerms
                    (PostingId, SearchTerm, FirstSeenRunId, LastSeenRunId,
                     FirstSeenUtc, LastSeenUtc, SeenCount)
                SELECT Id, SearchTerm, FirstSeenRunId, LastSeenRunId,
                       FirstSeenUtc, LastSeenUtc, SeenCount
                FROM JobPostings;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_JobPostings_ScrapeRuns_FirstSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropForeignKey(
                name: "FK_JobPostings_ScrapeRuns_LastSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_FirstSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_LastSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_SearchTerm_FirstSeenUtc",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_SearchTerm_FreshnessClass",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_SearchTerm_LastSeenUtc",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "FirstSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "LastSeenRunId",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "SearchTerm",
                table: "JobPostings");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_FreshnessClass",
                table: "JobPostings",
                column: "FreshnessClass");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_LastSeenUtc",
                table: "JobPostings",
                column: "LastSeenUtc");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobPostings_FreshnessClass",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_LastSeenUtc",
                table: "JobPostings");

            migrationBuilder.AddColumn<int>(
                name: "FirstSeenRunId",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastSeenRunId",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SearchTerm",
                table: "JobPostings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

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
                name: "IX_JobPostings_SearchTerm_FreshnessClass",
                table: "JobPostings",
                columns: new[] { "SearchTerm", "FreshnessClass" });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SearchTerm_LastSeenUtc",
                table: "JobPostings",
                columns: new[] { "SearchTerm", "LastSeenUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_JobPostings_ScrapeRuns_FirstSeenRunId",
                table: "JobPostings",
                column: "FirstSeenRunId",
                principalTable: "ScrapeRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JobPostings_ScrapeRuns_LastSeenRunId",
                table: "JobPostings",
                column: "LastSeenRunId",
                principalTable: "ScrapeRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // A posting may have several attributions and the column can hold one, so the
            // oldest is kept - the search that found it first. Genuinely lossy, which is
            // the nature of going back to a single column.
            migrationBuilder.Sql("""
                UPDATE p
                SET p.SearchTerm     = l.SearchTerm,
                    p.FirstSeenRunId = l.FirstSeenRunId,
                    p.LastSeenRunId  = l.LastSeenRunId
                FROM JobPostings p
                INNER JOIN (
                    SELECT PostingId, SearchTerm, FirstSeenRunId, LastSeenRunId,
                           ROW_NUMBER() OVER (PARTITION BY PostingId ORDER BY FirstSeenUtc) AS rn
                    FROM JobPostingSearchTerms
                ) l ON l.PostingId = p.Id AND l.rn = 1;
                """);

            migrationBuilder.DropTable(
                name: "JobPostingSearchTerms");
        }
    }
}
