#!/usr/bin/env bash
# Build a single Teamscop_setup.exe (no PowerShell) for Windows.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$ROOT/artifacts/setup-payload"
SETUP_PROJ="$ROOT/agent/Teamscop.Setup"
OUT_DIR="$ROOT/artifacts/setup-exe"
HOME_OUT="${HOME_OUT:-$HOME/Teamscop_setup.exe}"
API_BASE="${TEAMSCOP_API_BASE:-https://teamscop.com}"

# ---------------------------------------------------------------------------
# B2 — the company token key must be a real key, injected here.
#
# Teamscop.Engine.Auth ships an all-zero constant as the development default. It seeds the vault
# master key (SecureVault.DeriveMasterKey) and decrypts offline company tokens, so publishing an
# installer that carries it means every deployment shares a key printed in the source tree. The
# agent refuses to start on an all-zero key outside Development; this refuses to build one.
#
# Sources, in order: $TEAMSCOP_COMPANY_TOKEN_KEY, then CompanyToken__Key in /etc/teamscop/api.env.
# The key MUST be the same one the API uses, so it is read from the server's env rather than
# generated — a fresh random key here would produce an installer whose join tokens never decrypt.
# ---------------------------------------------------------------------------
resolve_company_key() {
  if [[ -n "${TEAMSCOP_COMPANY_TOKEN_KEY:-}" ]]; then
    printf '%s' "$TEAMSCOP_COMPANY_TOKEN_KEY"
    return
  fi
  local env_file=/etc/teamscop/api.env
  if [[ -r "$env_file" ]]; then
    grep -E '^CompanyToken__Key=' "$env_file" | head -1 | cut -d= -f2- || true
  elif [[ -f "$env_file" ]]; then
    sudo grep -E '^CompanyToken__Key=' "$env_file" | head -1 | cut -d= -f2- || true
  fi
}

COMPANY_KEY="$(resolve_company_key | tr -d '"'"'"' \r\n')"

if ! python3 - "$COMPANY_KEY" <<'PY'
import base64, sys
key = sys.argv[1].strip()
if not key:
    sys.exit(1)
try:
    raw = base64.b64decode(key, validate=True)
except Exception:
    sys.exit(1)
sys.exit(0 if len(raw) == 32 and any(raw) else 1)
PY
then
  cat >&2 <<'MSG'
FATAL: no usable company token key.

The installer would ship the all-zero development key, which the staff agent now refuses to run on.

Provide the SAME key the API uses (base64, 32 bytes, not all zero) by either:
  export TEAMSCOP_COMPANY_TOKEN_KEY="$(openssl rand -base64 32)"   # then set CompanyToken__Key
                                                                    # to the same value on the API
or ensure /etc/teamscop/api.env contains:
  CompanyToken__Key=<base64-32-bytes>

Do NOT generate a fresh key here: join tokens minted by the API would never decrypt on the agent.
MSG
  exit 1
fi

echo "==> Publishing win-x64 payload into $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE"

publish() {
  local proj="$1"
  echo "  publish $proj"
  dotnet publish "$ROOT/$proj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$STAGE" \
    -v q
}

publish agent/Teamscop.StaffService/Teamscop.StaffService.csproj
publish agent/Teamscop.SessionHelper/Teamscop.SessionHelper.csproj
publish agent/Teamscop.App/Teamscop.App.csproj
publish agent/Teamscop.UsbApproval/Teamscop.UsbApproval.csproj
publish agent/Teamscop.UninstallGuard/Teamscop.UninstallGuard.csproj

# Strip installer scripts if any leaked into stage
rm -f "$STAGE"/*.ps1 "$STAGE"/README*.txt "$STAGE"/INSTALLER.md 2>/dev/null || true

API_BASE="$API_BASE" COMPANY_KEY="$COMPANY_KEY" STAGE="$STAGE" python3 - <<'PY'
import json, os, pathlib
agent = {
    "ApiBaseUrl": os.environ["API_BASE"],
    "SyncSeconds": 30,
    "ConfigPullSeconds": 180,
    "CompanyTokenKey": os.environ["COMPANY_KEY"],
}
path = pathlib.Path(os.environ["STAGE"], "appsettings.Production.json")
path.write_text(json.dumps({"Agent": agent}, indent=2))
print("Wrote", path, "ApiBaseUrl=", agent["ApiBaseUrl"], "CompanyTokenKey= set")
PY

test -f "$STAGE/Teamscop.App.exe"
test -f "$STAGE/Teamscop.StaffService.exe"
test -f "$STAGE/Teamscop.SessionHelper.exe"
test -f "$STAGE/Teamscop.UsbApproval.exe"
test -f "$STAGE/Teamscop.UninstallGuard.exe"

echo "==> Zipping payload"
rm -f "$SETUP_PROJ/payload.zip"
(cd "$STAGE" && zip -qr "$SETUP_PROJ/payload.zip" .)
ls -lh "$SETUP_PROJ/payload.zip"

echo "==> Publishing Teamscop_setup.exe (single-file)"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"
# Build stamp baked into the exe. Every dialog and the Settings "Installed apps" version carry it,
# because "which build am I actually running?" has cost several debugging rounds against machines
# that were quietly running a stale installer copy. A visible stamp makes that mistake self-evident.
BUILD_STAMP="1.0.0-$(date -u +%Y%m%d.%H%M)"
echo "==> Build stamp: $BUILD_STAMP"
dotnet publish "$SETUP_PROJ/Teamscop.Setup.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:InformationalVersion="$BUILD_STAMP" \
  -o "$OUT_DIR" \
  -v minimal

EXE="$OUT_DIR/Teamscop_setup.exe"
test -f "$EXE"
cp -f "$EXE" "$HOME_OUT"
ls -lh "$EXE" "$HOME_OUT"
echo "SETUP_OK $HOME_OUT"
