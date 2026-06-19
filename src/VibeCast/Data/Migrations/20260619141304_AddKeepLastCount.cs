using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKeepLastCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeepLastCount",
                table: "Feeds",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepLastCount",
                table: "Feeds");
        }
    }
}
