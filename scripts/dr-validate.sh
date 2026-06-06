#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/melonmq-dr-validate.XXXXXX")"

cleanup() {
  rm -rf "${WORK_DIR}"
}
trap cleanup EXIT

SOURCE_DIR="${WORK_DIR}/data"
BACKUP_DIR="${WORK_DIR}/backup"
RESTORE_DIR="${WORK_DIR}/restore"

mkdir -p "${SOURCE_DIR}" "${BACKUP_DIR}" "${RESTORE_DIR}"

printf 'queue-entry-1\nqueue-entry-2\n' > "${SOURCE_DIR}/orders.log"
mkdir -p "${SOURCE_DIR}/streams"
printf '{"offset":1}\n' > "${SOURCE_DIR}/streams/audit.events.log"

"${ROOT_DIR}/scripts/dr-backup.sh" "${SOURCE_DIR}" "${BACKUP_DIR}" >/dev/null

ARCHIVE_PATH="$(find "${BACKUP_DIR}" -maxdepth 1 -name 'melonmq-backup-*.tar.gz' | sort | tail -n 1)"
MANIFEST_PATH="${ARCHIVE_PATH%.tar.gz}.manifest"

if [[ ! -f "${ARCHIVE_PATH}" || ! -f "${MANIFEST_PATH}" ]]; then
  echo "Backup archive or manifest was not created." >&2
  exit 1
fi

EXPECTED_SHA="$(awk -F= '/^sha256=/{print $2}' "${MANIFEST_PATH}")"
ACTUAL_SHA="$(shasum -a 256 "${ARCHIVE_PATH}" | awk '{print $1}')"

if [[ "${EXPECTED_SHA}" != "${ACTUAL_SHA}" ]]; then
  echo "Backup checksum mismatch." >&2
  exit 1
fi

"${ROOT_DIR}/scripts/dr-restore.sh" "${ARCHIVE_PATH}" "${RESTORE_DIR}" --force >/dev/null

diff -r "${SOURCE_DIR}" "${RESTORE_DIR}" >/dev/null

echo "DR validation passed."
echo "  archive=${ARCHIVE_PATH}"
echo "  manifest=${MANIFEST_PATH}"
