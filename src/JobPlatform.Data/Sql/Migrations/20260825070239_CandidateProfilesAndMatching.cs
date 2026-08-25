using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class CandidateProfilesAndMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Headline = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    LocationCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WillingToRelocate = table.Column<bool>(type: "bit", nullable: false),
                    PreferredArrangement = table.Column<int>(type: "int", nullable: false),
                    MaxDaysInOffice = table.Column<int>(type: "int", nullable: true),
                    MinimumSalary = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: true),
                    SalaryCurrency = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    YearsExperience = table.Column<int>(type: "int", nullable: true),
                    Seniority = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExtractionInputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ExtractorVersion = table.Column<int>(type: "int", nullable: true),
                    ExtractionModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExtractedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExtractionPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CurriculumVitaeMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoverLetterMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmphasisedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WriterVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationDocuments_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationDocuments_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobMatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    ComponentsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GapsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequiredGapCount = table.Column<int>(type: "int", nullable: false),
                    ScorerVersion = table.Column<int>(type: "int", nullable: false),
                    ScoredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Verdict = table.Column<int>(type: "int", nullable: true),
                    AssessmentScore = table.Column<int>(type: "int", nullable: true),
                    Rationale = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StrengthsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssessmentGapsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmphasiseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssessmentModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AssessmentVersion = table.Column<int>(type: "int", nullable: true),
                    AssessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AssessmentPayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobMatches_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobMatches_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProfileCertifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Year = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileCertifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileCertifications_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileConcepts",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_ProfileConcepts", x => new { x.ProfileId, x.ConceptId, x.Source });
                    table.ForeignKey(
                        name: "FK_ProfileConcepts_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileConcepts_Concepts_ConceptId",
                        column: x => x.ConceptId,
                        principalTable: "Concepts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProfileEducation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Institution = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Qualification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FieldOfStudy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Grade = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileEducation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileEducation_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileExperiences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Company = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    LocationCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileExperiences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileExperiences_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileJobTypes",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileJobTypes", x => new { x.ProfileId, x.JobType });
                    table.ForeignKey(
                        name: "FK_ProfileJobTypes_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileLanguages",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileLanguages", x => new { x.ProfileId, x.Name });
                    table.ForeignKey(
                        name: "FK_ProfileLanguages_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileLinks",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileLinks", x => new { x.ProfileId, x.Label });
                    table.ForeignKey(
                        name: "FK_ProfileLinks_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileMentions",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    SurfaceForm = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    ResolverVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileMentions", x => new { x.ProfileId, x.SurfaceForm });
                    table.ForeignKey(
                        name: "FK_ProfileMentions_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileProjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileProjects_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationDocuments_PostingId",
                table: "ApplicationDocuments",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationDocuments_ProfileId_CreatedAtUtc",
                table: "ApplicationDocuments",
                columns: new[] { "ProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationDocuments_ProfileId_PostingId_Revision",
                table: "ApplicationDocuments",
                columns: new[] { "ProfileId", "PostingId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateProfiles_SubjectId",
                table: "CandidateProfiles",
                column: "SubjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_PostingId",
                table: "JobMatches",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_ProfileId_AssessedAtUtc",
                table: "JobMatches",
                columns: new[] { "ProfileId", "AssessedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_ProfileId_PostingId",
                table: "JobMatches",
                columns: new[] { "ProfileId", "PostingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_ProfileId_Score",
                table: "JobMatches",
                columns: new[] { "ProfileId", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileCertifications_ProfileId_Ordinal",
                table: "ProfileCertifications",
                columns: new[] { "ProfileId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileConcepts_ConceptId_ProfileId",
                table: "ProfileConcepts",
                columns: new[] { "ConceptId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileEducation_ProfileId_Ordinal",
                table: "ProfileEducation",
                columns: new[] { "ProfileId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileExperiences_ProfileId_Ordinal",
                table: "ProfileExperiences",
                columns: new[] { "ProfileId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileMentions_Reason_SurfaceForm",
                table: "ProfileMentions",
                columns: new[] { "Reason", "SurfaceForm" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileProjects_ProfileId_Ordinal",
                table: "ProfileProjects",
                columns: new[] { "ProfileId", "Ordinal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationDocuments");

            migrationBuilder.DropTable(
                name: "JobMatches");

            migrationBuilder.DropTable(
                name: "ProfileCertifications");

            migrationBuilder.DropTable(
                name: "ProfileConcepts");

            migrationBuilder.DropTable(
                name: "ProfileEducation");

            migrationBuilder.DropTable(
                name: "ProfileExperiences");

            migrationBuilder.DropTable(
                name: "ProfileJobTypes");

            migrationBuilder.DropTable(
                name: "ProfileLanguages");

            migrationBuilder.DropTable(
                name: "ProfileLinks");

            migrationBuilder.DropTable(
                name: "ProfileMentions");

            migrationBuilder.DropTable(
                name: "ProfileProjects");

            migrationBuilder.DropTable(
                name: "CandidateProfiles");
        }
    }
}
