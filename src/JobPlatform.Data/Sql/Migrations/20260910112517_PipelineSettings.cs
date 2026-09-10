using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class PipelineSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineSettings",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentsPerNight = table.Column<int>(type: "int", nullable: false),
                    AssessmentThreshold = table.Column<int>(type: "int", nullable: false),
                    RecentSharePercent = table.Column<int>(type: "int", nullable: false),
                    RecentWindowDays = table.Column<int>(type: "int", nullable: false),
                    DraftsPerNight = table.Column<int>(type: "int", nullable: false),
                    DraftMinAssessmentScore = table.Column<int>(type: "int", nullable: false),
                    DraftPostedWithinDays = table.Column<int>(type: "int", nullable: true),
                    DailySendCap = table.Column<int>(type: "int", nullable: false),
                    ChaseAfterDays = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSettings", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_PipelineSettings_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineSettings");
        }
    }
}
