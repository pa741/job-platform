using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AwaitingQuestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AwaitingQuestionId",
                table: "Submissions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_AwaitingQuestionId",
                table: "Submissions",
                column: "AwaitingQuestionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_OpenQuestions_AwaitingQuestionId",
                table: "Submissions",
                column: "AwaitingQuestionId",
                principalTable: "OpenQuestions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_OpenQuestions_AwaitingQuestionId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_AwaitingQuestionId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "AwaitingQuestionId",
                table: "Submissions");
        }
    }
}
