using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartLine.API.Migrations
{
    /// <inheritdoc />
    public partial class InspectionPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalibrationProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalibrationProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatasetSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecipeName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QualityLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ColorLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefectLabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CaptureMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IntervalMs = table.Column<int>(type: "integer", nullable: true),
                    ImageCount = table.Column<int>(type: "integer", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSimulation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InspectionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProductClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ColorClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeClass = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefectClassesJson = table.Column<string>(type: "jsonb", nullable: false),
                    QualityStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DestinationBin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProcessingLatencyMs = table.Column<int>(type: "integer", nullable: true),
                    ImageReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsSimulation = table.Column<bool>(type: "boolean", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductRecipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductRecipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: true),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsSimulation = table.Column<bool>(type: "boolean", nullable: false),
                    DestinationBin = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InspectingAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ManualReviewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductTracks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationProfiles_IsActive",
                table: "CalibrationProfiles",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_CalibrationProfiles_Name",
                table: "CalibrationProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatasetSessions_StartedAt",
                table: "DatasetSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DatasetSessions_Status",
                table: "DatasetSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionRecords_IsSimulation",
                table: "InspectionRecords",
                column: "IsSimulation");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionRecords_ProcessedAt",
                table: "InspectionRecords",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionRecords_ProductId",
                table: "InspectionRecords",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionRecords_RecipeId_Decision",
                table: "InspectionRecords",
                columns: new[] { "RecipeId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductRecipes_IsActive",
                table: "ProductRecipes",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ProductRecipes_Name",
                table: "ProductRecipes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductTracks_IsSimulation",
                table: "ProductTracks",
                column: "IsSimulation");

            migrationBuilder.CreateIndex(
                name: "IX_ProductTracks_PublicCode",
                table: "ProductTracks",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductTracks_Stage_SortDueAt",
                table: "ProductTracks",
                columns: new[] { "Stage", "SortDueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalibrationProfiles");

            migrationBuilder.DropTable(
                name: "DatasetSessions");

            migrationBuilder.DropTable(
                name: "InspectionRecords");

            migrationBuilder.DropTable(
                name: "ProductRecipes");

            migrationBuilder.DropTable(
                name: "ProductTracks");
        }
    }
}
