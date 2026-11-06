using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeIsWatched : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWatched",
                table: "Episodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsWatched",
                table: "Episodes");
        }
    }
}
