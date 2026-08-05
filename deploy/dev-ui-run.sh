#!/usr/bin/env bash
# Run an Avalonia (or any X11) UI on this VPS display for visual iteration.
# View via Chrome Remote Desktop (already on :21) — or set TEAMSCOP_DISPLAY.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DISPLAY_NUM="${TEAMSCOP_DISPLAY:-:21}"
export DISPLAY="$DISPLAY_NUM"
export XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

APP_PROJECT="${1:-}"
CONFIGURATION="${CONFIGURATION:-Release}"
PREVIEW_DIR="${PREVIEW_DIR:-/tmp/teamscop-ui-preview}"
mkdir -p "$PREVIEW_DIR"

if [[ -z "$APP_PROJECT" ]]; then
  cat <<EOF
Usage: $(basename "$0") <path-to-csproj|dll> [args...]

Examples:
  $(basename "$0") agent/Teamscop.App/Teamscop.App.csproj
  $(basename "$0") /tmp/avalonia-smoke/SmokeApp8/SmokeApp8.csproj

View the window in Chrome Remote Desktop (display $DISPLAY_NUM).
Screenshots land in $PREVIEW_DIR/
EOF
  exit 1
fi

if ! xdpyinfo -display "$DISPLAY" >/dev/null 2>&1; then
  echo "ERROR: cannot open display $DISPLAY"
  echo "Start Chrome Remote Desktop session, or: Xvfb $DISPLAY_NUM -screen 0 1920x1080x24 &"
  exit 2
fi

shift || true
EXTRA_ARGS=("$@")

run_dll() {
  local dll="$1"
  local name
  name="$(basename "$dll" .dll)"
  # Replace previous instance of same app
  pkill -f "$name.dll" 2>/dev/null || true
  sleep 0.3
  echo "Launching $dll on DISPLAY=$DISPLAY"
  nohup dotnet "$dll" "${EXTRA_ARGS[@]}" >"$PREVIEW_DIR/$name.log" 2>&1 &
  echo $! >"$PREVIEW_DIR/$name.pid"
  sleep 2
  if command -v scrot >/dev/null; then
    scrot "$PREVIEW_DIR/$name.png" || true
    echo "Screenshot: $PREVIEW_DIR/$name.png"
  fi
  echo "Log: $PREVIEW_DIR/$name.log  PID=$(cat "$PREVIEW_DIR/$name.pid")"
  echo "Look at Chrome Remote Desktop desktop for the live window."
}

if [[ "$APP_PROJECT" == *.dll ]]; then
  run_dll "$(cd "$(dirname "$APP_PROJECT")" && pwd)/$(basename "$APP_PROJECT")"
  exit 0
fi

if [[ ! -f "$APP_PROJECT" ]]; then
  # allow repo-relative path
  if [[ -f "$ROOT/$APP_PROJECT" ]]; then
    APP_PROJECT="$ROOT/$APP_PROJECT"
  else
    echo "Not found: $APP_PROJECT"
    exit 1
  fi
fi

echo "Building $APP_PROJECT ($CONFIGURATION)…"
dotnet build "$APP_PROJECT" -c "$CONFIGURATION" -v q
DLL="$(dotnet msbuild "$APP_PROJECT" -getProperty:TargetPath -p:Configuration="$CONFIGURATION" 2>/dev/null | tail -1)"
if [[ -z "$DLL" || ! -f "$DLL" ]]; then
  # fallback: conventional path
  PROJ_DIR="$(cd "$(dirname "$APP_PROJECT")" && pwd)"
  NAME="$(basename "$APP_PROJECT" .csproj)"
  DLL="$PROJ_DIR/bin/$CONFIGURATION/net8.0/$NAME.dll"
fi
if [[ ! -f "$DLL" ]]; then
  echo "Could not locate output DLL"
  exit 3
fi
run_dll "$DLL"
