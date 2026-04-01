using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <summary>
    /// Creates a materialized view aggregating TransactionCostAnalysis data by symbol and day.
    /// Refreshed hourly by the TcaAggregateRefreshWorker for dashboard performance.
    /// </summary>
    public partial class AddTcaAggregateView : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE MATERIALIZED VIEW IF NOT EXISTS tca_daily_aggregates AS
                SELECT
                    ""Symbol"",
                    DATE_TRUNC('day', ""AnalyzedAt"") AS ""Period"",
                    COUNT(*)::int AS ""TradeCount"",
                    AVG(""ImplementationShortfall"") AS ""AvgImplementationShortfall"",
                    AVG(""DelayCost"") AS ""AvgDelayCost"",
                    AVG(""MarketImpactCost"") AS ""AvgMarketImpactCost"",
                    AVG(""SpreadCost"") AS ""AvgSpreadCost"",
                    AVG(""TotalCostBps"") AS ""AvgTotalCostBps"",
                    AVG(""SignalToFillMs"")::bigint AS ""AvgSignalToFillMs"",
                    AVG(""SubmissionToFillMs"")::bigint AS ""AvgSubmissionToFillMs"",
                    PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ""TotalCostBps"") AS ""MedianTotalCostBps"",
                    PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY ""TotalCostBps"") AS ""P95TotalCostBps""
                FROM ""TransactionCostAnalyses""
                WHERE ""IsDeleted"" = false
                GROUP BY ""Symbol"", DATE_TRUNC('day', ""AnalyzedAt"")
                WITH DATA;
            ");

            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_tca_daily_agg_symbol_period
                ON tca_daily_aggregates (""Symbol"", ""Period"");
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS tca_daily_aggregates;");
        }
    }
}
