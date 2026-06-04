#!/usr/bin/env bash
set -euo pipefail

BROKER_URL="${MELONMQ_BROKER_URL:-http://localhost:9090}"
PROMETHEUS_URL="${MELONMQ_PROMETHEUS_URL:-http://localhost:9091}"
GRAFANA_URL="${MELONMQ_GRAFANA_URL:-http://localhost:3000}"

require_command() {
  local name="$1"
  if ! command -v "${name}" >/dev/null 2>&1; then
    echo "Missing required command: ${name}" >&2
    exit 1
  fi
}

require_command curl

echo "Checking broker metrics at ${BROKER_URL}/metrics"
curl -fsS "${BROKER_URL}/metrics" | grep -q "melonmq_" || {
  echo "Broker metrics endpoint is reachable, but no MelonMQ metrics were found." >&2
  exit 1
}

echo "Checking Prometheus readiness at ${PROMETHEUS_URL}/-/ready"
curl -fsS "${PROMETHEUS_URL}/-/ready" >/dev/null

echo "Waiting for Prometheus to ingest melonmq_queues_total"
for _ in $(seq 1 12); do
  response="$(curl -fsS "${PROMETHEUS_URL}/api/v1/query?query=melonmq_queues_total")"
  if echo "${response}" | grep -q '"status":"success"' && ! echo "${response}" | grep -q '"result":\[\]'; then
    echo "Prometheus query ok"
    break
  fi

  sleep 2
else
  echo "Prometheus did not return melonmq_queues_total after waiting." >&2
  exit 1
fi

echo "Checking Grafana health at ${GRAFANA_URL}/api/health"
curl -fsS "${GRAFANA_URL}/api/health" | grep -q '"database"' || {
  echo "Grafana health endpoint did not return the expected payload." >&2
  exit 1
}

echo "Observability stack is reachable."
