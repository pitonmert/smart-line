using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartLine.API.Migrations
{
    /// <inheritdoc />
    public partial class DatasetSingleActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DatasetSessions_OneActive",
                table: "DatasetSessions",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'ACTIVE'"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DatasetSessions_OneActive",
                table: "DatasetSessions"
            );
        }
    }
}
