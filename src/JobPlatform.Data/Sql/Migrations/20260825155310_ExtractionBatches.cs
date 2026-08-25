using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class ExtractionBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtractionBatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderBatchId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Requested = table.Column<int>(type: "int", nullable: false),
                    Succeeded = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtractionBatchItems",
                columns: table => new
                {
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    InputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionBatchItems", x => new { x.BatchId, x.PostingId });
                    table.ForeignKey(
                        name: "FK_ExtractionBatchItems_ExtractionBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ExtractionBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtractionBatchItems_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionBatches_ProviderBatchId",
                table: "ExtractionBatches",
                column: "ProviderBatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionBatches_State_SubmittedAtUtc",
                table: "ExtractionBatches",
                columns: new[] { "State", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionBatchItems_PostingId",
                table: "ExtractionBatchItems",
                column: "PostingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtractionBatchItems");

            migrationBuilder.DropTable(
                name: "ExtractionBatches");
        }
    }
}
