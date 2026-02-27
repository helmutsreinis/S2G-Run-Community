using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsletterFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowAsNewsletter",
                table: "DeveloperNotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UserDismissedNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    NoteId = table.Column<int>(type: "integer", nullable: false),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDismissedNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserDismissedNotes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserDismissedNotes_DeveloperNotes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "DeveloperNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDismissedNotes_NoteId",
                table: "UserDismissedNotes",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDismissedNotes_UserId",
                table: "UserDismissedNotes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDismissedNotes");

            migrationBuilder.DropColumn(
                name: "ShowAsNewsletter",
                table: "DeveloperNotes");
        }
    }
}
