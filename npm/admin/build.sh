#!/usr/bin/env bash
# Builds @winche/admin from the SDK's one source (src/Winche.Functions/Runtime/admin.ts, which functions and console
# pages use too): dist/index.js (one ES module) and dist/index.d.ts. Needs Deno and Node.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
src="$here/../../src/Winche.Functions/Runtime/admin.ts"
deno="${DENO:-$(command -v deno || echo "$HOME/.deno/bin/deno")}"
rm -rf "$here/dist" && mkdir -p "$here/dist"
"$deno" bundle --quiet --platform browser "$src" -o "$here/dist/index.js"
tmp="$(mktemp -d)"
npx --yes -p typescript@5.8.3 tsc "$src" --declaration --emitDeclarationOnly --target ES2022 --module ESNext --moduleResolution bundler \
  --lib ES2022,DOM --skipLibCheck --allowImportingTsExtensions --outDir "$tmp"
mv "$tmp/admin.d.ts" "$here/dist/index.d.ts"
rm -rf "$tmp"
echo "built $(wc -c < "$here/dist/index.js") bytes of JS and its types in dist/"
