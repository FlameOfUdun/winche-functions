#!/usr/bin/env bash
# Copies the admin SDK (@winche/admin, the winche-admin-typescript-sdk repository) into the functions runtime, which
# gives it to every deployment and to console pages, and records which commit it came from. Run it after changing the
# SDK, then commit what it changed here.
#
#   scripts/update-admin-sdk.sh [path to winche-admin-typescript-sdk]
#   (default: ../../TypeScript/winche-admin-typescript-sdk from this repository)
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
sdk="$(cd "${1:-$here/../../TypeScript/winche-admin-typescript-sdk}" && pwd)"
cp "$sdk/src/admin.ts" "$here/src/Winche.Functions/Runtime/admin.ts"
commit="$(git -C "$sdk" rev-parse --short HEAD)"
dirty=""; [ -n "$(git -C "$sdk" status --porcelain)" ] && dirty=" (with uncommitted changes)"
version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$sdk/package.json" | head -1)"
cat > "$here/src/Winche.Functions/Runtime/admin-sdk.json" <<JSON
{
  "repository": "winche-admin-typescript-sdk",
  "version": "$version",
  "commit": "$commit$dirty"
}
JSON
echo "Admin SDK $version from winche-admin-typescript-sdk $commit$dirty → src/Winche.Functions/Runtime/admin.ts"
