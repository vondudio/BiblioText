using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BiblioText.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCopyBookshelfImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BookshelfImageUrl",
                table: "Copies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpineBoxNorm",
                table: "Copies",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BookshelfImageUrl",
                table: "Copies");

            migrationBuilder.DropColumn(
                name: "SpineBoxNorm",
                table: "Copies");
        }
    }
}
