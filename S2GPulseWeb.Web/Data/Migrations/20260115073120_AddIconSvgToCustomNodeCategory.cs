using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIconSvgToCustomNodeCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IconSvg",
                table: "CustomNodeCategories",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IconSvg",
                table: "CustomNodeCategories");
        }
    }
}
