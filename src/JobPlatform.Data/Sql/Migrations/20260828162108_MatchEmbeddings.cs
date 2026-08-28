using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class MatchEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "RankScore",
                table: "JobMatches",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "RankerVersion",
                table: "JobMatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "Similarity",
                table: "JobMatches",
                type: "float",
                nullable: true);

            // Every row already in the table was scored before a ranker existed, and a default of
            // zero would sink all of them below the first pair the next sweep ranks - so the
            // matches page would be ordered by nothing at all between this migration and 03:30.
            // Seeding the key with the score reproduces exactly the ordering these rows had
            // yesterday, and RankerVersion stays at 0 so the sweep still re-ranks them.
            migrationBuilder.Sql("UPDATE JobMatches SET RankScore = Score;");

            migrationBuilder.CreateTable(
                name: "PostingEmbeddings",
                columns: table => new
                {
                    PostingId = table.Column<long>(type: "bigint", nullable: false),
                    Vector = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DescriptionLength = table.Column<int>(type: "int", nullable: false),
                    EmbeddingVersion = table.Column<int>(type: "int", nullable: false),
                    EmbeddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostingEmbeddings", x => x.PostingId);
                    table.ForeignKey(
                        name: "FK_PostingEmbeddings_JobPostings_PostingId",
                        column: x => x.PostingId,
                        principalTable: "JobPostings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileEmbeddings",
                columns: table => new
                {
                    ProfileId = table.Column<long>(type: "bigint", nullable: false),
                    Vector = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EmbeddingVersion = table.Column<int>(type: "int", nullable: false),
                    EmbeddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileEmbeddings", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_ProfileEmbeddings_CandidateProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "CandidateProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobMatches_ProfileId_RankScore",
                table: "JobMatches",
                columns: new[] { "ProfileId", "RankScore" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostingEmbeddings");

            migrationBuilder.DropTable(
                name: "ProfileEmbeddings");

            migrationBuilder.DropIndex(
                name: "IX_JobMatches_ProfileId_RankScore",
                table: "JobMatches");

            migrationBuilder.DropColumn(
                name: "RankScore",
                table: "JobMatches");

            migrationBuilder.DropColumn(
                name: "RankerVersion",
                table: "JobMatches");

            migrationBuilder.DropColumn(
                name: "Similarity",
                table: "JobMatches");
        }
    }
}
