using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class ConceptGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsRemote",
                table: "JobPostings",
                type: "bit",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.AddColumn<string>(
                name: "AnnualSalaryCurrency",
                table: "JobPostings",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AnnualSalaryMax",
                table: "JobPostings",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AnnualSalaryMin",
                table: "JobPostings",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApplicantCount",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Applicants",
                table: "JobPostings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnrichmentVersion",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasContactEmail",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HybridDaysInOffice",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ir35",
                table: "JobPostings",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListingType",
                table: "JobPostings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresDegree",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresSecurityClearance",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RoleFamily",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SalaryFromText",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SalaryStatedInterval",
                table: "JobPostings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Seniority",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceBoard",
                table: "JobPostings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VacancyCount",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VisaSponsorship",
                table: "JobPostings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkArrangement",
                table: "JobPostings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WorkFromHomeType",
                table: "JobPostings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YearsExperienceMax",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YearsExperienceMin",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Industry = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EmployeesBand = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    EmployeesMin = table.Column<int>(type: "int", nullable: true),
                    EmployeesMax = table.Column<int>(type: "int", nullable: true),
                    Revenue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Rating = table.Column<double>(type: "float", nullable: true),
                    ReviewsCount = table.Column<int>(type: "int", nullable: true),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Concepts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConceptKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    PrefLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TaxonomyVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Concepts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobPostingJobTypes",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPostingJobTypes", x => new { x.PostingId, x.JobType });
                    table.ForeignKey(
                        name: "FK_JobPostingJobTypes_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostingExtractions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    ExtractorVersion = table.Column<int>(type: "int", nullable: false),
                    InputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExtractedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingExtractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostingExtractions_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostingMentions",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    SurfaceForm = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    ResolverVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingMentions", x => new { x.PostingId, x.SurfaceForm });
                    table.ForeignKey(
                        name: "FK_PostingMentions_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostingTags",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingTags", x => new { x.PostingId, x.Tag });
                    table.ForeignKey(
                        name: "FK_PostingTags_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConceptClosure",
                columns: table => new
                {
                    AncestorId = table.Column<int>(type: "int", nullable: false),
                    DescendantId = table.Column<int>(type: "int", nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptClosure", x => new { x.AncestorId, x.DescendantId });
                    table.ForeignKey(
                        name: "FK_ConceptClosure_Concepts_AncestorId",
                        column: x => x.AncestorId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConceptClosure_Concepts_DescendantId",
                        column: x => x.DescendantId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConceptLabels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConceptId = table.Column<int>(type: "int", nullable: false),
                    NormalizedLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConceptLabels_Concepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConceptRelations",
                columns: table => new
                {
                    FromConceptId = table.Column<int>(type: "int", nullable: false),
                    ToConceptId = table.Column<int>(type: "int", nullable: false),
                    RelationType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptRelations", x => new { x.FromConceptId, x.ToConceptId, x.RelationType });
                    table.ForeignKey(
                        name: "FK_ConceptRelations_Concepts_FromConceptId",
                        column: x => x.FromConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConceptRelations_Concepts_ToConceptId",
                        column: x => x.ToConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostingConcepts",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_PostingConcepts", x => new { x.PostingId, x.ConceptId, x.Source });
                    table.ForeignKey(
                        name: "FK_PostingConcepts_Concepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostingConcepts_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_CompanyId",
                table: "JobPostings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_EnrichmentVersion",
                table: "JobPostings",
                column: "EnrichmentVersion");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_RoleFamily",
                table: "JobPostings",
                column: "RoleFamily");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Seniority",
                table: "JobPostings",
                column: "Seniority");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_SourceBoard",
                table: "JobPostings",
                column: "SourceBoard");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_WorkArrangement",
                table: "JobPostings",
                column: "WorkArrangement");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_CompanyKey",
                table: "Companies",
                column: "CompanyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConceptClosure_DescendantId_AncestorId",
                table: "ConceptClosure",
                columns: new[] { "DescendantId", "AncestorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptLabels_ConceptId_NormalizedLabel",
                table: "ConceptLabels",
                columns: new[] { "ConceptId", "NormalizedLabel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConceptLabels_NormalizedLabel",
                table: "ConceptLabels",
                column: "NormalizedLabel");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptRelations_ToConceptId_RelationType",
                table: "ConceptRelations",
                columns: new[] { "ToConceptId", "RelationType" });

            migrationBuilder.CreateIndex(
                name: "IX_Concepts_ConceptKey",
                table: "Concepts",
                column: "ConceptKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Concepts_Kind",
                table: "Concepts",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostingJobTypes_JobType",
                table: "JobPostingJobTypes",
                column: "JobType");

            migrationBuilder.CreateIndex(
                name: "IX_PostingConcepts_ConceptId_PostingId",
                table: "PostingConcepts",
                columns: new[] { "ConceptId", "PostingId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingConcepts_ResolverVersion",
                table: "PostingConcepts",
                column: "ResolverVersion");

            migrationBuilder.CreateIndex(
                name: "IX_PostingExtractions_PostingId_ExtractorVersion_InputHash",
                table: "PostingExtractions",
                columns: new[] { "PostingId", "ExtractorVersion", "InputHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostingMentions_Reason_SurfaceForm",
                table: "PostingMentions",
                columns: new[] { "Reason", "SurfaceForm" });

            migrationBuilder.CreateIndex(
                name: "IX_PostingTags_Tag",
                table: "PostingTags",
                column: "Tag");

            migrationBuilder.AddForeignKey(
                name: "FK_JobPostings_Companies_CompanyId",
                table: "JobPostings",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobPostings_Companies_CompanyId",
                table: "JobPostings");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "ConceptClosure");

            migrationBuilder.DropTable(
                name: "ConceptLabels");

            migrationBuilder.DropTable(
                name: "ConceptRelations");

            migrationBuilder.DropTable(
                name: "JobPostingJobTypes");

            migrationBuilder.DropTable(
                name: "PostingConcepts");

            migrationBuilder.DropTable(
                name: "PostingExtractions");

            migrationBuilder.DropTable(
                name: "PostingMentions");

            migrationBuilder.DropTable(
                name: "PostingTags");

            migrationBuilder.DropTable(
                name: "Concepts");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_CompanyId",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_EnrichmentVersion",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_RoleFamily",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_Seniority",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_SourceBoard",
                table: "JobPostings");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_WorkArrangement",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "AnnualSalaryCurrency",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "AnnualSalaryMax",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "AnnualSalaryMin",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ApplicantCount",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Applicants",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "EnrichmentVersion",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "HasContactEmail",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "HybridDaysInOffice",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Ir35",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ListingType",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "RequiresDegree",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "RequiresSecurityClearance",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "RoleFamily",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "SalaryFromText",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "SalaryStatedInterval",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Seniority",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "SourceBoard",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "VacancyCount",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "VisaSponsorship",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "WorkArrangement",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "WorkFromHomeType",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "YearsExperienceMax",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "YearsExperienceMin",
                table: "JobPostings");

            migrationBuilder.AlterColumn<bool>(
                name: "IsRemote",
                table: "JobPostings",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);
        }
    }
}
