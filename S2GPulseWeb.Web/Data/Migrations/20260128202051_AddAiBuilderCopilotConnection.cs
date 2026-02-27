using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiBuilderCopilotConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AiBuilderCopilotConnectionId",
                table: "UserPreferences",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiBuilderCopilotConnectionId",
                table: "UserPreferences");
        }
    }
}
