#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$REPO_ROOT/ReserveBlockCore/ReserveBlockCore.csproj"
DLL="$REPO_ROOT/ReserveBlockCore/bin/Release/net6.0/ReserveBlockCore.dll"
OUT_DIR="$REPO_ROOT/docs/api"
SWAGGER_JSON="$OUT_DIR/swagger.json"

echo "==> Building project..."
dotnet build "$PROJ" -c Release --nologo -v quiet

echo "==> Restoring tools..."
dotnet tool restore --verbosity quiet 2>/dev/null || true

echo "==> Extracting OpenAPI spec (rest doc group)..."
mkdir -p "$OUT_DIR"
dotnet swagger tofile --output "$SWAGGER_JSON" "$DLL" rest

echo "==> Generating markdown docs..."
python3 "$REPO_ROOT/tools/generate-api-docs.py" "$SWAGGER_JSON" "$OUT_DIR"

echo ""
echo "==> Done. Docs written to docs/api/"
