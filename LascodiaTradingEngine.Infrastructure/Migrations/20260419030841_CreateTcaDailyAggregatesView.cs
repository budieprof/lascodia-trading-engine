using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateTcaDailyAggregatesView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Daily TCA aggregates. Consumed by dashboard reporting; refreshed
            // hourly by TcaAggregateRefreshWorker via REFRESH MATERIALIZED VIEW
            // CONCURRENTLY, which requires a unique index on the view.
            migrationBuilder.Sql(@"
CREATE MATERIALIZED VIEW IF NOT EXISTS tca_daily_aggregates AS
SELECT
    date_trunc('day', ""AnalyzedAt"")          AS ""Day"",
    ""Symbol""                                 AS ""Symbol"",
    COUNT(*)                                   AS ""OrderCount"",
    SUM(""Quantity"")                          AS ""TotalQuantity"",
    AVG(""TotalCostBps"")                      AS ""AvgTotalCostBps"",
    AVG(""ImplementationShortfall"")           AS ""AvgImplementationShortfall"",
    AVG(""DelayCost"")                         AS ""AvgDelayCost"",
    AVG(""MarketImpactCost"")                  AS ""AvgMarketImpactCost"",
    AVG(""SpreadCost"")                        AS ""AvgSpreadCost"",
    AVG(""CommissionCost"")                    AS ""AvgCommissionCost"",
    SUM(""TotalCost"")                         AS ""TotalCost"",
    AVG(""SignalToFillMs"")                    AS ""AvgSignalToFillMs"",
    AVG(""SubmissionToFillMs"")                AS ""AvgSubmissionToFillMs""
FROM ""TransactionCostAnalysis""
WHERE NOT ""IsDeleted""
GROUP BY date_trunc('day', ""AnalyzedAt""), ""Symbol"";
");

            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX IF NOT EXISTS ""ix_tca_daily_aggregates_day_symbol""
    ON tca_daily_aggregates (""Day"", ""Symbol"");
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS tca_daily_aggregates;");
        }
    }
}
