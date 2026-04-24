#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

API_BASE_URL="${API_BASE_URL:-http://localhost:5081}"
SMOKE_TIMEOUT_SECONDS="${SMOKE_TIMEOUT_SECONDS:-90}"
SMOKE_SLEEP_SECONDS="${SMOKE_SLEEP_SECONDS:-3}"

compose=(docker compose)
if [[ -n "${DOCKER_COMPOSE:-}" ]]; then
  # Allows callers to pass a wrapper such as: DOCKER_COMPOSE="docker compose --profile dev"
  read -r -a compose <<< "${DOCKER_COMPOSE}"
fi

wait_for_endpoint() {
  local path="$1"
  local deadline
  deadline=$((SECONDS + SMOKE_TIMEOUT_SECONDS))

  until curl -fsS "${API_BASE_URL}${path}" >/dev/null; do
    if (( SECONDS >= deadline )); then
      echo "Smoke check failed: ${API_BASE_URL}${path} did not become healthy within ${SMOKE_TIMEOUT_SECONDS}s" >&2
      return 1
    fi
    sleep "${SMOKE_SLEEP_SECONDS}"
  done

  echo "Smoke check passed: ${API_BASE_URL}${path}"
}

cd "${REPO_ROOT}"
"${compose[@]}" up -d --build "$@"
wait_for_endpoint "/health/live"
wait_for_endpoint "/health/ready"
"${compose[@]}" ps
