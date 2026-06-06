#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BROKER_HTTP_URL="${MELONMQ_BENCHMARK_HTTP_URL:-http://localhost:9090}"
BROKER_TCP_HOST="${MELONMQ_BENCHMARK_TCP_HOST:-localhost}"
BROKER_TCP_PORT="${MELONMQ_BENCHMARK_TCP_PORT:-5672}"
MESSAGES="${MELONMQ_BENCHMARK_MESSAGES:-10000}"
PAYLOAD_BYTES="${MELONMQ_BENCHMARK_PAYLOAD_BYTES:-256}"
QUEUE="${MELONMQ_BENCHMARK_QUEUE:-benchmark-queue}"
OUTPUT_DIR="${MELONMQ_BENCHMARK_OUTPUT_DIR:-${ROOT_DIR}/artifacts/benchmark}"

mkdir -p "${OUTPUT_DIR}"

echo "MelonMQ benchmark profile"
echo "  broker_http_url=${BROKER_HTTP_URL}"
echo "  broker_tcp=${BROKER_TCP_HOST}:${BROKER_TCP_PORT}"
echo "  messages=${MESSAGES}"
echo "  payload_bytes=${PAYLOAD_BYTES}"
echo "  queue=${QUEUE}"
echo "  output_dir=${OUTPUT_DIR}"

if ! curl -fsS "${BROKER_HTTP_URL}/health" >/dev/null; then
  echo "Broker health check failed at ${BROKER_HTTP_URL}/health" >&2
  echo "Start the broker first, then rerun this script." >&2
  exit 1
fi

export MELONMQ_BENCHMARK_URI="melon://${BROKER_TCP_HOST}:${BROKER_TCP_PORT}"
export MELONMQ_BENCHMARK_MESSAGES="${MESSAGES}"
export MELONMQ_BENCHMARK_PAYLOAD_BYTES="${PAYLOAD_BYTES}"
export MELONMQ_BENCHMARK_QUEUE="${QUEUE}"

RESULT_PATH="${OUTPUT_DIR}/benchmark-$(date -u +%Y%m%dT%H%M%SZ).txt"

dotnet run \
  --project "${ROOT_DIR}/tests/MelonMQ.Tests.Performance/MelonMQ.Tests.Performance.csproj" \
  -c Release \
  -- --simple | tee "${RESULT_PATH}"

echo "Benchmark result written to ${RESULT_PATH}"
