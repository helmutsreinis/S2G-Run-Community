using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTutorialPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TutorialCompleted",
                table: "UserPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TutorialLastStep",
                table: "UserPreferences",
                type: "integer",
                nullable: true);

            // Option B: Mark all existing users as tutorial-completed
            // so only brand-new registrations see the onboarding tutorial.
            migrationBuilder.Sql(
                "UPDATE \"UserPreferences\" SET \"TutorialCompleted\" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TutorialCompleted",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "TutorialLastStep",
                table: "UserPreferences");
        }
    }
}
