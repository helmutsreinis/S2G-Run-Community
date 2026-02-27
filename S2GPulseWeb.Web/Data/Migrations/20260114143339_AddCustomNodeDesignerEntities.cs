using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace S2GPulseWeb.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomNodeDesignerEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomNodeCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IconEmoji = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomNodeDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeTypeKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IconSvg = table.Column<string>(type: "text", nullable: false),
                    IconFallbackEmoji = table.Column<string>(type: "text", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionType = table.Column<int>(type: "integer", nullable: false),
                    ExecutionDelayMs = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Script = table.Column<string>(type: "text", nullable: false),
                    InitializationScript = table.Column<string>(type: "text", nullable: true),
                    InputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    DefaultConfigurationJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomNodeDefinitions_CustomNodeCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "CustomNodeCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CustomNodeConnectionTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true),
                    ConditionDescription = table.Column<string>(type: "text", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeConnectionTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomNodeConnectionTags_CustomNodeDefinitions_NodeDefiniti~",
                        column: x => x.NodeDefinitionId,
                        principalTable: "CustomNodeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomNodeInputFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlaceholderText = table.Column<string>(type: "text", nullable: true),
                    HelpText = table.Column<string>(type: "text", nullable: true),
                    FieldType = table.Column<int>(type: "integer", nullable: false),
                    DefaultValue = table.Column<string>(type: "text", nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    AllowPlaceholders = table.Column<bool>(type: "boolean", nullable: false),
                    ValidationRegex = table.Column<string>(type: "text", nullable: true),
                    ExpectedDataType = table.Column<string>(type: "text", nullable: true),
                    JsonSchemaValidation = table.Column<string>(type: "text", nullable: true),
                    SelectOptionsJson = table.Column<string>(type: "text", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeInputFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomNodeInputFields_CustomNodeDefinitions_NodeDefinitionId",
                        column: x => x.NodeDefinitionId,
                        principalTable: "CustomNodeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomNodeLogConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LogTarget = table.Column<int>(type: "integer", nullable: false),
                    TargetName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LogLevel = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MessageFormat = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeLogConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomNodeLogConfigs_CustomNodeDefinitions_NodeDefinitionId",
                        column: x => x.NodeDefinitionId,
                        principalTable: "CustomNodeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomNodeOutputParameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DataType = table.Column<string>(type: "text", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomNodeOutputParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomNodeOutputParameters_CustomNodeDefinitions_NodeDefini~",
                        column: x => x.NodeDefinitionId,
                        principalTable: "CustomNodeDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeCategories_DisplayOrder",
                table: "CustomNodeCategories",
                column: "DisplayOrder");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeConnectionTags_NodeDefinitionId",
                table: "CustomNodeConnectionTags",
                column: "NodeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeConnectionTags_NodeDefinitionId_TagName",
                table: "CustomNodeConnectionTags",
                columns: new[] { "NodeDefinitionId", "TagName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeDefinitions_CategoryId",
                table: "CustomNodeDefinitions",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeDefinitions_IsEnabled",
                table: "CustomNodeDefinitions",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeDefinitions_NodeTypeKey",
                table: "CustomNodeDefinitions",
                column: "NodeTypeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeInputFields_NodeDefinitionId",
                table: "CustomNodeInputFields",
                column: "NodeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeInputFields_NodeDefinitionId_FieldName",
                table: "CustomNodeInputFields",
                columns: new[] { "NodeDefinitionId", "FieldName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeLogConfigs_NodeDefinitionId",
                table: "CustomNodeLogConfigs",
                column: "NodeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeOutputParameters_NodeDefinitionId",
                table: "CustomNodeOutputParameters",
                column: "NodeDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomNodeOutputParameters_NodeDefinitionId_ParameterName",
                table: "CustomNodeOutputParameters",
                columns: new[] { "NodeDefinitionId", "ParameterName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomNodeConnectionTags");

            migrationBuilder.DropTable(
                name: "CustomNodeInputFields");

            migrationBuilder.DropTable(
                name: "CustomNodeLogConfigs");

            migrationBuilder.DropTable(
                name: "CustomNodeOutputParameters");

            migrationBuilder.DropTable(
                name: "CustomNodeDefinitions");

            migrationBuilder.DropTable(
                name: "CustomNodeCategories");
        }
    }
}
