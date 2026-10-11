#!/bin/sh
# Linux unit proofs (lazer only). Runs the platform-neutral policy proofs, the Linux descriptor-based
# file and diagnostic guards against a real kernel, and the metadata-only stock contract proof.
# Nothing here starts the game, opens a window, touches Realm or player data, or uses the network.
#
#   test.sh [native-root]
#
# native-root (optional) is an extracted osu!lazer AppImage usr/bin directory; when given, the
# Linux stock startup-progress contracts are checked against it as well.
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
NATIVE=${1:-}
OUTPUT="$ROOT/proof-output"
mkdir -p "$OUTPUT"
LOG="$OUTPUT/contract-proof-linux.txt"
: >"$LOG"

run() {
    dotnet "$@" | tee -a "$LOG"
}

for project in endpoint-tests/EndpointPolicy session-tests/SessionRevocation diagnostic-tests/StartupDiagnostics; do
    dotnet restore "$ROOT/$project.csproj" --source "$ROOT/$(dirname "$project")" --ignore-failed-sources
    run run --project "$ROOT/$project.csproj" -c Release --no-restore
done
if [ -n "$NATIVE" ]; then
    run run --project "$ROOT/diagnostic-tests/StartupDiagnostics.csproj" -c Release --no-restore -- --stock-contracts "$NATIVE" linux-x64
fi
dotnet restore "$ROOT/auth-tests/AuthConfigProof.csproj" --source "$ROOT/auth-tests" --ignore-failed-sources
AUTH_ROOT="$OUTPUT/data-transfiguration-proof-auth-$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')"
run run --project "$ROOT/auth-tests/AuthConfigProof.csproj" -c Release --no-restore -- "$AUTH_ROOT"
echo "Proof report: $LOG"
