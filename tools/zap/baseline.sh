#!/usr/bin/env bash
# OWASP ZAP baseline scan against a VeilGate API you are running locally (your own system only).
# It is a PASSIVE scan: it requests pages and reads the responses and headers, and does not attack anything.
# Usage: tools/zap/baseline.sh [target-url]      (default http://host.docker.internal:5080, the dev API)
# The report is written to tools/zap/out/ (ignored by git).
set -euo pipefail

target="${1:-http://host.docker.internal:5080}"
out="$(cd "$(dirname "$0")" && pwd)/out"
mkdir -p "$out"
chmod 777 "$out"   # the container runs as a different user and must be able to write the report

# -I: do not fail the script on warnings; we read the report instead.  -a: also report alpha-quality passive rules.
docker run --rm -v "$out:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:2.16.1 \
  zap-baseline.py -t "$target" -r report.html -w report.md -I -a

echo
echo "Report: $out/report.html"
