#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${MELONMQ_HASH_CONFIGURATION:-Debug}"
BROKER_DLL="${ROOT_DIR}/src/MelonMQ.Broker/bin/${CONFIGURATION}/net10.0/MelonMQ.Broker.dll"

if [[ $# -ne 1 || -z "${1}" ]]; then
  echo "Usage: $0 <password>" >&2
  exit 2
fi

if [[ ! -f "${BROKER_DLL}" ]]; then
  dotnet build "${ROOT_DIR}/src/MelonMQ.Broker/MelonMQ.Broker.csproj" \
    -c "${CONFIGURATION}" \
    --no-restore \
    --nologo
fi

dotnet "${BROKER_DLL}" hash-password "$1"
