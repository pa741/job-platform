using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddApplyLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAtUtc",
                table: "Submissions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Submissions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DocumentRevision",
                table: "Submissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ParkedAtUtc",
                table: "Submissions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParkedReason",
                table: "Submissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RunId",
                table: "Submissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnparkedAtUtc",
                table: "Submissions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationRef",
                table: "SubmissionEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalUrl",
                table: "SubmissionEvents",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenshotRef",
                table: "SubmissionEvents",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedFieldsJson",
                table: "SubmissionEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrossBoardKey",
                table: "JobPostings",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoverLetterBlobPath",
                table: "ApplicationDocuments",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CvBlobPath",
                table: "ApplicationDocuments",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CvDocxBlobPath",
                table: "ApplicationDocuments",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CvSha256",
                table: "ApplicationDocuments",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftedAnswersJson",
                table: "ApplicationDocuments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FormAnswers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QuestionText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    QuestionHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    NormalisedQuestion = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: true),
                    PostingId = table.Column<long>(type: "bigint", nullable: true),
                    Sensitive = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AnsweredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FormAnswers_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FormAnswers_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FormAnswers_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FormAnswerResolutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    QuestionHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OptionsHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ResolvedName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AnswerId = table.Column<long>(type: "bigint", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Confirmed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormAnswerResolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FormAnswerResolutions_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FormAnswerResolutions_FormAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "FormAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenQuestions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    PostingId = table.Column<long>(type: "bigint", nullable: true),
                    RunId = table.Column<long>(type: "bigint", nullable: true),
                    QuestionText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    QuestionHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Sensitive = table.Column<bool>(type: "bit", nullable: false),
                    AskedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AnsweredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AnswerId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_FormAnswers_AnswerId",
                        column: x => x.AnswerId,
                        principalTable: "FormAnswers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpenQuestions_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_RunId",
                table: "Submissions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_CrossBoardKey",
                table: "JobPostings",
                column: "CrossBoardKey");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswerResolutions_AnswerId",
                table: "FormAnswerResolutions",
                column: "AnswerId");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswerResolutions_FreeText",
                table: "FormAnswerResolutions",
                columns: new[] { "ProfileId", "QuestionHash" },
                unique: true,
                filter: "[OptionsHash] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswerResolutions_Options",
                table: "FormAnswerResolutions",
                columns: new[] { "ProfileId", "QuestionHash", "OptionsHash" },
                unique: true,
                filter: "[OptionsHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_CompanyId",
                table: "FormAnswers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_LiveCompany",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "QuestionHash", "CompanyId" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL AND [Scope] = 2 AND [CompanyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_LiveGlobal",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "QuestionHash" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL AND [Scope] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_LivePosting",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "QuestionHash", "PostingId" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL AND [Scope] = 3 AND [PostingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_PostingId",
                table: "FormAnswers",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_ProfileId_AnsweredAtUtc",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "AnsweredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_ProfileId_Name",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_FormAnswers_ProfileId_QuestionHash",
                table: "FormAnswers",
                columns: new[] { "ProfileId", "QuestionHash" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_AnswerId",
                table: "OpenQuestions",
                column: "AnswerId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_PostingId",
                table: "OpenQuestions",
                column: "PostingId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_ProfileId_AskedAtUtc",
                table: "OpenQuestions",
                columns: new[] { "ProfileId", "AskedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_ProfileId_PostingId",
                table: "OpenQuestions",
                columns: new[] { "ProfileId", "PostingId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_RunId",
                table: "OpenQuestions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenQuestions_Unanswered",
                table: "OpenQuestions",
                columns: new[] { "ProfileId", "QuestionHash" },
                unique: true,
                filter: "[AnsweredAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ProfileId_StartedAtUtc",
                table: "Runs",
                columns: new[] { "ProfileId", "StartedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_Runs_RunId",
                table: "Submissions",
                column: "RunId",
                principalTable: "Runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_Runs_RunId",
                table: "Submissions");

            migrationBuilder.DropTable(
                name: "FormAnswerResolutions");

            migrationBuilder.DropTable(
                name: "OpenQuestions");

            migrationBuilder.DropTable(
                name: "FormAnswers");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_RunId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_JobPostings_CrossBoardKey",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "DocumentRevision",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ParkedAtUtc",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ParkedReason",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "UnparkedAtUtc",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ConfirmationRef",
                table: "SubmissionEvents");

            migrationBuilder.DropColumn(
                name: "FinalUrl",
                table: "SubmissionEvents");

            migrationBuilder.DropColumn(
                name: "ScreenshotRef",
                table: "SubmissionEvents");

            migrationBuilder.DropColumn(
                name: "SubmittedFieldsJson",
                table: "SubmissionEvents");

            migrationBuilder.DropColumn(
                name: "CrossBoardKey",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "CoverLetterBlobPath",
                table: "ApplicationDocuments");

            migrationBuilder.DropColumn(
                name: "CvBlobPath",
                table: "ApplicationDocuments");

            migrationBuilder.DropColumn(
                name: "CvDocxBlobPath",
                table: "ApplicationDocuments");

            migrationBuilder.DropColumn(
                name: "CvSha256",
                table: "ApplicationDocuments");

            migrationBuilder.DropColumn(
                name: "DraftedAnswersJson",
                table: "ApplicationDocuments");
        }
    }
}
