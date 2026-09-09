using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class DraftedFieldProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftedFieldsJson",
                table: "SubmissionEvents",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftedFieldsJson",
                table: "SubmissionEvents");
        }
    }
}
