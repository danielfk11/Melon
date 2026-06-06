#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "${ROOT_DIR}/Directory.Build.props" | head -n 1)"
RID="${MELONMQ_RELEASE_RID:-}"
OUTPUT_ROOT="${MELONMQ_RELEASE_OUTPUT_DIR:-${ROOT_DIR}/artifacts/release}"
PUBLISH_DIR="${OUTPUT_ROOT}/melonmq-${VERSION}"

if [[ -z "${VERSION}" ]]; then
  echo "Could not read <Version> from Directory.Build.props" >&2
  exit 1
fi

rm -rf "${PUBLISH_DIR}"
mkdir -p "${PUBLISH_DIR}/bin" "${PUBLISH_DIR}/ops" "${PUBLISH_DIR}/observability"

publish_args=(
  "${ROOT_DIR}/src/MelonMQ.Broker/MelonMQ.Broker.csproj"
  -c Release
  -o "${PUBLISH_DIR}/bin"
  --no-restore
  --nologo
  -p:NuGetAudit=false
)

if [[ -n "${RID}" ]]; then
  publish_args+=(-r "${RID}" --self-contained false)
fi

dotnet publish "${publish_args[@]}"

cp -R "${ROOT_DIR}/ops/." "${PUBLISH_DIR}/ops/"
cp -R "${ROOT_DIR}/observability/." "${PUBLISH_DIR}/observability/"
cp "${ROOT_DIR}/README.md" "${PUBLISH_DIR}/README.md"
cp "${ROOT_DIR}/CHANGELOG.md" "${PUBLISH_DIR}/CHANGELOG.md"

(
  cd "${OUTPUT_ROOT}"
  tar -czf "melonmq-${VERSION}.tar.gz" "melonmq-${VERSION}"
  shasum -a 256 "melonmq-${VERSION}.tar.gz" > "melonmq-${VERSION}.tar.gz.sha256"
)

echo "Release package created:"
echo "  ${OUTPUT_ROOT}/melonmq-${VERSION}.tar.gz"
echo "  ${OUTPUT_ROOT}/melonmq-${VERSION}.tar.gz.sha256"
