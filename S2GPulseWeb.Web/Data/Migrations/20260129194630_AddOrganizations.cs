using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Workflows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveOrganizationId",
                table: "UserPreferences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxMembersPerOrganization",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxNodesPerOrgWorkflow",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxOrgExecutionsPerMonth",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "MaxOrgStorageBytes",
                table: "MembershipPlans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxOrgVectorDocs",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxOrganizations",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxWorkflowsPerOrganization",
                table: "MembershipPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SvgLogo = table.Column<string>(type: "text", nullable: true),
                    FounderId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Organizations_AspNetUsers_FounderId",
                        column: x => x.FounderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvitedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizationMembers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageBytesWorkflows = table.Column<long>(type: "bigint", nullable: false),
                    StorageBytesLogs = table.Column<long>(type: "bigint", nullable: false),
                    StorageBytesVectors = table.Column<long>(type: "bigint", nullable: false),
                    StorageBytesTables = table.Column<long>(type: "bigint", nullable: false),
                    StorageBytesBlobStorage = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionsThisMonth = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationUsages_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_OrganizationId",
                table: "Workflows",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMembers_OrganizationId_UserId",
                table: "OrganizationMembers",
                columns: new[] { "OrganizationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMembers_UserId",
                table: "OrganizationMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_FounderId",
                table: "Organizations",
                column: "FounderId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_IsActive",
                table: "Organizations",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUsages_OrganizationId",
                table: "OrganizationUsages",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Workflows_Organizations_OrganizationId",
                table: "Workflows",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Workflows_Organizations_OrganizationId",
                table: "Workflows");

            migrationBuilder.DropTable(
                name: "OrganizationMembers");

            migrationBuilder.DropTable(
                name: "OrganizationUsages");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Workflows_OrganizationId",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "ActiveOrganizationId",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "MaxMembersPerOrganization",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxNodesPerOrgWorkflow",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxOrgExecutionsPerMonth",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxOrgStorageBytes",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxOrgVectorDocs",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxOrganizations",
                table: "MembershipPlans");

            migrationBuilder.DropColumn(
                name: "MaxWorkflowsPerOrganization",
                table: "MembershipPlans");
        }
    }
}
