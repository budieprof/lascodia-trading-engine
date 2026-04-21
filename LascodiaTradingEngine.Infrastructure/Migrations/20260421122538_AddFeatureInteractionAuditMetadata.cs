using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureInteractionAuditMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseFeatureCount",
                table: "MLFeatureInteractionAudit",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "EffectSize",
                table: "MLFeatureInteractionAudit",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "FeatureSchemaVersion",
                table: "MLFeatureInteractionAudit",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "MLFeatureInteractionAudit",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "PValue",
                table: "MLFeatureInteractionAudit",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "QValue",
                table: "MLFeatureInteractionAudit",
                type: "double precision",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SampleCount",
                table: "MLFeatureInteractionAudit",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureInteractionAudit_Symbol_Timeframe_IsIncludedAsFeat~",
                table: "MLFeatureInteractionAudit",
                columns: new[] { "Symbol", "Timeframe", "IsIncludedAsFeature", "ComputedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MLFeatureInteractionAudit_Symbol_Timeframe_IsIncludedAsFeat~",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "BaseFeatureCount",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "EffectSize",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "FeatureSchemaVersion",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "PValue",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "QValue",
                table: "MLFeatureInteractionAudit");

            migrationBuilder.DropColumn(
                name: "SampleCount",
                table: "MLFeatureInteractionAudit");
        }
    }
}
