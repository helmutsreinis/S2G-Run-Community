using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipPlansTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MembershipPlanId",
                table: "UserSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MembershipPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DetailedDescription = table.Column<string>(type: "text", nullable: true),
                    SvgIcon = table.Column<string>(type: "text", nullable: true),
                    BadgeColorGradientStart = table.Column<string>(type: "text", nullable: true),
                    BadgeColorGradientEnd = table.Column<string>(type: "text", nullable: true),
                    BadgeTextColor = table.Column<string>(type: "text", nullable: true),
                    BadgeBorderColor = table.Column<string>(type: "text", nullable: true),
                    MonthlyPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    StripePriceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsFree = table.Column<bool>(type: "boolean", nullable: false),
                    IsContactSales = table.Column<bool>(type: "boolean", nullable: false),
                    MaxExecutionsPerMonth = table.Column<int>(type: "integer", nullable: false),
                    MaxStorageBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxWorkflows = table.Column<int>(type: "integer", nullable: false),
                    MaxNodesPerWorkflow = table.Column<int>(type: "integer", nullable: false),
                    MaxVectorDocs = table.Column<int>(type: "integer", nullable: false),
                    LogRetentionHours = table.Column<int>(type: "integer", nullable: false),
                    CanImportExport = table.Column<bool>(type: "boolean", nullable: false),
                    CanUseScheduling = table.Column<bool>(type: "boolean", nullable: false),
                    MaxHttpListeners = table.Column<int>(type: "integer", nullable: false),
                    MaxPaidMembers = table.Column<int>(type: "integer", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipPlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSubscriptions_MembershipPlanId",
                table: "UserSubscriptions",
                column: "MembershipPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPlans_DisplayOrder",
                table: "MembershipPlans",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPlans_IsActive",
                table: "MembershipPlans",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPlans_StripePriceId",
                table: "MembershipPlans",
                column: "StripePriceId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserSubscriptions_MembershipPlans_MembershipPlanId",
                table: "UserSubscriptions",
                column: "MembershipPlanId",
                principalTable: "MembershipPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSubscriptions_MembershipPlans_MembershipPlanId",
                table: "UserSubscriptions");

            migrationBuilder.DropTable(
                name: "MembershipPlans");

            migrationBuilder.DropIndex(
                name: "IX_UserSubscriptions_MembershipPlanId",
                table: "UserSubscriptions");

            migrationBuilder.DropColumn(
                name: "MembershipPlanId",
                table: "UserSubscriptions");
        }
    }
}
