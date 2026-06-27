#!/usr/bin/env bash
# Runs a k6 script against the API using the grafana/k6 Docker image (no host install needed).
# Everything goes through the nginx load balancer on :8080, which fans out to the silo replicas.
# Usage: ./tests/run.sh [smoke.js|consistency.js|load.js|stress.js|auth.js]
#   BASE_URL overrides the target (default http://localhost:8080).
#   Most scripts accept knobs, e.g. ACCOUNTS=20 VUS=30 DURATION=40s ./tests/run.sh consistency.js
set -euo pipefail

SCRIPT="${1:-smoke.js}"
DIR="$(cd "$(dirname "$0")" && pwd)"

exec docker run --rm -i --network host \
  -e BASE_URL="${BASE_URL:-http://localhost:8080}" \
  -e ACCOUNTS="${ACCOUNTS:-}" -e INITIAL="${INITIAL:-}" -e VUS="${VUS:-}" -e DURATION="${DURATION:-}" \
  -e POOL="${POOL:-}" -e PEAK="${PEAK:-}" -e WRITE_RATIO="${WRITE_RATIO:-}" \
  -v "$DIR/k6:/scripts:ro" \
  grafana/k6 run "/scripts/$SCRIPT"
