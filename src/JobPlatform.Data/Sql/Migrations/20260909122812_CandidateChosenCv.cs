using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class CandidateChosenCv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChosenCvAtUtc",
                table: "JobMatches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ChosenCvVariantId",
                table: "JobMatches",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_ChosenCvVariantId",
                table: "JobMatches",
                column: "ChosenCvVariantId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobMatches_CvVariants_ChosenCvVariantId",
                table: "JobMatches",
                column: "ChosenCvVariantId",
                principalTable: "CvVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobMatches_CvVariants_ChosenCvVariantId",
                table: "JobMatches");

            migrationBuilder.DropIndex(
                name: "IX_JobMatches_ChosenCvVariantId",
                table: "JobMatches");

            migrationBuilder.DropColumn(
                name: "ChosenCvAtUtc",
                table: "JobMatches");

            migrationBuilder.DropColumn(
                name: "ChosenCvVariantId",
                table: "JobMatches");
        }
    }
}
