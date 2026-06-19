using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastRefreshError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastRefreshError",
                table: "Feeds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRefreshError",
                table: "Feeds");
        }
    }
}
