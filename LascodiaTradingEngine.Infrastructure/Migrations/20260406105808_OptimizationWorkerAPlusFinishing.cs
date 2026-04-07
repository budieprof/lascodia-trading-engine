using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationWorkerAPlusFinishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "WorkerHealthSnapshot",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RolloutLastOutcome",
                table: "Strategy",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RolloutLastFailureMessage",
                table: "Strategy",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolloutLastDiagnosticsJson",
                table: "Strategy",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompletionPublicationErrorMessage",
                table: "OptimizationRun",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Strategy_Status_RolloutPct_RolloutStartedAt",
                table: "Strategy",
                columns: new[] { "Status", "RolloutPct", "RolloutStartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_QueuedAt",
                table: "OptimizationRun",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_CompletionPublicationStatus_Completi~",
                table: "OptimizationRun",
                columns: new[] { "Status", "CompletionPublicationStatus", "CompletionPublicationLastAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_Status_NextFollowUpCheckAt",
                table: "OptimizationRun",
                columns: new[] { "Status", "NextFollowUpCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRun_StrategyId_CompletedAt",
                table: "OptimizationRun",
                columns: new[] { "StrategyId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strategy_Status_RolloutPct_RolloutStartedAt",
                table: "Strategy");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_QueuedAt",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_CompletionPublicationStatus_Completi~",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_Status_NextFollowUpCheckAt",
                table: "OptimizationRun");

            migrationBuilder.DropIndex(
                name: "IX_OptimizationRun_StrategyId_CompletedAt",
                table: "OptimizationRun");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "WorkerHealthSnapshot");

            migrationBuilder.DropColumn(
                name: "RolloutLastDiagnosticsJson",
                table: "Strategy");

            migrationBuilder.AlterColumn<string>(
                name: "RolloutLastOutcome",
                table: "Strategy",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RolloutLastFailureMessage",
                table: "Strategy",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CompletionPublicationErrorMessage",
                table: "OptimizationRun",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
