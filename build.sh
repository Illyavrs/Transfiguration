#!/bin/sh
# Linux x64 counterpart of build.ps1 (lazer only). Compiles the bridge against an extracted
# osu!lazer AppImage (its usr/bin directory) after checking every pinned assembly hash.
#
#   build.sh <native-root>
#
# <native-root> must contain osu.Game.dll, osu.Framework.dll, osu!.dll, Realm.dll and the
# runtime assemblies listed in compatibility.linux-x64.json. Nothing in it is modified.
# Output: artifacts/linux-x64/ (bridge DLL, artifact.json, compatibility.linux-x64.json,
# notices, README and licenses). Requires the .NET 10 SDK, node and sha256sum.
set -eu

[ "$#" -eq 1 ] || { echo "usage: build.sh <native-root>" >&2; exit 2; }
BRIDGE_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
NATIVE=$(CDPATH= cd -- "$1" && pwd)
MANIFEST="$BRIDGE_ROOT/compatibility.linux-x64.json"
[ -f "$MANIFEST" ] || { echo "Missing compatibility.linux-x64.json" >&2; exit 1; }

# One "<file> <sha256>" line per pinned assembly, plus the artifact name.
PINS=$(node -e '
const m = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
if (m.platform !== "linux-x64") throw new Error("not a linux-x64 manifest");
for (const [name, a] of Object.entries(m.assemblies)) console.log(name + " " + a.sha256.toLowerCase());
' "$MANIFEST")
ARTIFACT=$(node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).artifact)' "$MANIFEST")

echo "$PINS" | while IFS=' ' read -r name expected; do
    [ -f "$NATIVE/$name" ] || { echo "Missing tested client assembly: $name" >&2; exit 1; }
    actual=$(sha256sum "$NATIVE/$name" | cut -d' ' -f1)
    [ "$actual" = "$expected" ] || { echo "Untested client assembly: $name. Update the small bridge compatibility gate after its proofs pass." >&2; exit 1; }
done

dotnet build "$BRIDGE_ROOT/Transfiguration.csproj" -c Release -p:Fixture=false "-p:NativeRoot=$NATIVE" --nologo \
    || { echo "Transfiguration compilation failed" >&2; exit 1; }

OUTPUT="$BRIDGE_ROOT/artifacts/linux-x64"
mkdir -p "$OUTPUT/licenses"
DLL="$BRIDGE_ROOT/bin/Release/net10.0/$ARTIFACT"
cp -f "$DLL" "$OUTPUT/$ARTIFACT"
cp -f "$MANIFEST" "$BRIDGE_ROOT/THIRD-PARTY-NOTICES.md" "$BRIDGE_ROOT/README.md" "$OUTPUT/"
cp -f "$BRIDGE_ROOT"/licenses/* "$OUTPUT/licenses/"
HASH=$(sha256sum "$DLL" | cut -d' ' -f1)
node -e '
const fs = require("fs");
const [manifestPath, hash, outPath] = process.argv.slice(1);
const m = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const artifact = {
  protocol: 1, artifact: m.artifact, sha256: hash, default_endpoint: m.default_endpoint,
  endpoint_mode: m.endpoint_mode, configurable_endpoint: m.configurable_endpoint,
  private_auth_filename: m.private_auth_filename, shared_game_preferences: m.shared_game_preferences, fixture: false,
};
fs.writeFileSync(outPath, JSON.stringify(artifact, null, 2) + "\n");
' "$MANIFEST" "$HASH" "$OUTPUT/artifact.json"
echo "Production bridge: $OUTPUT"
echo "SHA256: $HASH"
