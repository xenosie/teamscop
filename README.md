# Teamscop

Windows agent + ASP.NET Core API. This repository currently ships the **Auth Engine**.

## Stack

- Auth engine: `agent/Teamscop.Engine.Auth`
- Lifecycle engine: `agent/Teamscop.Engine.Lifecycle`
- Sync engine: `agent/Teamscop.Engine.Sync`
- Tracking engine: `agent/Teamscop.Engine.Tracking` (time/screenshot/Chrome + secure vault)
- Staff Windows Service: `agent/Teamscop.StaffService`
- Admin host: `agent/Teamscop.AdminHost`
- Uninstall TOTP guard: `agent/Teamscop.UninstallGuard`
- API: `backend/Teamscop.Api` (.NET 8 + PostgreSQL)
- Deploy: `deploy/`
- Phase-2 policy: [`docs/PHASE2_LIFECYCLE.md`](docs/PHASE2_LIFECYCLE.md)
- Phase-3 sync: [`docs/PHASE3_SYNC.md`](docs/PHASE3_SYNC.md)
- Phase-4 tracking: [`docs/PHASE4_TRACKING.md`](docs/PHASE4_TRACKING.md)

## Auth model

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
| `windows-agent-build` | windows-latest | Build Staff/Admin/Uninstall + re-run tests on Windows |
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
- `POST /api/lifecycle/totp/enroll` (Admin Bearer) — returns 6-digit TOTP secret / otpauth URI
- `GET /api/lifecycle/totp/status` (Admin Bearer)
- `POST /api/lifecycle/uninstall/verify` — staff deviceKey + TOTP → uninstall ticket
- `POST /api/lifecycle/uninstall/consume` — consume ticket during MSI uninstall
- `POST /api/lifecycle/heartbeat` (Bearer)
- `POST /api/ingest/batch` (Bearer) — durable agent event push
- `GET /api/tracking/config/me` (Staff Bearer)
- `PUT /api/tracking/config/{staffUserId}` (Admin Bearer) — quality/period; SignalR push to staff
- Hub: `/hubs/config` — `TrackingConfigUpdated`
- `GET /health`
