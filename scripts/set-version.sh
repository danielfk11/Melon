#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <version>"
  echo "Example: $0 1.3.0"
  exit 1
fi

VERSION="$1"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROPS_FILE="${ROOT_DIR}/Directory.Build.props"

if [[ "${VERSION}" == v* ]]; then
  echo "Use sem prefixo 'v' no Version. Exemplo: 1.3.0"
  exit 1
fi

if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "Formato de versao invalido: ${VERSION}"
  echo "Exemplos validos: 1.3.0, 1.3.1-preview.1"
  exit 1
fi

tmp_file="$(mktemp)"
sed -E "s#<Version>[^<]+</Version>#<Version>${VERSION}</Version>#" "${PROPS_FILE}" > "${tmp_file}"
mv "${tmp_file}" "${PROPS_FILE}"

echo "Version atualizada com sucesso:"
echo "  Directory.Build.props -> ${VERSION}"
echo
echo "Tag de release correspondente:"
echo "  v${VERSION}"
