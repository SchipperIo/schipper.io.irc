#!/usr/bin/env bash
# Runs inside mcr.microsoft.com/dotnet/sdk:10.0. Invoked by build.sh and build.ps1.
# The project is mounted at /workspace and copied to /tmp/build so bin/ and obj/ stay
# off the host. ../nuget.cache is mounted at /tmp/nuget.cache for Schipper.* restore.
set -euo pipefail

export HOME=/tmp NUGET_PACKAGES=/tmp/nuget DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$PATH:$HOME/.dotnet/tools"

SRCDIR=/tmp/build
mkdir -p "$SRCDIR"
cp -a /workspace/. "$SRCDIR/"
rm -rf "$SRCDIR/dist" "$SRCDIR/.git"
find "$SRCDIR" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
cd "$SRCDIR"

SRC=$(ls src/*.csproj 2>/dev/null | head -n1)
TEST=$(ls tests/*.csproj 2>/dev/null | head -n1 || true)
[ -n "$SRC" ] || { echo "no src/*.csproj found" >&2; exit 1; }

RAW=/workspace/dist/raw
TRACE=/workspace/dist/trace

run() {
  local name="$1"; shift
  if [ "$DO_OUT" = 1 ]; then
    mkdir -p "$RAW"
    "$@" 2>&1 | tee "$RAW/$name.log"
  else
    "$@"
  fi
}

echo "==> build $(basename "$SRC")"
run build dotnet build "$SRC" -c Release

if [ "$DO_TEST" = 1 ]; then
  if [ -n "$TEST" ]; then
    echo "==> unit tests $(basename "$TEST")"
    run test dotnet test --project "$TEST" -c Release
  else
    echo "==> no unit test project, skipping -t"
  fi
fi

if [ "$DO_INTEG" = 1 ]; then
  INTEG=$(ls tests/*Integration*.csproj integration/*.csproj 2>/dev/null | head -n1 || true)
  if [ -n "$INTEG" ]; then
    echo "==> integration tests $(basename "$INTEG")"
    run integration dotnet test --project "$INTEG" -c Release
  else
    echo "==> no integration tests, skipping -i"
  fi
fi

if [ "$DO_TRACE" = 1 ]; then
  if [ -n "$TEST" ]; then
    NAME=$(basename "$TEST" .csproj)
    echo "==> traced unit tests $NAME -> dist/trace"
    mkdir -p "$TRACE"
    dotnet tool install --global dotnet-trace >/dev/null 2>&1 || true

    dotnet build "$TEST" -c Release
    TESTDLL="$SRCDIR/tests/bin/Release/net10.0/$NAME.dll"
    [ -f "$TESTDLL" ] || TESTDLL=$(find "$SRCDIR/tests/bin" -name "$NAME.dll" -not -path "*/ref/*" | head -n1)

    NETTRACE="$TRACE/$NAME.nettrace"
    run trace dotnet trace collect --output "$NETTRACE" -- dotnet exec "$TESTDLL"

    echo "==> trace report topN -> dist/trace/$NAME.topN.txt"
    dotnet trace report "$NETTRACE" topN -n 50 > "$TRACE/$NAME.topN.txt"
  else
    echo "==> no unit test project, skipping -q"
  fi
fi

if [ "$DO_PACK" = 1 ]; then
  echo "==> pack -> dist/"
  mkdir -p /workspace/dist
  run pack dotnet pack "$SRC" -c Release -o /workspace/dist
fi

if [ "$DO_RUN" = 1 ]; then
  if grep -qiE "<OutputType>[[:space:]]*Exe|<IsExe>[[:space:]]*true" "$SRC"; then
    echo "==> run $(basename "$SRC") (rid=$RID)"
    run run dotnet run --project "$SRC" -c Release -r "$RID"
  else
    echo "==> $(basename "$SRC") is a library (not runnable), skipping -r"
  fi
fi
