using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgScopedSecretsAndConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "UserSecrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "OAuthConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSecrets_OrganizationId",
                table: "UserSecrets",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthConnections_OrganizationId",
                table: "OAuthConnections",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_OAuthConnections_Organizations_OrganizationId",
                table: "OAuthConnections",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSecrets_Organizations_OrganizationId",
                table: "UserSecrets",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OAuthConnections_Organizations_OrganizationId",
                table: "OAuthConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSecrets_Organizations_OrganizationId",
                table: "UserSecrets");

            migrationBuilder.DropIndex(
                name: "IX_UserSecrets_OrganizationId",
                table: "UserSecrets");

            migrationBuilder.DropIndex(
                name: "IX_OAuthConnections_OrganizationId",
                table: "OAuthConnections");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "UserSecrets");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "OAuthConnections");
        }
    }
}
