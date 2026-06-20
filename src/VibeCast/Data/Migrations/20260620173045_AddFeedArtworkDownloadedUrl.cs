using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VibeCast.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedArtworkDownloadedUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtworkDownloadedUrl",
                table: "Feeds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtworkDownloadedUrl",
                table: "Feeds");
        }
    }
}
