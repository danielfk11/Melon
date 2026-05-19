#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <backup-archive.tar.gz> <target-data-directory> [--force]"
  exit 1
fi

ARCHIVE_PATH="$1"
TARGET_DIR="$2"
FORCE_RESTORE="${3:-}"

if [[ ! -f "$ARCHIVE_PATH" ]]; then
  echo "Backup archive not found: $ARCHIVE_PATH"
  exit 1
fi

mkdir -p "$TARGET_DIR"

if [[ -n "$(ls -A "$TARGET_DIR" 2>/dev/null || true)" && "$FORCE_RESTORE" != "--force" ]]; then
  echo "Target directory is not empty: $TARGET_DIR"
  echo "Use --force to clear it before restore."
  exit 1
fi

if [[ "$FORCE_RESTORE" == "--force" ]]; then
  find "$TARGET_DIR" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
fi

tar -C "$TARGET_DIR" -xzf "$ARCHIVE_PATH"

echo "Restore completed:"
echo "  archive: $ARCHIVE_PATH"
echo "  target:  $TARGET_DIR"
