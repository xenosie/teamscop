# 09 — Install & Deploy

---

## Windows agent

### Target platform

**Windows 10 and 11, any edition, 64-bit**, on cheap low-end hardware
([02-REQUIREMENTS](02-REQUIREMENTS.md) §15.2).

### The installer

`Teamscop_setup.exe`, built from `agent/Teamscop.Setup`. A single self-contained executable that
installs, uninstalls, and registers itself in Add/Remove Programs. Requires administrator
elevation.

Built with `deploy/windows/build-setup.sh`.

**What an install does:**

1. Create `%ProgramData%\Teamscop\bin` and apply a DACL granting only `SYSTEM` and
   `Administrators`, inheritance disabled
2. Copy the agent binaries
3. Prompt for the company token and enroll the machine
4. Register and start the `TeamscopStaff` Windows service, with recovery configured so it
   restarts if it stops
5. Add **HKCU** `…\CurrentVersion\Run` entries for `Teamscop.SessionHelper.exe` and `Teamscop.App.exe`
6. Write the Add/Remove Programs entry (`TeamscopStaffAgent`) and the
   `HKLM\SOFTWARE\Teamscop\StaffAgent` marker
7. Copy itself aside so uninstall works after the source file is gone

The binaries ship inside the executable as an embedded `payload.zip`, produced by
`build-setup.sh`; a setup built without it fails loudly rather than installing half a product.

**What an uninstall does:**

1. Determine whether this is a staff machine — from the SCM service and the HKLM marker, never
   from a file the monitored user can delete
2. If it is, launch `Teamscop.UninstallGuard.exe` to collect the 6-digit approval code
3. Verify the code (locally, so this works offline)
4. Only on success: stop and delete the service, remove the Run keys, delete
   `%ProgramData%\Teamscop\bin`, and remove the HKLM marker and the ARP entry
5. On failure or cancellation, **remove nothing**

**`%ProgramData%\Teamscop\Agent` is always preserved** — the vault and the outbox survive an
uninstall, so anything captured but not yet uploaded is not destroyed by removing the product.

Lifting the USB block and destroying the offline approval credential happen under
`Teamscop.UninstallGuard.exe --restore-machine`, invoked by the uninstaller once removal is actually
under way — not on code entry, so a correct code followed by a cancelled uninstall leaves the machine
protected.

> The `/cleanup` switch and `FORCE_CLEANUP.txt` are **deleted** (§11.3). Both removed everything with
> no code required, which made the uninstall guard decorative.

### Packaging directory

`deploy/windows/` now contains **one file: `build-setup.sh`.** Every superseded generation —
`Install-Teamscop.ps1`, `Uninstall-Teamscop.ps1`, `install-staff.ps1`, `uninstall-staff.ps1`,
`publish-staff.ps1`, `wix/Teamscop.StaffAgent.wxs`, `README_ONE_INSTALL.txt` — has been removed.

That mattered because those scripts installed to different layouts while registering the **same**
Add/Remove Programs key as `Teamscop_setup.exe`, so which uninstaller ran depended on which installer
ran last. Do not reintroduce a second installer that claims that key.

The SCM contract that used to live here as `ServiceInstallerHints.ps1` is now the C# class
`Teamscop.Engine.Lifecycle.ServiceInstallerHints`, which `Teamscop.StaffService` and
`Teamscop.Setup` both compile against. Stale `.ps1` copies survive only under the gitignored
`artifacts/` tree; they are build leftovers, not sources.

### Code signing

**Not yet purchased.** Without an Authenticode certificate:

- SmartScreen warns on every install, which for self-installing employees is a real adoption
  barrier
- Antivirus is likely to flag the agent — screen capture, reading a browser database, and
  editing USB device policy together look exactly like malware

No antivirus problems have been observed so far, but this has not been tested broadly.

---

## Server

### Provisioning

```bash
sudo bash deploy/install.sh
```

Provisions PostgreSQL, generates secrets into `/etc/teamscop/api.env` (mode 600), publishes the
API to `/opt/teamscop/api`, installs the `teamscop-api` systemd unit and the nginx site, then
attempts certbot.

### Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings__Default` | PostgreSQL connection, or the literal `InMemory` |
| `Jwt__Key` | Signing key, **≥32 characters or the API refuses to start**. Also wraps the TOTP secrets at rest, so rotating it orphans every enrollment |
| `Jwt__Issuer`, `Jwt__Audience` | Token validation |
| `CompanyToken__Key` | Base64 32-byte AES key. **Must match the agent build**, and the API refuses to start if it is empty |
| `Storage__AvatarRoot` | Avatar directory on disk |
| `Storage__PublicAvatarBasePath` | Base path stored in `AvatarUrl`. Defaults to `/api/media/avatars` — an **authenticated API route** since B12, not a static-file path |
| `Storage__ScreenshotRoot` | Screenshot blob directory |
| `Retention__AgentEventsDays` | Retention window, **30** per §13.2. `0` disables pruning |
| `Ingest__MaxBatchEvents`, `__MaxPayloadChars`, `__MaxBatchBytes`, `__MaxRequestBodyBytes` | Ingest bounds. `MaxRequestBodyBytes` must stay **above** `MaxBatchBytes` or an oversized batch dies as a connection reset instead of answering 400 |
| `Database__MigrateOnStartup` | Whether to apply migrations on boot |

> ⚠️ The retention key is **`Retention__AgentEventsDays`**, not `Storage__AgentEventsDays`.
> `RetentionOptions.SectionName` is `"Retention"`, so a `Storage__` spelling binds to nothing and
> silently leaves the default in place. `install.sh` writes the correct key and carries a comment
> saying why.

### nginx

Terminates TLS, proxies to Kestrel on loopback, and applies its own rate-limit zones
(10 r/s burst 20 on `/api/auth/`, 50 r/s burst 100 on `/api/`).

> ⚠️ **Reload nginx from the updated config before trusting B12.** Both configs used to carry a
> `location /media/avatars/` block aliasing the avatar directory as static files, and `install.sh`
> additionally set `Storage__PublicAvatarBasePath=/media/avatars`. nginx matches a location before
> it ever proxies, so that block silently defeated the authenticated route — the photos stayed
> readable by anyone with a URL, cached publicly for 7 days on top. All three are fixed in the
> repo, but **a box running the old nginx config still serves avatars from disk.**

**`/hubs/` must be proxied with WebSocket support:**

```nginx
location /hubs/ {
    proxy_pass http://127.0.0.1:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_read_timeout 3600s;
}
```

Without every one of those directives, SignalR negotiation fails and every push path — tracking config,
business clock, org structure, authorities — silently stops working. There are **two** copies of this
config: the committed `nginx.teamscop.conf` and the inline template written by `install.sh`. Both
currently carry the `/hubs/` block; a change to one that misses the other leaves fresh installs
broken while the checked-in file looks fine.

Kestrel listens on loopback only, and the API trusts `X-Forwarded-For` **only from loopback**, so
this proxy hop is the sole source of client IPs for rate limiting and audit records.

### Retention and backups

- Retention runs hourly as a hosted service inside the API: prunes `agent_events` past the window in
  batches of 5 000 (up to 200 000 rows per run), deletes the matching screenshot blobs, prunes
  `agent_chain_breaks` past the same cutoff, and garbage-collects tickets 7 days past expiry
- `pg_dump` runs on a daily systemd timer

There is no point-in-time recovery. `companies → users → agent_events` cascades on delete, so
removing one company row destroys its entire history irrecoverably between dumps.

### Live endpoints

- `https://teamscop.com/health`
- `https://teamscop.com/api/...`
- `wss://teamscop.com/hubs/config`

> ⚠️ **`/health` is a static literal and does not touch the database.** It returned 200 throughout a
> real outage in which the database credential had been broken. Monitor with
> `POST /api/auth/login` and a bogus credential instead — **401 means healthy**, because that path
> resolves configuration, opens a connection and runs a query.

### Operating the database

**Never point automated tooling at the live `teamscop` role or database.** A subagent running
`ALTER ROLE teamscop WITH PASSWORD …` during reconstruction pass 3 took the API down and did not
restore it. Anything that needs PostgreSQL creates its own throwaway role and database and drops it
afterwards. See [08-SECURITY](08-SECURITY.md).

---

## CI

`.github/workflows/e2e.yml`, four jobs:

| Job | Runner | Covers |
|---|---|---|
| `linux-api-e2e` | ubuntu | `dotnet test Teamscop.sln` with coverage, against an in-process host on the in-memory provider |
| `windows-agent-build` | windows | Builds the engines, agent hosts and the Avalonia app; publishes the staff layout as an installer-input dry run; re-runs the tests |
| `linux-app-build` | ubuntu | Builds `Teamscop.App` and the portable `SessionHelper` |
| `live-smoke` | ubuntu | **Read-only** liveness check against production, on push to `main` or manual dispatch |

`live-smoke` used to sign up a real admin **and** a real staff member on production on every merge.
Since §1.7 guarantees there is no delete path, every merge permanently added a company. It now only
checks `/health` and asserts that `/api/tracking/staff` answers 401 unauthenticated. **Do not add any
request to that job that writes.**

### Local equivalent

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build Teamscop.sln -c Release          # must stay 0 errors AND 0 warnings
ASPNETCORE_ENVIRONMENT=Development dotnet test backend/Teamscop.Api.Tests -c Release
```

Current state: **0 errors, 0 warnings, 157 tests (154 passing, 3 Windows-only skipped).** Warnings are fixed, never suppressed — no
`#pragma`, no `NoWarn`.

Most of the suite runs on the in-memory provider, which enforces no unique index and does not
implement `ExecuteDeleteAsync`. **Seven `[PostgresFact]` tests** cover exactly the machinery that
depends on those — the enrollment race, the team-exclusivity invariants, the retention pruner,
avatar-owner resolution — by replaying the real migration chain into a scratch database.

They need a `teamscop_test` role (or `TEAMSCOP_TEST_PG` pointing elsewhere):

```bash
sudo -u postgres psql -c "CREATE ROLE teamscop_test LOGIN CREATEDB PASSWORD 'teamscop_test'"
```

A deliberately separate role: these tests create and drop databases, so they must never be able to
reach — or have their credential confused with — the one the deployed API authenticates as.

> ⚠️ **CI provisions no PostgreSQL**, so `PostgresFactAttribute` sets `Skip` and those seven do not
> run there — CI reports 103 passed, 7 skipped, and the skip is silent. Schema and migration work
> therefore still has **no automated coverage** ([10-GAP-ANALYSIS](10-GAP-ANALYSIS.md) C1), and it is
> the highest-value remaining test item: adding a `postgres` service to `linux-api-e2e` turns seven
> already-written tests from decorative into real. Until then, migration integrity is verified
> empirically by hand against scratch databases; see [04-DATA-MODEL](04-DATA-MODEL.md).
