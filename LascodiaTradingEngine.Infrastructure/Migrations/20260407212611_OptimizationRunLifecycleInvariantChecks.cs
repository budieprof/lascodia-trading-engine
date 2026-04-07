using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizationRunLifecycleInvariantChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "ResultsPersistedAt" = COALESCE(
                    "CompletedAt",
                    "ApprovedAt",
                    "ExecutionStartedAt",
                    "ClaimedAt",
                    "QueuedAt",
                    "StartedAt")
                WHERE "Status" IN ('Completed','Approved','Rejected')
                  AND "ResultsPersistedAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "ApprovalEvaluatedAt" = COALESCE(
                    "ApprovedAt",
                    "CompletedAt",
                    "ResultsPersistedAt",
                    "ExecutionStartedAt",
                    "ClaimedAt",
                    "QueuedAt",
                    "StartedAt")
                WHERE "Status" IN ('Approved','Rejected')
                  AND "ApprovalEvaluatedAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "ValidationFollowUpsCreatedAt" = COALESCE(
                    "ApprovedAt",
                    "CompletedAt",
                    "ResultsPersistedAt",
                    "ExecutionStartedAt",
                    "ClaimedAt",
                    "QueuedAt",
                    "StartedAt")
                WHERE "ValidationFollowUpStatus" IS NOT NULL
                  AND "ValidationFollowUpsCreatedAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "CompletionPublicationStatus" = 'Pending',
                    "CompletionPublicationCompletedAt" = NULL,
                    "CompletionPublicationErrorMessage" = COALESCE(
                        "CompletionPublicationErrorMessage",
                        'Lifecycle invariant repair: republishing required after missing completion payload was detected during migration.')
                WHERE "CompletionPublicationStatus" = 'Published'
                  AND "CompletionPublicationPayloadJson" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "CompletionPublicationPreparedAt" = NULL
                WHERE "CompletionPublicationPreparedAt" IS NOT NULL
                  AND "CompletionPublicationPayloadJson" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "CompletionPublicationPreparedAt" = COALESCE(
                    "CompletionPublicationLastAttemptAt",
                    "ResultsPersistedAt",
                    "ApprovedAt",
                    "CompletedAt",
                    "ExecutionStartedAt",
                    "ClaimedAt",
                    "QueuedAt",
                    "StartedAt")
                WHERE "CompletionPublicationPayloadJson" IS NOT NULL
                  AND "CompletionPublicationPreparedAt" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "OptimizationRun"
                SET "CompletionPublicationCompletedAt" = COALESCE(
                    "CompletionPublicationLastAttemptAt",
                    "CompletionPublicationPreparedAt",
                    "ApprovedAt",
                    "CompletedAt",
                    "ResultsPersistedAt",
                    "ExecutionStartedAt",
                    "ClaimedAt",
                    "QueuedAt",
                    "StartedAt")
                WHERE "CompletionPublicationStatus" = 'Published'
                  AND "CompletionPublicationPayloadJson" IS NOT NULL
                  AND "CompletionPublicationCompletedAt" IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptimizationRun_ApprovalStatesRequireApprovalEvaluated",
                table: "OptimizationRun",
                sql: "\"Status\" NOT IN ('Approved','Rejected') OR \"ApprovalEvaluatedAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptimizationRun_CompletionPreparedRequiresPayload",
                table: "OptimizationRun",
                sql: "\"CompletionPublicationPreparedAt\" IS NULL OR \"CompletionPublicationPayloadJson\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptimizationRun_CompletionPublishedRequiresPreparedPayload",
                table: "OptimizationRun",
                sql: "\"CompletionPublicationStatus\" IS DISTINCT FROM 'Published' OR (\"CompletionPublicationPayloadJson\" IS NOT NULL AND \"CompletionPublicationPreparedAt\" IS NOT NULL AND \"CompletionPublicationCompletedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptimizationRun_FollowUpStatusRequiresCreation",
                table: "OptimizationRun",
                sql: "\"ValidationFollowUpStatus\" IS NULL OR \"ValidationFollowUpsCreatedAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OptimizationRun_TerminalRunsRequireResultsPersisted",
                table: "OptimizationRun",
                sql: "\"Status\" NOT IN ('Completed','Approved','Rejected') OR \"ResultsPersistedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OptimizationRun_ApprovalStatesRequireApprovalEvaluated",
                table: "OptimizationRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OptimizationRun_CompletionPreparedRequiresPayload",
                table: "OptimizationRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OptimizationRun_CompletionPublishedRequiresPreparedPayload",
                table: "OptimizationRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OptimizationRun_FollowUpStatusRequiresCreation",
                table: "OptimizationRun");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OptimizationRun_TerminalRunsRequireResultsPersisted",
                table: "OptimizationRun");
        }
    }
}
