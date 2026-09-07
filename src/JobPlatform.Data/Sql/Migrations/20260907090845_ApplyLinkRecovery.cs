using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class ApplyLinkRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmployerAtsApplyUrl",
                table: "JobPostings",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmployerAtsCheckedUtc",
                table: "JobPostings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmployerAtsMatchConfidence",
                table: "JobPostings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmployerAtsBoards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    Vendor = table.Column<int>(type: "int", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Region = table.Column<int>(type: "int", nullable: false),
                    Discovery = table.Column<int>(type: "int", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFetchedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployerAtsBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployerAtsBoards_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployerAtsBoards_Identity",
                table: "EmployerAtsBoards",
                columns: new[] { "CompanyId", "Vendor", "Token", "Region" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "Discovery", "ConfirmedAtUtc", "LastFetchedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployerAtsBoards");

            migrationBuilder.DropColumn(
                name: "EmployerAtsApplyUrl",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "EmployerAtsCheckedUtc",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "EmployerAtsMatchConfidence",
                table: "JobPostings");
        }
    }
}
