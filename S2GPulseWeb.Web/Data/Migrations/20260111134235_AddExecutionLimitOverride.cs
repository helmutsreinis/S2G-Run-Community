using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLimitOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionLimitOverride",
                table: "UserUsages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionLimitOverride",
                table: "UserUsages");
        }
    }
}
