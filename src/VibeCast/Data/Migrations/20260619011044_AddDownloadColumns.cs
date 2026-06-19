using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoDownloadEnabled",
                table: "Feeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoDownloadMaxAgeDays",
                table: "Feeds",
                type: "INTEGER",
                nullable: true,
                defaultValue: 90);

            migrationBuilder.AddColumn<string>(
                name: "DownloadedFileName",
                table: "Episodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoDownloadEnabled",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "AutoDownloadMaxAgeDays",
                table: "Feeds");

            migrationBuilder.DropColumn(
                name: "DownloadedFileName",
                table: "Episodes");
        }
    }
}
