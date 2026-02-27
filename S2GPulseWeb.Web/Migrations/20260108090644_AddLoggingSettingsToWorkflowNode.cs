using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLoggingSettingsToWorkflowNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoggingSettingsJson",
                table: "WorkflowNodes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoggingSettingsJson",
                table: "WorkflowNodes");
        }
    }
}
