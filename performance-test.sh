#!/bin/bash

# MelonMQ Performance Test Script
# Runs comprehensive performance tests and generates reports

set -e

echo "⚡ MelonMQ Performance Test Suite"
echo "================================="

# Configuration
BROKER_HOST=${1:-localhost}
BROKER_PORT=${2:-5672}
DURATION=${3:-60}  # seconds
CONNECTIONS=${4:-10}
MESSAGE_SIZE=${5:-1024}  # bytes
RESULTS_DIR="performance-results-$(date +%Y%m%d-%H%M%S)"

echo "Broker: ${BROKER_HOST}:${BROKER_PORT}"
echo "Duration: ${DURATION}s"
echo "Connections: ${CONNECTIONS}"
echo "Message Size: ${MESSAGE_SIZE} bytes"
echo "Results: ${RESULTS_DIR}"
echo ""

# Create results directory
mkdir -p "$RESULTS_DIR"

# Check if broker is running
echo "🔍 Checking broker connectivity..."
timeout 5 bash -c "</dev/tcp/${BROKER_HOST}/${BROKER_PORT}" 2>/dev/null
if [ $? -eq 0 ]; then
    echo "✅ Broker is accessible"
else
    echo "❌ Cannot connect to broker at ${BROKER_HOST}:${BROKER_PORT}"
    echo "Make sure MelonMQ broker is running"
    exit 1
fi
echo ""

# Generate test message
TEST_MESSAGE=$(printf 'A%.0s' $(seq 1 $MESSAGE_SIZE))

# Test 1: Publish throughput
echo "🚀 Test 1: Publish Throughput"
echo "============================="

cat > publish_test.sh << 'EOF'
#!/bin/bash
BROKER_HOST=$1
BROKER_PORT=$2
DURATION=$3
MESSAGE_SIZE=$4
CONN_ID=$5
RESULTS_DIR=$6

CONNECTION_STRING="melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}"
TEST_MESSAGE=$(printf 'A%.0s' $(seq 1 $MESSAGE_SIZE))

# Use CLI to publish messages for the duration
END_TIME=$(($(date +%s) + DURATION))
COUNT=0

while [ $(date +%s) -lt $END_TIME ]; do
    melonmq publish \
        --broker "$CONNECTION_STRING" \
        --exchange "perf-test" \
        --routing-key "test.key" \
        --message "$TEST_MESSAGE" \
        --no-persistent >/dev/null 2>&1
    
    COUNT=$((COUNT + 1))
    
    # Report progress every 1000 messages
    if [ $((COUNT % 1000)) -eq 0 ]; then
        echo "Connection $CONN_ID: $COUNT messages sent"
    fi
done

echo "$COUNT" > "${RESULTS_DIR}/publish_${CONN_ID}.result"
EOF

chmod +x publish_test.sh

# Setup test topology
echo "Setting up test topology..."
melonmq declare exchange --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" --name "perf-test" --type direct >/dev/null 2>&1
melonmq declare queue --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" --name "perf-queue" >/dev/null 2>&1
melonmq bind --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" --queue "perf-queue" --exchange "perf-test" --routing-key "test.key" >/dev/null 2>&1

# Run publish test with multiple connections
echo "Running publish test with $CONNECTIONS connections for ${DURATION}s..."
PUBLISH_PIDS=()

for i in $(seq 1 $CONNECTIONS); do
    ./publish_test.sh "$BROKER_HOST" "$BROKER_PORT" "$DURATION" "$MESSAGE_SIZE" "$i" "$RESULTS_DIR" &
    PUBLISH_PIDS+=($!)
done

# Wait for all publish processes
for pid in "${PUBLISH_PIDS[@]}"; do
    wait $pid
done

# Calculate publish results
TOTAL_PUBLISHED=0
for i in $(seq 1 $CONNECTIONS); do
    if [ -f "${RESULTS_DIR}/publish_${i}.result" ]; then
        COUNT=$(cat "${RESULTS_DIR}/publish_${i}.result")
        TOTAL_PUBLISHED=$((TOTAL_PUBLISHED + COUNT))
    fi
done

PUBLISH_RATE=$((TOTAL_PUBLISHED / DURATION))
echo "✅ Publish Rate: ${PUBLISH_RATE} msg/s (${TOTAL_PUBLISHED} total)"
echo ""

# Test 2: Consume throughput
echo "🔽 Test 2: Consume Throughput"
echo "============================="

cat > consume_test.sh << 'EOF'
#!/bin/bash
BROKER_HOST=$1
BROKER_PORT=$2
DURATION=$3
CONN_ID=$4
RESULTS_DIR=$5

CONNECTION_STRING="melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}"

# Use CLI to consume messages for the duration
timeout $DURATION melonmq consume \
    --broker "$CONNECTION_STRING" \
    --queue "perf-queue" \
    --prefetch 100 \
    --timeout $DURATION 2>/dev/null | wc -l > "${RESULTS_DIR}/consume_${CONN_ID}.result"
EOF

chmod +x consume_test.sh

echo "Running consume test with $CONNECTIONS connections for ${DURATION}s..."
CONSUME_PIDS=()

for i in $(seq 1 $CONNECTIONS); do
    ./consume_test.sh "$BROKER_HOST" "$BROKER_PORT" "$DURATION" "$i" "$RESULTS_DIR" &
    CONSUME_PIDS+=($!)
done

# Wait for all consume processes
for pid in "${CONSUME_PIDS[@]}"; do
    wait $pid
done

# Calculate consume results
TOTAL_CONSUMED=0
for i in $(seq 1 $CONNECTIONS); do
    if [ -f "${RESULTS_DIR}/consume_${i}.result" ]; then
        COUNT=$(cat "${RESULTS_DIR}/consume_${i}.result")
        TOTAL_CONSUMED=$((TOTAL_CONSUMED + COUNT))
    fi
done

CONSUME_RATE=$((TOTAL_CONSUMED / DURATION))
echo "✅ Consume Rate: ${CONSUME_RATE} msg/s (${TOTAL_CONSUMED} total)"
echo ""

# Test 3: Round-trip latency
echo "🔄 Test 3: Round-trip Latency"
echo "============================="

# Simple latency test
echo "Running latency test (100 round-trips)..."
LATENCY_FILE="${RESULTS_DIR}/latency.log"

for i in $(seq 1 100); do
    START_TIME=$(date +%s%N)
    
    melonmq publish \
        --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" \
        --exchange "perf-test" \
        --routing-key "test.key" \
        --message "latency-test-$i" >/dev/null 2>&1
    
    # Consume the message
    timeout 5 melonmq consume \
        --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" \
        --queue "perf-queue" \
        --count 1 >/dev/null 2>&1
    
    END_TIME=$(date +%s%N)
    LATENCY=$(((END_TIME - START_TIME) / 1000000))  # Convert to milliseconds
    echo "$LATENCY" >> "$LATENCY_FILE"
done

# Calculate latency statistics
if [ -f "$LATENCY_FILE" ]; then
    MIN_LATENCY=$(sort -n "$LATENCY_FILE" | head -1)
    MAX_LATENCY=$(sort -n "$LATENCY_FILE" | tail -1)
    AVG_LATENCY=$(awk '{sum+=$1} END {print sum/NR}' "$LATENCY_FILE")
    P95_LATENCY=$(sort -n "$LATENCY_FILE" | awk 'NR==int(0.95*NF+0.5){print}')
    P99_LATENCY=$(sort -n "$LATENCY_FILE" | awk 'NR==int(0.99*NF+0.5){print}')
    
    echo "✅ Latency Results:"
    echo "   Min: ${MIN_LATENCY}ms"
    echo "   Avg: ${AVG_LATENCY}ms"
    echo "   Max: ${MAX_LATENCY}ms"
    echo "   P95: ${P95_LATENCY}ms"
    echo "   P99: ${P99_LATENCY}ms"
fi
echo ""

# Test 4: Memory usage
echo "💾 Test 4: Memory Usage"
echo "======================="

# Get broker stats before and after
echo "Getting broker memory statistics..."
STATS_BEFORE="${RESULTS_DIR}/stats_before.json"
STATS_AFTER="${RESULTS_DIR}/stats_after.json"

curl -s "http://${BROKER_HOST}:8080/api/admin/stats" > "$STATS_BEFORE" 2>/dev/null || echo "{}" > "$STATS_BEFORE"

# Load test with many messages
echo "Loading 10,000 messages..."
for i in $(seq 1 10000); do
    melonmq publish \
        --broker "melon://guest:guest@${BROKER_HOST}:${BROKER_PORT}" \
        --exchange "perf-test" \
        --routing-key "test.key" \
        --message "$TEST_MESSAGE" >/dev/null 2>&1
done

curl -s "http://${BROKER_HOST}:8080/api/admin/stats" > "$STATS_AFTER" 2>/dev/null || echo "{}" > "$STATS_AFTER"

# Parse memory usage if available
if command -v jq >/dev/null 2>&1; then
    MEMORY_BEFORE=$(jq -r '.memoryUsed // 0' "$STATS_BEFORE" 2>/dev/null || echo "0")
    MEMORY_AFTER=$(jq -r '.memoryUsed // 0' "$STATS_AFTER" 2>/dev/null || echo "0")
    MEMORY_DIFF=$((MEMORY_AFTER - MEMORY_BEFORE))
    
    echo "✅ Memory Usage:"
    echo "   Before: $((MEMORY_BEFORE / 1024 / 1024))MB"
    echo "   After: $((MEMORY_AFTER / 1024 / 1024))MB"
    echo "   Difference: $((MEMORY_DIFF / 1024 / 1024))MB"
else
    echo "⚠️  jq not available, skipping memory analysis"
fi
echo ""

# Generate performance report
echo "📊 Generating Performance Report"
echo "==============================="

REPORT_FILE="${RESULTS_DIR}/performance_report.md"

cat > "$REPORT_FILE" << EOF
# MelonMQ Performance Test Report

**Test Date:** $(date)
**Broker:** ${BROKER_HOST}:${BROKER_PORT}
**Duration:** ${DURATION}s
**Connections:** ${CONNECTIONS}
**Message Size:** ${MESSAGE_SIZE} bytes

## Results Summary

| Metric | Value |
|--------|-------|
| Publish Rate | ${PUBLISH_RATE} msg/s |
| Consume Rate | ${CONSUME_RATE} msg/s |
| Total Published | ${TOTAL_PUBLISHED} messages |
| Total Consumed | ${TOTAL_CONSUMED} messages |
EOF

if [ -f "$LATENCY_FILE" ]; then
cat >> "$REPORT_FILE" << EOF
| Min Latency | ${MIN_LATENCY}ms |
| Avg Latency | ${AVG_LATENCY}ms |
| Max Latency | ${MAX_LATENCY}ms |
| P95 Latency | ${P95_LATENCY}ms |
| P99 Latency | ${P99_LATENCY}ms |
EOF
fi

cat >> "$REPORT_FILE" << EOF

## Test Configuration

- **Broker Host:** ${BROKER_HOST}
- **Broker Port:** ${BROKER_PORT}
- **Test Duration:** ${DURATION} seconds
- **Concurrent Connections:** ${CONNECTIONS}
- **Message Size:** ${MESSAGE_SIZE} bytes
- **Exchange Type:** Direct
- **Persistence:** Disabled (for maximum throughput)

## Raw Data

- Publish results: publish_*.result
- Consume results: consume_*.result
- Latency measurements: latency.log
- Broker stats: stats_before.json, stats_after.json

## Notes

- Tests were run with persistence disabled for maximum throughput
- Latency tests include both publish and consume operations
- Memory usage is measured via broker admin API
- Results may vary based on hardware and system load
EOF

# Cleanup
rm -f publish_test.sh consume_test.sh

echo "✅ Performance report generated: ${REPORT_FILE}"
echo ""
echo "🎉 Performance Testing Complete!"
echo ""
echo "Results Summary:"
echo "  Publish Rate: ${PUBLISH_RATE} msg/s"
echo "  Consume Rate: ${CONSUME_RATE} msg/s"
if [ -f "$LATENCY_FILE" ]; then
echo "  Avg Latency: ${AVG_LATENCY}ms"
fi
echo ""
echo "Full report: ${RESULTS_DIR}/"
echo "  📄 ${REPORT_FILE}"
echo "  📊 Raw data files"