#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${ROOT_DIR}/artifacts/nuget"

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "NUGET_API_KEY is required."
  echo "Example: NUGET_API_KEY=*** ./scripts/release-nuget.sh"
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