using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class CvLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CvSelectionVersion",
                table: "Submissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CvVariantId",
                table: "Submissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CvVariants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LabelKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Markdown = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthoredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RenderedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PdfBlobPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    DocxBlobPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CvVariants_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionParkGaps",
                columns: table => new
                {
                    SubmissionId = table.Column<long>(type: "bigint", nullable: false),
                    ConceptId = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionParkGaps", x => new { x.SubmissionId, x.ConceptId });
                    table.ForeignKey(
                        name: "FK_SubmissionParkGaps_Concepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionParkGaps_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CvVariantConcepts",
                columns: table => new
                {
                    VariantId = table.Column<long>(type: "bigint", nullable: false),
                    ConceptId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Polarity = table.Column<int>(type: "int", nullable: false),
                    YearsMin = table.Column<int>(type: "int", nullable: true),
                    YearsMax = table.Column<int>(type: "int", nullable: true),
                    EvidenceText = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    ResolverVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CvVariantConcepts", x => new { x.VariantId, x.ConceptId, x.Source });
                    table.ForeignKey(
                        name: "FK_CvVariantConcepts_Concepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CvVariantConcepts_CvVariants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "CvVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_CvVariantId",
                table: "Submissions",
                column: "CvVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_CvVariantConcepts_ConceptId_VariantId",
                table: "CvVariantConcepts",
                columns: new[] { "ConceptId", "VariantId" });

            migrationBuilder.CreateIndex(
                name: "IX_CvVariants_LiveLabel",
                table: "CvVariants",
                columns: new[] { "ProfileId", "LabelKey" },
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_CvVariants_ProfileId_IsArchived",
                table: "CvVariants",
                columns: new[] { "ProfileId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionParkGaps_ConceptId_SubmissionId",
                table: "SubmissionParkGaps",
                columns: new[] { "ConceptId", "SubmissionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_CvVariants_CvVariantId",
                table: "Submissions",
                column: "CvVariantId",
                principalTable: "CvVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_CvVariants_CvVariantId",
                table: "Submissions");

            migrationBuilder.DropTable(
                name: "CvVariantConcepts");

            migrationBuilder.DropTable(
                name: "SubmissionParkGaps");

            migrationBuilder.DropTable(
                name: "CvVariants");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_CvVariantId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CvSelectionVersion",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CvVariantId",
                table: "Submissions");
        }
    }
}
