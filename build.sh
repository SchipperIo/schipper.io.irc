#!/usr/bin/env bash
# Build, test, and pack this project inside the latest .NET 10 SDK image.
#
# The project directory is mounted at /workspace. container.sh copies the source to
# /tmp/build inside the container so bin/ and obj/ never land on the host. Packages
# are written to ./dist and copied to the shared feed ../nuget.cache.
# Schipper.* restores use that feed (see nuget.config).
#
# Image: mcr.microsoft.com/dotnet/sdk:10.0  (floating latest .NET 10 SDK)
# RID defaults to linux-x64. Override with: RID=win-x64 ./build.sh
#
# Usage: ./build.sh [-t] [-i] [-p] [-r] [-q] [-o]
#   (no flags)  restore + build (Release)
#   -t          run unit tests
#   -i          run integration tests, if any
#   -p          pack into ./dist and ../nuget.cache
#   -r          run the project, if it is an executable
#   -q          run unit tests under dotnet-trace -> ./dist/trace
#   -o          also capture build/test console output to ./dist/raw
set -euo pipefail

IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${RID:-linux-x64}"
FEED="$(cd "$SCRIPT_DIR/.." && pwd)/nuget.cache"
mkdir -p "$FEED"

do_test=0; do_integ=0; do_pack=0; do_run=0; do_trace=0; do_out=0
while getopts "tiprqo" opt; do
  case "$opt" in
    t) do_test=1 ;;
    i) do_integ=1 ;;
    p) do_pack=1 ;;
    r) do_run=1 ;;
    q) do_trace=1 ;;
    o) do_out=1 ;;
    *) echo "usage: $0 [-t] [-i] [-p] [-r] [-q] [-o]" >&2; exit 2 ;;
  esac
done

# Keep Git Bash (Windows) from rewriting the in-container paths.
export MSYS_NO_PATHCONV=1

docker run --pull always --rm \
  -v "$SCRIPT_DIR:/workspace" \
  -v "$FEED:/tmp/nuget.cache:ro" \
  -e DO_TEST="$do_test" -e DO_INTEG="$do_integ" -e DO_PACK="$do_pack" -e DO_RUN="$do_run" \
  -e DO_TRACE="$do_trace" -e DO_OUT="$do_out" -e RID="$RID" \
  "$IMAGE" bash /workspace/container.sh

if [ "$do_pack" -eq 1 ]; then
  find "$SCRIPT_DIR/dist" -maxdepth 1 -type f \( -name '*.nupkg' -o -name '*.snupkg' \) -exec cp -f {} "$FEED/" \;
  echo "==> copied packages -> $FEED"
fi
