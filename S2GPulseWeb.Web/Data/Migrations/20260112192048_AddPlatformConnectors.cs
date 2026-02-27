using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PlatformConnectorId",
                table: "OAuthConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectorCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IconEmoji = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformConnectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    ConsentType = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientSecretEncrypted = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequiredScopes = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConnectors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformConnectors_ConnectorCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ConnectorCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OAuthConnections_PlatformConnectorId",
                table: "OAuthConnections",
                column: "PlatformConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorCategories_DisplayOrder",
                table: "ConnectorCategories",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnectors_CategoryId",
                table: "PlatformConnectors",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformConnectors_IsEnabled",
                table: "PlatformConnectors",
                column: "IsEnabled");

            migrationBuilder.AddForeignKey(
                name: "FK_OAuthConnections_PlatformConnectors_PlatformConnectorId",
                table: "OAuthConnections",
                column: "PlatformConnectorId",
                principalTable: "PlatformConnectors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OAuthConnections_PlatformConnectors_PlatformConnectorId",
                table: "OAuthConnections");

            migrationBuilder.DropTable(
                name: "PlatformConnectors");

            migrationBuilder.DropTable(
                name: "ConnectorCategories");

            migrationBuilder.DropIndex(
                name: "IX_OAuthConnections_PlatformConnectorId",
                table: "OAuthConnections");

            migrationBuilder.DropColumn(
                name: "PlatformConnectorId",
                table: "OAuthConnections");
        }
    }
}
