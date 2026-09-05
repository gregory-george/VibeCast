using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedIsPaused : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaused",
                table: "Feeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPaused",
                table: "Feeds");
        }
    }
}
