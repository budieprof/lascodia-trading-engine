using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureConsensusSnapshotMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContributorModelIdsJson",
                table: "MLFeatureConsensusSnapshot",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "FeatureCount",
                table: "MLFeatureConsensusSnapshot",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ImportanceSourceSummaryJson",
                table: "MLFeatureConsensusSnapshot",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SchemaKey",
                table: "MLFeatureConsensusSnapshot",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureConsensusSnapshot_Symbol_Timeframe_SchemaKey_Detec~",
                table: "MLFeatureConsensusSnapshot",
                columns: new[] { "Symbol", "Timeframe", "SchemaKey", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLFeatureConsensusSnapshot_Symbol_Timeframe_SchemaKey_Detec~",
                table: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropColumn(
                name: "ContributorModelIdsJson",
                table: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropColumn(
                name: "FeatureCount",
                table: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropColumn(
                name: "ImportanceSourceSummaryJson",
                table: "MLFeatureConsensusSnapshot");

            migrationBuilder.DropColumn(
                name: "SchemaKey",
                table: "MLFeatureConsensusSnapshot");
        }
    }
}
