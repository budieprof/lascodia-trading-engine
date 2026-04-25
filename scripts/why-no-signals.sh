#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

WINDOW_MINUTES="${WINDOW_MINUTES:-60}"
API_BASE_URL="${API_BASE_URL:-http://localhost:5081}"
DB_USER="${DB_USER:-postgres}"
DB_NAME="${DB_NAME:-LascodiaTradingEngineDb}"

usage() {
  cat <<'USAGE'
Usage: scripts/why-no-signals.sh [options]

Explains the current signal path: market data freshness, EA heartbeat, active
strategies, live backtest qualification, recent rejections, and signal output.

Options:
  --window MINUTES       Activity window. Default: 60.
  --api-base-url URL     API base URL. Default: http://localhost:5081.
  -h, --help             Show this help.

Environment overrides:
  WINDOW_MINUTES, API_BASE_URL, DB_USER, DB_NAME
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --window)
      WINDOW_MINUTES="${2:?missing window minutes}"
      shift 2
      ;;
    --api-base-url)
      API_BASE_URL="${2:?missing API base URL}"
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

if ! [[ "$WINDOW_MINUTES" =~ ^[0-9]+$ ]] || [[ "$WINDOW_MINUTES" -lt 1 ]]; then
  echo "WINDOW_MINUTES must be a positive integer." >&2
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

http_status() {
  local path="$1"
  local code
  if code="$(curl -fsS -o /dev/null -w '%{http_code}' "$API_BASE_URL$path" 2>/dev/null)"; then
    printf '%-20s %s\n' "$path" "$code"
  else
    printf '%-20s DOWN\n' "$path"
  fi
}

printf '== API Health ==\n'
http_status "/health/live"
http_status "/health/ready"

psql_engine <<SQL
\pset border 2
\pset null '-'

\echo 'Clock'
select now() at time zone 'utc' as utc_now, ${WINDOW_MINUTES}::int as window_minutes;

\echo 'EA heartbeat'
select
  "InstanceId",
  "Status",
  "EAVersion",
  "ChartSymbol",
  "ChartTimeframe",
  left("Symbols", 80) as symbols,
  "LastHeartbeat",
  age(now(), "LastHeartbeat") as heartbeat_lag,
  "DeregisteredAt"
from "EAInstance"
where "IsDeleted" = false
order by "LastHeartbeat" desc
limit 10;

\echo 'Market data freshness'
select
  'LivePrice' as source,
  count(*) as rows_total,
  count(*) filter (where "Timestamp" >= now() - interval '${WINDOW_MINUTES} minutes') as rows_recent,
  max("Timestamp") as latest_observed_at,
  age(now(), max("Timestamp")) as latest_lag
from "LivePrice"
union all
select
  'TickRecord' as source,
  count(*) as rows_total,
  count(*) filter (where "ReceivedAt" >= now() - interval '${WINDOW_MINUTES} minutes') as rows_recent,
  max("ReceivedAt") as latest_observed_at,
  age(now(), max("ReceivedAt")) as latest_lag
from "TickRecord"
where "IsDeleted" = false;

\echo 'Latest prices by symbol'
select
  "Symbol",
  "Bid",
  "Ask",
  "Timestamp",
  age(now(), "Timestamp") as lag
from "LivePrice"
order by "Timestamp" desc
limit 12;

\echo 'Active strategy coverage'
select
  "Symbol",
  "Timeframe",
  "StrategyType",
  "LifecycleStage",
  count(*) as strategies,
  max("LastSignalAt") as latest_signal_at
from "Strategy"
where "IsDeleted" = false
  and "Status" = 'Active'
group by "Symbol", "Timeframe", "StrategyType", "LifecycleStage"
order by "Symbol", "Timeframe", "StrategyType";

\echo 'Backtest qualification gate'
with cfg as (
  select
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinWinRate' limit 1), 0.60) as min_wr,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinProfitFactor' limit 1), 1.00) as min_pf,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MaxDrawdownPct' limit 1), 0.25) as max_dd,
    coalesce((select "Value"::numeric from "EngineConfig" where "Key" = 'Backtest:Gate:MinSharpe' limit 1), 0.00) as min_sharpe,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MaxAgeDays' limit 1), 180) as max_age_days,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MinTotalTrades' limit 1), 5) as min_trades_default,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MinTotalTrades:M5M15' limit 1), 10) as min_trades_m5m15,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MinTotalTrades:H1' limit 1), 5) as min_trades_h1,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MinTotalTrades:H4' limit 1), 5) as min_trades_h4,
    coalesce((select "Value"::int from "EngineConfig" where "Key" = 'Backtest:Gate:MinTotalTrades:D1' limit 1), 3) as min_trades_d1
),
active_strategies as (
  select "Id", "Name", "StrategyType", "Symbol", "Timeframe", "LifecycleStage"
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
),
evaluated as (
  select
    s.*,
    b."CompletedAt",
    b."TotalTrades",
    b."WinRate",
    b."ProfitFactor",
    b."MaxDrawdownPct",
    b."SharpeRatio",
    case s."Timeframe"
      when 'M1' then cfg.min_trades_m5m15
      when 'M5' then cfg.min_trades_m5m15
      when 'M15' then cfg.min_trades_m5m15
      when 'H1' then cfg.min_trades_h1
      when 'H4' then cfg.min_trades_h4
      when 'D1' then cfg.min_trades_d1
      else cfg.min_trades_default
    end as min_total_trades,
    cfg.*
  from active_strategies s
  cross join cfg
  left join latest_bt b on b."StrategyId" = s."Id"
)
select
  "Id",
  "StrategyType",
  "Symbol",
  "Timeframe",
  "LifecycleStage",
  "CompletedAt" as latest_backtest_completed_at,
  "TotalTrades",
  min_total_trades,
  "WinRate",
  min_wr,
  "ProfitFactor",
  min_pf,
  "MaxDrawdownPct",
  max_dd,
  "SharpeRatio",
  min_sharpe,
  case
    when "CompletedAt" is null then 'no_completed_backtest'
    when "CompletedAt" < now() - make_interval(days => max_age_days) then 'backtest_stale'
    when coalesce("TotalTrades", 0) < min_total_trades then 'not_enough_trades'
    when coalesce("WinRate", 0) < min_wr then 'win_rate_low'
    when coalesce("ProfitFactor", 0) < min_pf then 'profit_factor_low'
    when coalesce("MaxDrawdownPct", 999) > max_dd then 'drawdown_high'
    when coalesce("SharpeRatio", -999) < min_sharpe then 'sharpe_low'
    else 'qualified'
  end as qualification_state
from evaluated
order by "Symbol", "Timeframe", "StrategyType";

\echo 'Recent signal rejections'
select
  "Stage",
  "Reason",
  "Symbol",
  count(*) as rejections,
  max("RejectedAt") as latest_rejected_at,
  left((array_agg(coalesce("Detail", '') order by "RejectedAt" desc))[1], 140) as latest_detail
from "SignalRejectionAudit"
where "RejectedAt" >= now() - interval '${WINDOW_MINUTES} minutes'
group by "Stage", "Reason", "Symbol"
order by rejections desc, latest_rejected_at desc
limit 20;

\echo 'Trade signal output'
select
  "Status",
  count(*) as signals,
  max("GeneratedAt") as latest_generated_at
from "TradeSignal"
where "IsDeleted" = false
  and "GeneratedAt" >= now() - interval '${WINDOW_MINUTES} minutes'
group by "Status"
order by signals desc, "Status";

\echo 'Latest trade signals'
select
  "Id",
  "StrategyId",
  "Symbol",
  "Direction",
  "Confidence",
  "Status",
  "GeneratedAt",
  left(coalesce("RejectionReason", ''), 120) as rejection_reason
from "TradeSignal"
where "IsDeleted" = false
order by "GeneratedAt" desc
limit 10;
SQL
