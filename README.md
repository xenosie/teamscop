# Teamscop

Windows monitoring agent + ASP.NET Core API + Avalonia admin desktop.

**Status matrix:** [`docs/STATUS.md`](docs/STATUS.md) (what is Spec / Engine / API / App / Tests).

## Stack

- Avalonia admin UI: `agent/Teamscop.App`
- Auth engine: `agent/Teamscop.Engine.Auth`
- Lifecycle engine: `agent/Teamscop.Engine.Lifecycle`
- Sync engine: `agent/Teamscop.Engine.Sync`
- Tracking engine: `agent/Teamscop.Engine.Tracking` (time/screenshot/Chrome + secure vault)
- USB engine: `agent/Teamscop.Engine.Usb` (mass-storage block + session TOTP unlock)
- Staff Windows Service: `agent/Teamscop.StaffService`
- Admin host (console): `agent/Teamscop.AdminHost` (TOTP, teams, police CLI)
- USB approval sticker: `agent/Teamscop.UsbApproval`
- Uninstall TOTP guard: `agent/Teamscop.UninstallGuard`
- API: `backend/Teamscop.Api` (.NET 8 + PostgreSQL)
- Deploy: `deploy/`
- Phases: [PHASE1](docs/PHASE1_AUTH.md) · [2](docs/PHASE2_LIFECYCLE.md) · [3](docs/PHASE3_SYNC.md) · [4](docs/PHASE4_TRACKING.md) · [5](docs/PHASE5_BUSINESS_TIME.md) · [6](docs/PHASE6_USB.md) · [7](docs/PHASE7_TEAMS.md) · [8](docs/PHASE8_AUTHORITIES.md) · [9 App history](docs/PHASE9_APP_HISTORY.md)
- UI preview (VPS/CRD): [`docs/UI_DEV_PREVIEW.md`](docs/UI_DEV_PREVIEW.md)

## Auth model

See [`docs/PHASE1_AUTH.md`](docs/PHASE1_AUTH.md).

- Identity = hardware-bound `deviceKey` (not email)
- Admin signup creates a company and returns an offline encrypted `companyToken` (`TS1.…`)
- Staff signup requires that company token; server re-validates it

## Local development

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/ubuntu/Teamscop
dotnet test
dotnet run --project backend/Teamscop.Api
```

Development uses an in-memory database (`appsettings.Development.json`).

## CI / E2E on GitHub

Workflow: [`.github/workflows/e2e.yml`](.github/workflows/e2e.yml)

| Job | Runner | What it covers |
|---|---|---|
| `linux-api-e2e` | ubuntu-latest | Full `dotnet test` (unit + end-to-end API path) |
| `windows-agent-build` | windows-latest | Build Staff/Admin/Uninstall/USB + re-run tests on Windows |
| `live-smoke` | ubuntu-latest | Hits `https://teamscop.com` (manual dispatch or push to `main`) |

```bash
# local equivalent of CI tests
ASPNETCORE_ENVIRONMENT=Development dotnet test -c Release
```

## Production deploy (this VPS)

```bash
sudo bash deploy/install.sh
```

Secrets are written once to `/etc/teamscop/api.env`.

Live endpoints:
- Health: `https://teamscop.com/health`
- Auth API: `https://teamscop.com/api/auth/...`

The Windows agent must use the same `CompanyToken__Key` (base64 32-byte AES key) as the API for offline company-token decrypt.

## API

- `POST /api/auth/admin/signup` (multipart: deviceKey, username, password, avatar?)
- `POST /api/auth/staff/signup` (multipart: deviceKey, username, password, companyToken, avatar?)
- `POST /api/auth/login` (JSON: deviceKey, password)
- `GET /api/auth/me` (Bearer)
- `POST /api/auth/company-token/reveal` (Admin Bearer)
- `POST /api/lifecycle/totp/enroll` (Admin Bearer, `{ staffUserId }`) — per-staff TOTP for USB + uninstall
- `GET /api/lifecycle/totp/staff` (Admin, or `usb_approval` / `uninstall_approval`)
- `GET /api/lifecycle/totp/status/{staffUserId}` (Admin, or approval packages)
- `GET /api/lifecycle/totp/code/{staffUserId}` (Admin, or `usb_approval` / `uninstall_approval`) — current 6-digit code
- `POST /api/lifecycle/uninstall/verify` — staff deviceKey + TOTP → uninstall ticket
- `POST /api/lifecycle/uninstall/consume` — consume ticket during MSI uninstall
- `POST /api/lifecycle/usb/verify` — staff deviceKey + TOTP → USB session ticket
- `POST /api/lifecycle/usb/consume` — consume USB session ticket
- `POST /api/lifecycle/heartbeat` (Bearer)
- `POST /api/ingest/batch` (Bearer) — durable agent event push
- `GET /api/tracking/config/me` (Staff Bearer)
- `PUT /api/tracking/config/{staffUserId}` (Admin Bearer) — quality/period; SignalR push to staff
- Hub: `/hubs/config` — `TrackingConfigUpdated`, `BusinessTimeUpdated`, `OrgStructureUpdated`, `AuthoritiesUpdated`, `PolicemenUpdated` (JWT via `Authorization` or `?access_token=`)
- `GET /api/business-time/me` (Bearer)
- `POST /api/business-time/declare` (Admin Bearer) — absolute company sync clock
- `GET /api/business-time/now` (Bearer)
- `GET /api/org/structure` (Admin or `team_management`) — full teams tree
- `GET /api/org/me` (Bearer) — my team placement
- `POST/PUT/DELETE /api/teams…` (Admin or `team_management`)
- `GET /api/tracking/staff` (Admin / policeman company-wide; team leader → members only)
- `GET /api/tracking/events?staffUserId=` (filtered by authority packages)
- `GET/PUT/DELETE /api/police…` — policemen + authority packages
- `GET /health`
