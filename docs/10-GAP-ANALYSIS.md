# 10 — Gap Analysis

Where the code diverges from [02-REQUIREMENTS](02-REQUIREMENTS.md), and the plan to close it.

Two sources feed this list:

1. **Spec gaps** — the owner stated an intent the code does not implement
2. **Audit findings** — defects found by code review that survive into the current tree

Verified against the tree on 2026-08-07 after reconstruction passes 1–3.
**Build: 0 errors, 0 warnings. Tests: 157 (154 passing, 3 Windows-only skipped).**

---

## A. Spec gaps

| # | Requirement | Status | Notes |
|---|---|---|---|
| A1 | Retention **30 days** (§13.2) | ✅ **DONE** | `AgentEventsDays = 30`; retention job also un-throttled |
| A2 | Idle threshold **3 minutes** (§5.2) | ✅ **DONE** | New `TrackingDefaults.IdleThreshold`, single source |
| A3 | Business clock = **timezone dropdown** (§8.4) | ✅ **DONE** | 10 clock columns → 1 (`BusinessTimeZoneId`); 3 `agent_events` columns dropped; anchor UI replaced by `TimeZoneCatalog` dropdown. Dissolves B11 |
| A4 | USB device **must not appear at all** (§9.2) | ✅ **DONE** | Device-node disable via SetupDi — no drive letter is ever presented |
| A5 | USB grant **until that stick is removed** (§9.5) | ✅ **DONE** | Keyed on device identity, not drive letter; grant covers every node of the device |
| A6 | Approval codes **verify offline** (§9.6, §11.2) | ✅ **DONE** | `GET /api/lifecycle/totp/me/secrets` + local verifier; both purposes, offline |
| A7 | **Delete `/cleanup`** (§11.3) | ✅ **DONE** | Zero occurrences remain outside docs |
| A8 | Service **auto-restarts** (§11.4) | ✅ **DONE** | `sc failure` recovery: 5s / 10s / 30s, 24h reset |
| A9 | **Inline gap markers with exact position** (§12.4) | ✅ **DONE** | Inline markers in all three views, server-authoritative via `/api/tracking/gaps` |
| A10 | **Today's overview** home screen (§14.1) | ✅ **DONE** | Today's overview is the landing route, fed by one aggregate |
| A11 | **Leaderboard** by hours (§14.2) | ✅ **DONE** | Leaderboard by hours, gated on `view_timetrack` |
| A12 | **One adaptive window** (§4.7) | ✅ **DONE** | One adaptive shell; five windows + router deleted |
| A13 | **Login screen**, device key + password (§3.2) | ✅ **DONE** | Login screen — device key + password, key pre-filled |
| A14 | **Pagination** for 20–50 staff (§15.1) | ✅ **DONE** | Row virtualisation + cursor paging + lazy thumbnails |
| A15 | Sticker = **engine health proof** (§14.4) | ✅ **DONE** | Sticker reads `agent-health.json`, falls back to `/api/tracking/health/me` |
| A16 | **No employee lifecycle** (§1.7) | ✅ **DONE** | Endpoints, `SessionVersion`, `IsDisabled`, `session_ver` claim and the per-request `OnTokenValidated` DB round-trip all removed (also closes B16). `password/change` kept for §3.2 |
| A17 | Superseded installers removed (09) | ✅ **DONE** | Only `build-setup.sh` and `ServiceInstallerHints.ps1` remain |

**Also completed in pass 1:** B14 (`sc.exe` fully qualified), B18 (CI live-smoke is read-only),
B3 (vault crash consistency), 4 dead `companies` columns dropped, `AdminHost` retired (closes C4
and B17), build brought to **0 warnings** without suppressions.

### Not gaps — confirmed correct as built

Do not "fix" these. Each was verified against owner intent:

- Device-bound identity; duplicate accounts per person; merged shared-PC data
- Chrome-only browsing; full URLs; no window titles; no application-usage tracking
- "App history" meaning Teamscop's own lifecycle events
- Closing a leader/policeman workspace dropping to the staff sticker
- The self-monitoring ban
- Permanent, reusable company token with no approval step

---

## B. Audit findings

**Closed** — B1 (server-side chain anchoring via `agent_chain_breaks`), B2, B3, B4, B6, B7, B8,
B9, B10, B11, B13, B14, B15, B16, B17, B18, B19.

**Still open:**

| # | Finding | Impact | Severity |
|---|---|---|---|
**All B findings are now closed.** B5 (org fan-out batched) and B12 (avatars behind
`GET /api/media/avatars/{fileName}`) closed in the close-out pass.

> ⚠️ B12 has a **deployment** half that is easy to miss: nginx matched `location /media/avatars/`
> before ever proxying, so the old unauthenticated alias would have silently defeated the
> authenticated route on the deployed box. Both `deploy/nginx.teamscop.conf` and the inline
> template in `deploy/install.sh` had it, and `install.sh` also set
> `Storage__PublicAvatarBasePath` to the old path. All three are fixed — but **the running nginx
> must be reloaded from the updated config**, or production still serves avatars from disk.

## C. Structural issues

| # | Issue |
|---|---|
| ~~C1~~ | **Closed.** CI now runs a PostgreSQL service; the seven `[PostgresFact]` tests execute rather than skip (verified by delta: 154 pass / 3 skip with PostgreSQL, 147 / 10 without). CI also applies the full migration chain and fails on `has-pending-model-changes`. |
| C2 | No agent-side integration tests. `StaffAgentWorker`, `SessionHelper`, `TrackingCoordinator`, `ChromeHistoryWatcher`, the outbox and the pipe have zero coverage — precisely where the reported bugs are |
| C3 | 4 hardcoded `https://teamscop.com` references in `agent/` (25 is the whole-repo count). Correct only for single-tenant SaaS, which is undecided |
| ~~C4~~ | **Closed.** `Teamscop.AdminHost` retired and removed from the solution |
| ~~C5~~ | **Closed.** Engine clients collapsed onto one shared typed-client core |
| ~~C6~~ | **Closed.** `ILogger` throughout plus a structured audit log covering 21 action types |

---

## D. Work plan

Ordered so each stage unblocks the next.

### Stage 1 — Install & uninstall

Nothing else is testable until the agent deploys cleanly.

- A7 delete `/cleanup`; A17 delete superseded installers; A8 service recovery
- B14 fully-qualify `sc.exe`
- Verify a clean install → run → uninstall cycle on a real Windows 10 and 11 machine

### Stage 2 — Tracking engine

The data everything else displays.

- A2 idle threshold; A1 retention
- Verify the full capture path end to end: helper → pipe → vault → outbox → ingest → query
- Confirm per-staff configuration actually reaches the capturing process
- B3 vault crash consistency; B13 review the sequence-rewrite rejection against real traffic
- C2 add agent-side integration tests as this work proceeds

### Stage 3 — USB & approval codes

- A4 block at device level so the stick does not appear
- A5 real device identity, grant until removal
- A6 offline code verification
- Resolve the open question in §10.4 (UTC vs company-timezone codes)

### Stage 4 — Desktop app

The largest block, and where the one-window refactor pays for itself.

- A12 collapse five windows into one adaptive window — do this first; A13, A10 and A11 all land inside it
- A13 login screen; A10 Today's overview; A11 leaderboard
- A9 inline data-gap markers; A15 sticker as health proof
- A14 pagination; A3 timezone dropdown
- A16 remove the unwanted lifecycle endpoints and their UI

### Stage 5 — Server hardening

- B1 server-side chain anchoring — this is what makes §12.1–12.2 true rather than aspirational
- B2 inject a real company-token key at build time
- B4 finish the screenshot storage migration
- B5–B12 correctness and performance
- C1 PostgreSQL test fixture; C6 audit logging

### Stage 6 — Release

- Code-signing certificate and signed installer
- Antivirus testing on clean machines
- B18 point CI at staging; B19 warning-clean build
- Resolve the deferred decisions in [01-PRODUCT](01-PRODUCT.md): legal/consent model and hosting

---

## E. Open questions

| # | Question |
|---|---|
| E1 | TOTP on UTC or company timezone? (§10.4) Recommendation: UTC internally, company time for display |
| E2 | Legal and consent model — disclosed or covert, which jurisdictions, GDPR obligations |
| E3 | Hosting model — SaaS or self-hosted. Determines whether C3 is a defect |
| E4 | Licensing, pricing, seat enforcement |
| ~~E5~~ | **ACCEPTED 2026-08-07.** Company-local timestamps are computed at read time from the company's *current* timezone. Changing the timezone retroactively shifts how historical data reads. Accepted as the simpler model; removes the stored wall clock (B11) |

---

## F. Reconstruction log

### Pass 1 — spec alignment (2026-08-07)

Design across five domains, then sequential implementation with disjoint file ownership.

**Closed:** A1, A2, A3, A7, A8, A16, A17, B3, B14, B16, B17, B18, C4.
**Result:** 0 errors, 0 warnings (from 37), 58 tests (from 54).

**Migration integrity — verified empirically, not by inspection.** A real PostgreSQL 16 instance
was used to (1) confirm no pending model changes, (2) diff the migration-built schema against the
model-built schema — 80 columns and 30 indexes identical, (3) replay the upgrade from the last
committed migration against seeded rows using the *old* columns, and (4) confirm zero model
elements lacking migration backing. This is the check the in-memory test suite cannot perform.

**One regression introduced and fixed within the pass.** Narrowing the SignalR company group to
management (originally recommended to stop the org chart leaking to every employee) also cut
plain staff agents off from `BusinessTimeUpdated`, which every agent subscribes to. Fixed by
splitting into two groups — `company:{id}` for everyone, `company:{id}:mgmt` for privileged
payloads — and guarded by three new tests in `ConfigHubGroupTests`.

**Migration squash: attempted, then correctly abandoned.** The add-then-drop churn between
`20260807010000_AddUserSessionVersion` and `20260808000000_Collapse…` looked like uncommitted
debt worth squashing. It is not — querying `__EFMigrationsHistory` on the live database showed
three of the four are **already applied in production**:

```
20260806194000_AddAgentEventUserOccurredIndexes   applied
20260807010000_AddUserSessionVersion              applied
20260807010100_WidenTotpSecretColumn              applied
20260808000000_CollapseBusinessClock…             NOT applied
```

A squashed migration would have been seen as unapplied and tried to re-add columns that already
exist, failing on startup. **Deployed migrations are permanent history and must never be
rewritten.** The chain stays as-is.

Instead, the *undeployed* migration was verified against a clone of the production database:
applies cleanly, `companies` clock columns 11 → 1, `SessionVersion`/`IsDisabled` dropped, no
errors. Production currently holds 0 users and 0 events, so no data is at risk either way.

**Remaining debt from this pass:**

- CI never applies a migration, so none of this schema work has automated coverage (C1).
- `20260806194000` onward have no `.Designer.cs`, unlike the first eight. Harmless at runtime —
  EF only needs `AppDbContextModelSnapshot.cs`, verified in sync — but inconsistent.
- **Rule going forward:** before squashing or editing any migration, check
  `__EFMigrationsHistory` on every environment it may have reached.

### Pass 2 — desktop app (2026-08-07)

One adaptive shell replacing five windows, a composition root, and the missing screens.
**Net −3,742 lines in the app.** Closed A9–A15.

Three defects the reviewers caught and that were fixed within the pass:

- **Every monitored employee saw the workspace.** `desktop.MainWindow = shell` makes Avalonia show
  it automatically, so a workspace window popped on every staff machine at login and only
  disappeared after a network round-trip — i.e. never, on an offline machine.
- **Start-up still blocked**, just moved: the shell constructor derived the device key for the
  login field, spawning several hardware queries before the first frame.
- **The gallery invented gaps** from elapsed time alone, so a lunch break or an overnight shutdown
  rendered as "Data missing". Gaps are now server-authoritative.

### Pass 3 — backend, USB, offline codes (2026-08-07)

Closed A4, A5, A6, B1–B19 (except B5, B12), C4, C5, C6. **Tests 58 → 97.**

**Migration integrity verified empirically**: the full chain replayed into a scratch database and
diffed against the model-derived DDL — 10 tables, 85 columns, 32 indexes, 24 constraints, all
diffs empty. No committed migration was edited; new schema is additions only.

**A production incident occurred.** A subagent setting up the PostgreSQL test fixture ran
`ALTER ROLE teamscop WITH PASSWORD 'teamscop'` against the **live** role, breaking the running API,
then failed to restore it. Root cause was the instruction, not just the agent: it was told "a real
PostgreSQL is running locally" with no ring-fence around the production role. Resolved by rotating
to a fresh credential and rebuilding the database (which held 0 users and 0 events).

**Rule for anyone automating against this box:** provision a dedicated throwaway role and database
for tests. Never point tooling at `teamscop`. And note that `GET /health` returns a static OK with
no database round-trip — it read 200 throughout the outage. `POST /api/auth/login` (401 = healthy)
is the real liveness check.

**Findings fixed after the pass, from the adversarial review:**

| Sev | Finding |
|---|---|
| Critical | The agent pulled offline approval secrets from a route that did not exist, so every USB approval and every uninstall would have failed closed forever. Endpoint built, 5 contract tests added |
| High | A team leader granted `usb_approval` had their team-scoped inherent views silently promoted company-wide. Split into `CanViewCompanyStaff` (roster) and `CanViewCompanyData` (granted view packages) |
| High | The uninstall guard lifted the USB block and destroyed the offline credential on code entry, decoupled from the uninstall happening. Moved behind `--restore-machine`, invoked by the uninstaller |
| High | Today's overview measured coverage from midnight, so every employee read "data missing" every morning. Now measured between first and last reported segment, with a separate signal for a machine that reported nothing at all |
| Medium | `ObjectDisposedException` derives from `InvalidOperationException` and was surfacing as HTTP 400; dead sessions returned 403 so the desktop shell could never return to login. Added `SessionInvalidException` → 401 and an explicit 500 arm |
| Medium | A multi-LUN device left sibling nodes ungated, and `IDeviceGate` failures were discarded so an ungated device reported as blocked |
