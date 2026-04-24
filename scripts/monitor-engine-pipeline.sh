#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

INTERVAL_SECONDS="${INTERVAL_SECONDS:-30}"
WINDOW_MINUTES="${WINDOW_MINUTES:-15}"
LOG_SINCE="${LOG_SINCE:-5m}"
API_BASE_URL="${API_BASE_URL:-http://localhost:5081}"
DB_USER="${DB_USER:-postgres}"
DB_NAME="${DB_NAME:-LascodiaTradingEngineDb}"
ONCE=0
NO_CLEAR="${NO_CLEAR:-0}"

usage() {
  cat <<'USAGE'
Usage: scripts/monitor-engine-pipeline.sh [options]

Continuously monitors the local Docker Compose engine stack, data ingestion,
feature generation, ML training, and strategy generation pipeline.

Options:
  --once                 Print one snapshot and exit.
  --interval SECONDS     Loop interval. Default: 30.
  --window MINUTES       DB activity window. Default: 15.
  --log-since DURATION   Docker log lookback. Default: 5m.
  --no-clear             Do not clear the terminal between loop snapshots.
  -h, --help             Show this help.

Environment overrides:
  INTERVAL_SECONDS, WINDOW_MINUTES, LOG_SINCE, API_BASE_URL, DB_USER, DB_NAME
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --once)
      ONCE=1
      shift
      ;;
    --interval)
      INTERVAL_SECONDS="${2:?missing interval seconds}"
      shift 2
      ;;
    --window)
      WINDOW_MINUTES="${2:?missing window minutes}"
      shift 2
      ;;
    --log-since)
      LOG_SINCE="${2:?missing log duration}"
      shift 2
      ;;
    --no-clear)
      NO_CLEAR=1
      shift
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

if ! [[ "$INTERVAL_SECONDS" =~ ^[0-9]+$ ]] || [[ "$INTERVAL_SECONDS" -lt 1 ]]; then
  echo "INTERVAL_SECONDS must be a positive integer." >&2
  exit 2
fi

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

section() {
  printf '\n== %s ==\n' "$1"
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

render_stack_health() {
  section "Stack"
  compose ps api postgres rabbitmq prometheus grafana || true

  section "API Health"
  http_status "/health/live"
  http_status "/health/ready"
}

render_rabbitmq() {
  section "RabbitMQ Queues"
  compose exec -T rabbitmq rabbitmqctl list_queues \
    name messages_ready messages_unacknowledged consumers \
    --formatter json 2>/dev/null || echo "RabbitMQ queue query unavailable."
}

render_database_snapshot() {
  section "Database Pipeline Snapshot (${WINDOW_MINUTES}m window)"
  psql_engine <<SQL
\pset border 2
\pset null '-'
\echo 'Clock'
select now() at time zone 'utc' as utc_now;

\echo 'Data intake and feature store'
with candle_stats as (
  select
    count(*) filter (where "IsDeleted" = false) as candles_total,
    count(*) filter (where "IsDeleted" = false and "IsClosed" = true) as closed_total,
    count(*) filter (
      where "IsDeleted" = false
        and "IsClosed" = true
        and "Timestamp" >= now() - interval '${WINDOW_MINUTES} minutes'
    ) as closed_recent,
    max("Timestamp") filter (where "IsDeleted" = false and "IsClosed" = true) as latest_closed_bar
  from "Candle"
),
feature_stats as (
  select
    count(*) filter (where "IsDeleted" = false) as feature_total,
    count(*) filter (
      where "IsDeleted" = false
        and "BarTimestamp" >= now() - interval '${WINDOW_MINUTES} minutes'
    ) as feature_recent,
    max("BarTimestamp") filter (where "IsDeleted" = false) as latest_feature_bar,
    max("ComputedAt") filter (where "IsDeleted" = false) as latest_feature_computed_at
  from "FeatureVector"
),
recent_missing_features as (
  select count(*) as missing_recent_features
  from "Candle" c
  where c."IsDeleted" = false
    and c."IsClosed" = true
    and c."Timestamp" >= now() - interval '${WINDOW_MINUTES} minutes'
    and not exists (
      select 1
      from "FeatureVector" f
      where f."CandleId" = c."Id"
        and f."IsDeleted" = false
    )
)
select
  c.candles_total,
  c.closed_total,
  c.closed_recent,
  c.latest_closed_bar,
  age(now(), c.latest_closed_bar) as latest_closed_lag,
  f.feature_total,
  f.feature_recent,
  f.latest_feature_bar,
  age(now(), f.latest_feature_bar) as latest_feature_lag,
  f.latest_feature_computed_at,
  m.missing_recent_features
from candle_stats c
cross join feature_stats f
cross join recent_missing_features m;

\echo 'Recent candle watermarks'
select
  "Symbol",
  "Timeframe",
  count(*) as recent_closed,
  max("Timestamp") as latest_closed_bar,
  age(now(), max("Timestamp")) as lag
from "Candle"
where "IsDeleted" = false
  and "IsClosed" = true
  and "Timestamp" >= now() - interval '${WINDOW_MINUTES} minutes'
group by "Symbol", "Timeframe"
order by max("Timestamp") desc, count(*) desc
limit 16;

\echo 'COT and regime freshness'
select
  'COTReport' as source,
  count(*) as total_rows,
  max("ReportDate") as latest_observed_at,
  age(now(), max("ReportDate")) as lag
from "COTReport"
where "IsDeleted" = false
union all
select
  'MarketRegimeSnapshot' as source,
  count(*) as total_rows,
  max("DetectedAt") as latest_observed_at,
  age(now(), max("DetectedAt")) as lag
from "MarketRegimeSnapshot"
where "IsDeleted" = false;

\echo 'ML training status'
select
  "Status",
  count(*) as runs,
  max(coalesce("CompletedAt", "PickedUpAt", "StartedAt")) as latest_activity
from "MLTrainingRun"
where "IsDeleted" = false
group by "Status"
order by runs desc, "Status";

\echo 'Recent/active ML training runs'
select
  "Id",
  "Symbol",
  "Timeframe",
  "Status",
  "TriggerType",
  "AttemptCount",
  "Priority",
  "StartedAt",
  "PickedUpAt",
  "CompletedAt",
  left(coalesce("ErrorMessage", ''), 96) as error
from "MLTrainingRun"
where "IsDeleted" = false
  and (
    "Status" in ('Queued', 'Running')
    or "StartedAt" >= now() - interval '${WINDOW_MINUTES} minutes'
    or "CompletedAt" >= now() - interval '${WINDOW_MINUTES} minutes'
  )
order by coalesce("CompletedAt", "PickedUpAt", "StartedAt") desc
limit 12;

\echo 'Active ML models'
select
  "Symbol",
  "Timeframe",
  count(*) filter (where "IsActive" = true) as active_models,
  count(*) filter (where "Status" = 'Active') as active_status_models,
  max("ActivatedAt") as latest_activation,
  max("TrainedAt") as latest_training
from "MLModel"
where "IsDeleted" = false
group by "Symbol", "Timeframe"
order by "Symbol", "Timeframe";

\echo 'Strategy generation cycle runs'
select
  "Status",
  count(*) as cycles,
  max(coalesce("CompletedAtUtc", "StartedAtUtc")) as latest_activity
from "StrategyGenerationCycleRun"
where "IsDeleted" = false
group by "Status"
order by cycles desc, "Status";

\echo 'Latest strategy generation cycles'
select
  "Id",
  "CycleId",
  "Status",
  "StartedAtUtc",
  "CompletedAtUtc",
  "DurationMs",
  "SymbolsProcessed",
  "CandidatesScreened",
  "CandidatesCreated",
  "ReserveCandidatesCreated",
  "StrategiesPruned",
  left(coalesce("FailureStage", ''), 32) as failure_stage,
  left(coalesce("FailureMessage", ''), 96) as failure
from "StrategyGenerationCycleRun"
where "IsDeleted" = false
order by "StartedAtUtc" desc
limit 8;

\echo 'Strategy artifacts and unresolved failures'
select
  count(*) filter (where "QuarantinedAtUtc" is null) as pending_artifacts,
  count(*) filter (where "NeedsCreationAudit" = true and "QuarantinedAtUtc" is null) as needs_creation_audit,
  count(*) filter (where "NeedsCreatedEvent" = true and "QuarantinedAtUtc" is null) as needs_created_event,
  count(*) filter (where "NeedsAutoPromoteEvent" = true and "QuarantinedAtUtc" is null) as needs_auto_promote_event,
  count(*) filter (where "QuarantinedAtUtc" is not null) as quarantined_artifacts,
  max("LastAttemptAtUtc") as latest_artifact_attempt
from "StrategyGenerationPendingArtifact"
where "IsDeleted" = false;

select
  "FailureStage",
  "FailureReason",
  count(*) as unresolved,
  max("CreatedAtUtc") as latest_failure
from "StrategyGenerationFailure"
where "IsDeleted" = false
  and "ResolvedAtUtc" is null
group by "FailureStage", "FailureReason"
order by unresolved desc, latest_failure desc
limit 8;

\echo 'Strategies and signals'
select
  "LifecycleStage",
  "Status",
  count(*) as strategies,
  max("CreatedAt") as latest_created,
  max("LastSignalAt") as latest_signal_at
from "Strategy"
where "IsDeleted" = false
group by "LifecycleStage", "Status"
order by "LifecycleStage", "Status";

select
  "Status",
  count(*) as signals,
  max("GeneratedAt") as latest_generated
from "TradeSignal"
where "IsDeleted" = false
  and "GeneratedAt" >= now() - interval '${WINDOW_MINUTES} minutes'
group by "Status"
order by signals desc, "Status";

\echo 'Optimization and walk-forward queues'
select
  'OptimizationRun' as queue,
  "Status",
  count(*) as runs,
  max(coalesce("CompletedAt", "ExecutionStartedAt", "ClaimedAt", "QueuedAt", "StartedAt")) as latest_activity
from "OptimizationRun"
where "IsDeleted" = false
group by "Status"
union all
select
  'WalkForwardRun' as queue,
  "Status",
  count(*) as runs,
  max(coalesce("CompletedAt", "ExecutionStartedAt", "ClaimedAt", "QueuedAt", "StartedAt")) as latest_activity
from "WalkForwardRun"
where "IsDeleted" = false
group by "Status"
order by queue, "Status";

\echo 'Focused worker health snapshots'
with latest as (
  select distinct on ("WorkerName")
    "WorkerName",
    "IsRunning",
    "LastSuccessAt",
    "LastErrorAt",
    "ConsecutiveFailures",
    "ErrorsLastHour",
    "SuccessesLastHour",
    "BacklogDepth",
    "LastCycleDurationMs",
    "CapturedAt",
    left(coalesce("LastErrorMessage", ''), 96) as last_error
  from "WorkerHealthSnapshot"
  where "IsDeleted" = false
    and (
      "WorkerName" ilike '%Strategy%'
      or "WorkerName" ilike '%ML%'
      or "WorkerName" ilike '%Feature%'
      or "WorkerName" ilike '%Candle%'
      or "WorkerName" ilike '%COT%'
      or "WorkerName" ilike '%Optimization%'
      or "WorkerName" ilike '%WalkForward%'
    )
  order by "WorkerName", "CapturedAt" desc
)
select
  "WorkerName",
  "IsRunning",
  age(now(), "LastSuccessAt") as success_lag,
  age(now(), "LastErrorAt") as error_lag,
  "ConsecutiveFailures",
  "ErrorsLastHour",
  "SuccessesLastHour",
  "BacklogDepth",
  "LastCycleDurationMs",
  age(now(), "CapturedAt") as captured_lag,
  last_error
from latest
order by
  "ConsecutiveFailures" desc,
  "ErrorsLastHour" desc,
  "WorkerName"
limit 30;
SQL
}

render_recent_logs() {
  section "Recent Pipeline Log Highlights (${LOG_SINCE})"
  local highlights
  highlights="$(
    compose logs --since="$LOG_SINCE" api 2>/dev/null \
      | rg -n --max-columns 280 --max-columns-preview \
        'api-1  \|       (StrategyGenerationWorker:|FeatureStoreBackfillWorker:|COTDataWorker:|CandleAggregationWorker:|WalkForwardWorker:|OptimizationWorker:|StrategyWorker:|MLTrainingWorker|SignalOrderBridgeWorker:|MLPredictionOutcomeWorker:|cycle complete|computed daily attribution|vectorsWritten|claimed [0-9]+ run|completed [0-9]+|manual cycle completed)' \
      | head -n 80 || true
  )"
  if [[ -n "$highlights" ]]; then
    printf '%s\n' "$highlights"
  else
    echo "No focused pipeline highlights in this log window."
  fi

  section "Recent Failure Signatures (${LOG_SINCE})"
  local failures
  failures="$(
    compose logs --since="$LOG_SINCE" api 2>/dev/null \
      | rg -n --max-columns 280 --max-columns-preview \
        'api-1  \| (fail|crit):|Unhandled exception|Npgsql\.PostgresException|DbUpdateException|cycle failed|polling cycle failed|loop error|unexpected error|persistFailed=[1-9]|errors=[1-9]|quarantined|dead-letter' \
      | head -n 80 || true
  )"
  if [[ -n "$failures" ]]; then
    printf '%s\n' "$failures"
  else
    echo "No focused failure signatures in this log window."
  fi
}

render_snapshot() {
  if [[ "$NO_CLEAR" != "1" && "$ONCE" != "1" ]]; then
    clear || true
  fi

  printf 'Lascodia engine pipeline monitor | %s | window=%sm | logs=%s | interval=%ss\n' \
    "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
    "$WINDOW_MINUTES" \
    "$LOG_SINCE" \
    "$INTERVAL_SECONDS"

  render_stack_health
  render_database_snapshot
  render_rabbitmq
  render_recent_logs
}

if [[ "$ONCE" == "1" ]]; then
  render_snapshot
  exit 0
fi

while true; do
  render_snapshot
  sleep "$INTERVAL_SECONDS"
done
