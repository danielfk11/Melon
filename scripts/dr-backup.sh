#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <data-directory> <backup-directory>"
  exit 1
fi

DATA_DIR="$1"
BACKUP_DIR="$2"

if [[ ! -d "$DATA_DIR" ]]; then
  echo "Data directory not found: $DATA_DIR"
  exit 1
fi

mkdir -p "$BACKUP_DIR"

TS="$(date -u +%Y%m%dT%H%M%SZ)"
BASENAME="melonmq-backup-${TS}"
ARCHIVE_PATH="${BACKUP_DIR}/${BASENAME}.tar.gz"
MANIFEST_PATH="${BACKUP_DIR}/${BASENAME}.manifest"

tar -C "$DATA_DIR" -czf "$ARCHIVE_PATH" .

{
  echo "timestamp=${TS}"
  echo "source_data_dir=${DATA_DIR}"
  echo "archive=$(basename "$ARCHIVE_PATH")"
  echo "sha256=$(shasum -a 256 "$ARCHIVE_PATH" | awk '{print $1}')"
} > "$MANIFEST_PATH"

echo "Backup created:"
echo "  archive:  $ARCHIVE_PATH"
echo "  manifest: $MANIFEST_PATH"
