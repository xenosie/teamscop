# Teamscop

Windows employee-monitoring product for small companies: a Windows agent that records work
hours, screenshots and browsing history; an ASP.NET Core API; and an Avalonia desktop console
for the people who review that data.

**Documentation is in [`docs/`](docs/). Start with [01-PRODUCT](docs/01-PRODUCT.md).**

---

## Documentation map

| Doc | What it covers |
|---|---|
| [01-PRODUCT](docs/01-PRODUCT.md) | What Teamscop is, who uses it, core concepts, glossary |
| [02-REQUIREMENTS](docs/02-REQUIREMENTS.md) | **Source of truth.** Owner-confirmed decisions. Where code disagrees, code is wrong |
| [03-ARCHITECTURE](docs/03-ARCHITECTURE.md) | Process topology, projects, data flow, end-to-end paths |
| [04-DATA-MODEL](docs/04-DATA-MODEL.md) | Entities, storage layout, indexes, retention |
| [05-API](docs/05-API.md) | HTTP endpoint and SignalR reference |
| [06-AGENT](docs/06-AGENT.md) | Windows agent internals: capture, vault, sync, USB, uninstall |
| [07-DESKTOP-APP](docs/07-DESKTOP-APP.md) | Desktop UI specification and screen-by-screen behaviour |
| [08-SECURITY](docs/08-SECURITY.md) | Threat model, credentials, what is and isn't defended |
| [09-INSTALL-DEPLOY](docs/09-INSTALL-DEPLOY.md) | Windows installer, server deployment, CI |
| [10-GAP-ANALYSIS](docs/10-GAP-ANALYSIS.md) | Where the code diverges from the spec, and the work plan |

**02-REQUIREMENTS is the source of truth**; where the code disagrees with it, the code is wrong.
Every other doc describes **the code as it actually is**, and flags remaining divergence with a ⚠️
callout. [10-GAP-ANALYSIS](docs/10-GAP-ANALYSIS.md) is the consolidated list of what is still open,
plus the reconstruction log.

Current state: **0 errors, 0 warnings, 157 tests (154 passing, 3 Windows-only skipped), model in sync with migrations.**

---

## Repository layout

```
backend/
  Teamscop.Api/            ASP.NET Core 8 minimal API + PostgreSQL + SignalR hub
  Teamscop.Api.Tests/      xunit suite (also covers the agent engine libraries)
agent/
  Teamscop.Engine.Auth/        device key, company token codec, auth client
  Teamscop.Engine.Lifecycle/   roles, authority packages, TOTP, local state
  Teamscop.Engine.Sync/        outbox queue, batch upload, connectivity
  Teamscop.Engine.Tracking/    time track, screen capture, Chrome, vault, pipe
  Teamscop.Engine.Usb/         removable-storage policy and approval flow
  Teamscop.StaffService/       Windows Service (LocalSystem) — the always-on agent
  Teamscop.SessionHelper/      per-session helper; the only process that can capture the screen
  Teamscop.App/                Avalonia desktop app (admin / leader / policeman / staff sticker)
  Teamscop.UsbApproval/        USB approval sticker
  Teamscop.UninstallGuard/     uninstall approval sticker
  Teamscop.Setup/              Teamscop_setup.exe — single-file installer/uninstaller
deploy/
  install.sh                   Linux/VPS provisioning for the API
  nginx.teamscop.conf          reverse proxy config
  teamscop-api.service         systemd unit
  pg-backup.sh                 daily pg_dump, driven by a systemd timer
  windows/build-setup.sh       builds Teamscop_setup.exe — the only file left in deploy/windows
```

## Local development

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/ubuntu/Teamscop

dotnet build Teamscop.sln -c Release          # must stay 0 errors AND 0 warnings
ASPNETCORE_ENVIRONMENT=Development dotnet test backend/Teamscop.Api.Tests -c Release
dotnet run --project backend/Teamscop.Api      # API on http://localhost:5080
```

Warnings are fixed, never suppressed — no `#pragma`, no `NoWarn`.

Development uses an in-memory database (`"Default": "InMemory"` in
`appsettings.Development.json`). Be aware this hides persistence bugs — unique indexes are not
enforced and `ExecuteDeleteAsync` is unsupported. Seven `[PostgresFact]` tests cover that machinery
against real PostgreSQL and skip **silently** when none is reachable; see
[09-INSTALL-DEPLOY](docs/09-INSTALL-DEPLOY.md) for how to provision the `teamscop_test` role.

**Never point tooling at the live `teamscop` role or database.** Doing so has already taken
production down once. Create a throwaway role and database instead, and note that `GET /health` is a
static literal that does not touch the database — it is useless as a liveness check. See
[08-SECURITY](docs/08-SECURITY.md).

**Never edit or delete an existing migration.** New schema is a new migration plus an updated
`AppDbContextModelSnapshot.cs`; several migrations are already applied in production and are
permanent history. See [04-DATA-MODEL](docs/04-DATA-MODEL.md).

## Production

The API runs on a VPS at `https://teamscop.com` behind nginx, deployed with
`sudo bash deploy/install.sh`. Secrets live in `/etc/teamscop/api.env`.
See [09-INSTALL-DEPLOY](docs/09-INSTALL-DEPLOY.md).
