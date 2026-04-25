#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

LOOKBACK_DAYS="${LOOKBACK_DAYS:-30}"
DB_USER="${DB_USER:-postgres}"
DB_NAME="${DB_NAME:-LascodiaTradingEngineDb}"

usage() {
  cat <<'USAGE'
Usage: scripts/strategy-pipeline-quality-report.sh [options]

Prints a strategy-generation quality audit from the local Docker Compose database.

Options:
  --lookback DAYS       History window for failures/cycles. Default: 30.
  -h, --help            Show this help.

Environment overrides:
  LOOKBACK_DAYS, DB_USER, DB_NAME
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --lookback)
      LOOKBACK_DAYS="${2:?missing lookback days}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if ! [[ "$LOOKBACK_DAYS" =~ ^[0-9]+$ ]] || [[ "$LOOKBACK_DAYS" -lt 1 ]]; then
  echo "LOOKBACK_DAYS must be a positive integer." >&2
  exit 2
fi

compose() {
  docker compose "$@"
}

psql_engine() {
  compose exec -T postgres psql \
    -U "$DB_USER" \
    -d "$DB_NAME" \
    -v ON_ERROR_STOP=1 \
    -P pager=off \
    "$@"
}

psql_engine <<SQL
\pset border 2
\pset null '-'

\echo 'Strategy pipeline quality report'
select now() at time zone 'utc' as utc_now, ${LOOKBACK_DAYS}::int as lookback_days;

\echo 'Generation funnel'
select
  count(*) filter (where "Status" = 'Completed') as completed_cycles,
  count(*) filter (where "Status" = 'Failed') as failed_cycles,
  count(*) filter (where "Status" = 'Running') as running_cycles,
  sum("SymbolsProcessed") as symbols_processed,
  sum("SymbolsSkipped") as symbols_skipped,
  sum("CandidatesScreened") as candidates_screened,
  sum("CandidatesCreated") as candidates_created,
  sum("ReserveCandidatesCreated") as reserve_created,
  sum("StrategiesPruned") as strategies_pruned,
  round(
    100.0 * sum("CandidatesCreated")::numeric
    / nullif(sum("CandidatesScreened"), 0),
    2
  ) as candidate_acceptance_pct,
  max(coalesce("CompletedAtUtc", "StartedAtUtc")) as latest_activity
from "StrategyGenerationCycleRun"
where "IsDeleted" = false
  and "StartedAtUtc" >= now() - interval '${LOOKBACK_DAYS} days';

\echo 'Latest cycles'
select
  "CycleId",
  "Status",
  "StartedAtUtc",
  "CompletedAtUtc",
  "DurationMs",
  "SymbolsProcessed",
  "SymbolsSkipped",
  "CandidatesScreened",
  "CandidatesCreated",
  "ReserveCandidatesCreated",
  "PortfolioFilterRemoved",
  left(coalesce("FailureStage", ''), 32) as failure_stage,
  left(coalesce("FailureMessage", ''), 96) as failure
from "StrategyGenerationCycleRun"
where "IsDeleted" = false
order by "StartedAtUtc" desc
limit 10;

\echo 'Failure funnel by gate'
with classified as (
  select
    "StrategyType",
    "Symbol",
    "Timeframe",
    "FailureStage",
    "FailureReason",
    case
      when "FailureStage" in ('ZeroTradesIS', 'ZeroTradesOOS', 'IsThreshold', 'OosThreshold', 'MarginalSharpe', 'EquityCurveR2') then 'underfit'
      when "FailureStage" in ('Degradation', 'MonteCarloShuffle', 'DeflatedSharpe', 'PositionSizingSensitivity', 'WalkForward', 'MonteCarloSignFlip') then 'overfit_or_fragile'
      when "FailureStage" in ('Timeout', 'TaskFault') then 'infrastructure'
      else 'other'
    end as failure_class,
    "CreatedAtUtc"
  from "StrategyGenerationFailure"
  where "IsDeleted" = false
    and "CreatedAtUtc" >= now() - interval '${LOOKBACK_DAYS} days'
)
select
  "FailureStage",
  "FailureReason",
  failure_class,
  count(*) as failures,
  count(distinct "StrategyType") as strategy_types,
  count(distinct "Symbol") as symbols,
  max("CreatedAtUtc") as latest_failure
from classified
group by "FailureStage", "FailureReason", failure_class
order by failures desc, latest_failure desc
limit 20;

\echo 'Near-miss candidates for next-cycle learning'
with failures as (
  select
    *,
    case
      when coalesce("DetailsJson", '') ~ '^\\s*\\{' then "DetailsJson"::jsonb
      else '{}'::jsonb
    end as details
  from "StrategyGenerationFailure"
  where "IsDeleted" = false
    and "CreatedAtUtc" >= now() - interval '${LOOKBACK_DAYS} days'
)
select
  "StrategyType",
  "Symbol",
  "Timeframe",
  "FailureReason",
  count(*) as near_misses,
  round(max(nullif((details ->> 'qualityScore'), '')::numeric), 4) as best_quality_score,
  max("CreatedAtUtc") as latest_near_miss,
  left((array_agg("ParametersJson" order by nullif((details ->> 'qualityScore'), '')::numeric desc nulls last))[1], 160) as best_params
from failures
where coalesce((details ->> 'isNearMiss')::boolean, false) = true
group by "StrategyType", "Symbol", "Timeframe", "FailureReason"
order by best_quality_score desc nulls last, near_misses desc
limit 16;

\echo 'Surrogate observation readiness'
with failures as (
  select
    *,
    case
      when coalesce("DetailsJson", '') ~ '^\\s*\\{' then "DetailsJson"::jsonb
      else '{}'::jsonb
    end as details
  from "StrategyGenerationFailure"
  where "IsDeleted" = false
    and "CreatedAtUtc" >= now() - interval '${LOOKBACK_DAYS} days'
)
select
  "StrategyType",
  "Symbol",
  "Timeframe",
  count(*) filter (where "ParametersJson" <> '') as observations,
  count(*) filter (where coalesce((details ->> 'isNearMiss')::boolean, false)) as near_miss_observations,
  round(avg(nullif((details ->> 'qualityScore'), '')::numeric), 4) as avg_quality_score,
  round(max(nullif((details ->> 'qualityScore'), '')::numeric), 4) as max_quality_score
from failures
where "ParametersJson" <> ''
group by "StrategyType", "Symbol", "Timeframe"
having count(*) >= 3
order by observations desc, max_quality_score desc nulls last
limit 20;

\echo 'Pending promotion/replay artifacts'
select
  count(*) filter (where "QuarantinedAtUtc" is null) as pending_artifacts,
  count(*) filter (where "NeedsCreationAudit" = true and "QuarantinedAtUtc" is null) as needs_creation_audit,
  count(*) filter (where "NeedsCreatedEvent" = true and "QuarantinedAtUtc" is null) as needs_created_event,
  count(*) filter (where "NeedsAutoPromoteEvent" = true and "QuarantinedAtUtc" is null) as needs_auto_promote_event,
  count(*) filter (where "QuarantinedAtUtc" is not null) as quarantined_artifacts,
  max("LastAttemptAtUtc") as latest_attempt
from "StrategyGenerationPendingArtifact"
where "IsDeleted" = false;

\echo 'Active strategy live-qualification snapshot'
with cfg as (
  select
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinWinRate' limit 1), 0.60) as min_wr,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinProfitFactor' limit 1), 1.00) as min_pf,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MaxDrawdownPct' limit 1), 0.25) as max_dd,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinSharpe' limit 1), 0.00) as min_sharpe,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MaxAgeDays' limit 1), 180) as max_age_days
),
active_strategies as (
  select "Id", "Name", "StrategyType", "Symbol", "Timeframe", "Status", "LifecycleStage", "CreatedAt"
  from "Strategy"
  where "IsDeleted" = false
    and "Status" = 'Active'
),
latest_bt as (
  select distinct on ("StrategyId")
    "StrategyId",
    "CompletedAt",
    "TotalTrades",
    "WinRate",
    "ProfitFactor",
    "MaxDrawdownPct",
    "SharpeRatio"
  from "BacktestRun"
  where "IsDeleted" = false
    and "Status" = 'Completed'
    and "CompletedAt" is not null
  order by "StrategyId", "CompletedAt" desc
)
select
  s."Id",
  s."StrategyType",
  s."Symbol",
  s."Timeframe",
  s."LifecycleStage",
  b."CompletedAt" as latest_backtest_completed_at,
  b."TotalTrades",
  b."WinRate",
  b."ProfitFactor",
  b."MaxDrawdownPct",
  b."SharpeRatio",
  case
    when b."StrategyId" is null then 'no_completed_backtest'
    when b."CompletedAt" < now() - make_interval(days => cfg.max_age_days) then 'backtest_stale'
    when coalesce(b."WinRate", 0) < cfg.min_wr then 'win_rate_low'
    when coalesce(b."ProfitFactor", 0) < cfg.min_pf then 'profit_factor_low'
    when coalesce(b."MaxDrawdownPct", 999) > cfg.max_dd then 'drawdown_high'
    when coalesce(b."SharpeRatio", -999) < cfg.min_sharpe then 'sharpe_low'
    else 'qualified'
  end as qualification_state
from active_strategies s
cross join cfg
left join latest_bt b on b."StrategyId" = s."Id"
order by s."Symbol", s."Timeframe", s."StrategyType";
SQL
