#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${ROOT_DIR}/artifacts/nuget"
RELEASE_TAG="${RELEASE_TAG:-}"

if [[ "${1:-}" == "--tag" ]]; then
  RELEASE_TAG="${2:-}"
  shift 2
fi

if [[ -z "${RELEASE_TAG}" ]]; then
  RELEASE_TAG="$(git -C "${ROOT_DIR}" describe --tags --exact-match 2>/dev/null || true)"
fi

if [[ -z "${RELEASE_TAG}" ]]; then
  echo "Release tag is required."
  echo "Use one of:"
  echo "  RELEASE_TAG=v1.0.0-preview.6 NUGET_API_KEY=*** ./scripts/release-nuget.sh"
  echo "  NUGET_API_KEY=*** ./scripts/release-nuget.sh --tag v1.0.0-preview.6"
  exit 1
fi

PROP_VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "${ROOT_DIR}/Directory.Build.props" || true)"
if [[ -z "${PROP_VERSION}" ]]; then
  echo "Could not read <Version> from Directory.Build.props"
  exit 1
fi

TAG_VERSION="${RELEASE_TAG#v}"
echo "Tag version: ${TAG_VERSION}"
echo "Props version: ${PROP_VERSION}"

if [[ "${TAG_VERSION}" != "${PROP_VERSION}" ]]; then
  echo "Tag and project version mismatch."
  echo "Expected tag: v${PROP_VERSION}"
  exit 1
fi

echo "Version validation passed."

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "NUGET_API_KEY is required."
  echo "Example: RELEASE_TAG=v${PROP_VERSION} NUGET_API_KEY=*** ./scripts/release-nuget.sh"
  exit 1
fi

echo "Cleaning output directory: ${OUTPUT_DIR}"
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

echo "Packing projects..."
dotnet pack "${ROOT_DIR}/src/MelonMQ.Protocol/MelonMQ.Protocol.csproj" -c Release -o "${OUTPUT_DIR}" --nologo
dotnet pack "${ROOT_DIR}/src/MelonMQ.Client/MelonMQ.Client.csproj" -c Release -o "${OUTPUT_DIR}" --nologo
dotnet pack "${ROOT_DIR}/src/MelonMQ.Broker/MelonMQ.Broker.csproj" -c Release -o "${OUTPUT_DIR}" --nologo

echo "Pushing packages to NuGet..."
find "${OUTPUT_DIR}" -maxdepth 1 -name '*.nupkg' ! -name '*.symbols.nupkg' -print0 | while IFS= read -r -d '' pkg; do
  echo "-> ${pkg}"
  dotnet nuget push "${pkg}" \
    --api-key "${NUGET_API_KEY}" \
    --source "https://api.nuget.org/v3/index.json" \
    --skip-duplicate
done

echo "Release completed successfully."