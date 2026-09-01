using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobPlatform.Data.Sql.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossBoardLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_JobPostings_Company_LocationCity",
                table: "JobPostings",
                columns: new[] { "Company", "LocationCity" })
                .Annotation("SqlServer:Include", new[] { "Title", "JobUrlDirect", "Site" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobPostings_Company_LocationCity",
                table: "JobPostings");
        }
    }
}
