#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_DIR="${ROOT_DIR}/.observability"
PROMETHEUS_CONFIG="${MELONMQ_PROMETHEUS_CONFIG:-${ROOT_DIR}/observability/prometheus/prometheus.yml}"
PROMETHEUS_LISTEN="${MELONMQ_PROMETHEUS_LISTEN:-127.0.0.1:9091}"
GRAFANA_ADDR="${MELONMQ_GRAFANA_ADDR:-127.0.0.1}"
GRAFANA_PORT="${MELONMQ_GRAFANA_PORT:-3000}"

require_command() {
  local name="$1"
  if ! command -v "${name}" >/dev/null 2>&1; then
    echo "Missing required command: ${name}" >&2
    return 1
  fi
}

find_grafana_command() {
  if command -v grafana-server >/dev/null 2>&1; then
    command -v grafana-server
    return 0
  fi

  if command -v grafana >/dev/null 2>&1; then
    command -v grafana
    return 0
  fi

  return 1
}

find_grafana_home() {
  if [[ -n "${GRAFANA_HOME:-}" && -d "${GRAFANA_HOME}" ]]; then
    echo "${GRAFANA_HOME}"
    return 0
  fi

  if command -v brew >/dev/null 2>&1; then
    local brew_prefix
    brew_prefix="$(brew --prefix grafana 2>/dev/null || true)"
    if [[ -n "${brew_prefix}" && -d "${brew_prefix}/share/grafana" ]]; then
      echo "${brew_prefix}/share/grafana"
      return 0
    fi
  fi

  for candidate in /opt/homebrew/share/grafana /usr/local/share/grafana /usr/share/grafana; do
    if [[ -d "${candidate}" ]]; then
      echo "${candidate}"
      return 0
    fi
  done

  return 1
}

require_command prometheus || {
  echo "Install Prometheus first, then rerun this script." >&2
  echo "macOS: brew install prometheus" >&2
  echo "Linux: install the prometheus package or binary for your distro." >&2
  exit 1
}

GRAFANA_COMMAND="$(find_grafana_command || true)"
if [[ -z "${GRAFANA_COMMAND}" ]]; then
  echo "Missing required command: grafana-server or grafana" >&2
  echo "Install Grafana first, then rerun this script." >&2
  echo "macOS: brew install grafana" >&2
  echo "Linux: install the grafana package or binary for your distro." >&2
  exit 1
fi

mkdir -p \
  "${RUNTIME_DIR}/prometheus-data" \
  "${RUNTIME_DIR}/grafana-data" \
  "${RUNTIME_DIR}/grafana-logs" \
  "${RUNTIME_DIR}/grafana-plugins" \
  "${RUNTIME_DIR}/logs"

export MELONMQ_GRAFANA_DASHBOARDS_PATH="${ROOT_DIR}/observability/grafana/dashboards"
export GF_PATHS_DATA="${RUNTIME_DIR}/grafana-data"
export GF_PATHS_LOGS="${RUNTIME_DIR}/grafana-logs"
export GF_PATHS_PLUGINS="${RUNTIME_DIR}/grafana-plugins"
export GF_PATHS_PROVISIONING="${ROOT_DIR}/observability/grafana/provisioning"
export GF_SERVER_HTTP_ADDR="${GRAFANA_ADDR}"
export GF_SERVER_HTTP_PORT="${GRAFANA_PORT}"
export GF_SECURITY_ADMIN_USER="${GF_SECURITY_ADMIN_USER:-admin}"
export GF_SECURITY_ADMIN_PASSWORD="${GF_SECURITY_ADMIN_PASSWORD:-admin}"
export GF_ANALYTICS_REPORTING_ENABLED=false
export GF_ANALYTICS_CHECK_FOR_UPDATES=false

echo "Starting Prometheus on http://${PROMETHEUS_LISTEN}"
prometheus \
  --config.file="${PROMETHEUS_CONFIG}" \
  --storage.tsdb.path="${RUNTIME_DIR}/prometheus-data" \
  --web.listen-address="${PROMETHEUS_LISTEN}" \
  >"${RUNTIME_DIR}/logs/prometheus.log" 2>&1 &
PROMETHEUS_PID="$!"

GRAFANA_ARGS=()
if [[ "$(basename "${GRAFANA_COMMAND}")" == "grafana" ]]; then
  GRAFANA_ARGS+=(server)
fi

GRAFANA_HOME_PATH="$(find_grafana_home || true)"
if [[ -n "${GRAFANA_HOME_PATH}" ]]; then
  GRAFANA_ARGS+=(--homepath "${GRAFANA_HOME_PATH}")
fi

echo "Starting Grafana on http://${GRAFANA_ADDR}:${GRAFANA_PORT}"
"${GRAFANA_COMMAND}" "${GRAFANA_ARGS[@]}" \
  >"${RUNTIME_DIR}/logs/grafana.log" 2>&1 &
GRAFANA_PID="$!"

cleanup() {
  echo
  echo "Stopping observability services..."
  kill "${PROMETHEUS_PID}" "${GRAFANA_PID}" >/dev/null 2>&1 || true
}
trap cleanup INT TERM EXIT

echo
echo "MelonMQ broker metrics: http://localhost:9090/metrics"
echo "Prometheus:            http://${PROMETHEUS_LISTEN}"
echo "Grafana:               http://${GRAFANA_ADDR}:${GRAFANA_PORT}"
echo "Grafana login:         ${GF_SECURITY_ADMIN_USER}/${GF_SECURITY_ADMIN_PASSWORD}"
echo
echo "Logs:"
echo "  ${RUNTIME_DIR}/logs/prometheus.log"
echo "  ${RUNTIME_DIR}/logs/grafana.log"
echo
echo "Press Ctrl+C to stop."

while kill -0 "${PROMETHEUS_PID}" >/dev/null 2>&1 && kill -0 "${GRAFANA_PID}" >/dev/null 2>&1; do
  sleep 1
done

echo "One observability service exited. Check logs in ${RUNTIME_DIR}/logs." >&2
exit 1
