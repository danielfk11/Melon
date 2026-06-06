#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

required_files=(
  "${ROOT_DIR}/ops/README.md"
  "${ROOT_DIR}/ops/melonmq.env.example"
  "${ROOT_DIR}/ops/systemd/melonmq.service"
  "${ROOT_DIR}/ops/launchd/com.melonmq.broker.plist"
  "${ROOT_DIR}/ops/logrotate/melonmq"
  "${ROOT_DIR}/scripts/dr-backup.sh"
  "${ROOT_DIR}/scripts/dr-restore.sh"
  "${ROOT_DIR}/scripts/dr-validate.sh"
  "${ROOT_DIR}/scripts/release-package.sh"
)

for file in "${required_files[@]}"; do
  if [[ ! -f "${file}" ]]; then
    echo "Missing required operational artifact: ${file}" >&2
    exit 1
  fi
done

bash -n "${ROOT_DIR}/scripts/dr-backup.sh"
bash -n "${ROOT_DIR}/scripts/dr-restore.sh"
bash -n "${ROOT_DIR}/scripts/dr-validate.sh"
bash -n "${ROOT_DIR}/scripts/release-package.sh"
bash -n "${ROOT_DIR}/scripts/benchmark.sh"
bash -n "${ROOT_DIR}/scripts/hash-password.sh"

if command -v plutil >/dev/null 2>&1; then
  plutil -lint "${ROOT_DIR}/ops/launchd/com.melonmq.broker.plist" >/dev/null
fi

if command -v systemd-analyze >/dev/null 2>&1; then
  systemd-analyze verify "${ROOT_DIR}/ops/systemd/melonmq.service" >/dev/null
fi

"${ROOT_DIR}/scripts/dr-validate.sh" >/dev/null

echo "Operational validation passed."
