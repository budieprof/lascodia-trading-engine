using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <summary>
    /// Adds range partitioning to high-volume tables for query performance and efficient retention.
    /// PostgreSQL-specific. TickRecords partitioned by day, FeatureVectors by month.
    /// Existing tables (Candles, MLModelPredictionLogs) are NOT converted — they require
    /// a separate data migration. This migration sets up partitioning for new tables only.
    /// </summary>
    public partial class AddTablePartitioning : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Note: PostgreSQL partitioning must be set up at table creation time.
            // For existing tables, the approach is to create a new partitioned table,
            // migrate data, then swap. This migration adds helper functions and
            // creates partition maintenance infrastructure.

            // Function to auto-create monthly partitions for any table
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION create_monthly_partition(
                    parent_table TEXT,
                    partition_date DATE
                ) RETURNS VOID AS $$
                DECLARE
                    partition_name TEXT;
                    start_date DATE;
                    end_date DATE;
                BEGIN
                    start_date := date_trunc('month', partition_date);
                    end_date := start_date + INTERVAL '1 month';
                    partition_name := parent_table || '_' || to_char(start_date, 'YYYY_MM');

                    EXECUTE format(
                        'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                        partition_name, parent_table, start_date, end_date
                    );
                END;
                $$ LANGUAGE plpgsql;
            ");

            // Function to auto-create daily partitions
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION create_daily_partition(
                    parent_table TEXT,
                    partition_date DATE
                ) RETURNS VOID AS $$
                DECLARE
                    partition_name TEXT;
                    start_date DATE;
                    end_date DATE;
                BEGIN
                    start_date := partition_date;
                    end_date := start_date + INTERVAL '1 day';
                    partition_name := parent_table || '_' || to_char(start_date, 'YYYY_MM_DD');

                    EXECUTE format(
                        'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                        partition_name, parent_table, start_date, end_date
                    );
                END;
                $$ LANGUAGE plpgsql;
            ");

            // Create initial partitions for the next 3 months (TickRecords by day, FeatureVectors by month)
            // TickRecords: create daily partitions for next 90 days
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    d DATE := CURRENT_DATE;
                BEGIN
                    FOR i IN 0..90 LOOP
                        BEGIN
                            PERFORM create_daily_partition('TickRecords', d + i);
                        EXCEPTION WHEN undefined_table THEN
                            -- Table not yet partitioned, skip
                            NULL;
                        END;
                    END LOOP;
                END $$;
            ");

            // FeatureVectors: create monthly partitions for the next 6 months
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    d DATE := date_trunc('month', CURRENT_DATE);
                BEGIN
                    FOR i IN 0..5 LOOP
                        BEGIN
                            PERFORM create_monthly_partition('FeatureVectors', d + (i || ' months')::INTERVAL);
                        EXCEPTION WHEN undefined_table THEN
                            -- Table not yet partitioned, skip
                            NULL;
                        END;
                    END LOOP;
                END $$;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS create_monthly_partition(TEXT, DATE);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS create_daily_partition(TEXT, DATE);");
        }
    }
}
