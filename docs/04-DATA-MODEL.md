# 04 — Data Model

PostgreSQL in production; an in-memory provider in Development. Schema is created by EF Core
migrations, applied at startup when `Database:MigrateOnStartup` is enabled (default `true`).

Verified against `backend/Teamscop.Api/Data/Entities.cs`, `Data/AppDbContext.cs`,
`Data/Migrations/*` and `AppDbContextModelSnapshot.cs` on 2026-08-07, after reconstruction
passes 1–3.

> ⚠️ **The in-memory provider hides real bugs.** It does not enforce unique indexes and does not
> implement `ExecuteDeleteAsync`. Tests that pass in Development can fail on PostgreSQL. The
> retention job in particular is *only* exercisable against real PostgreSQL.

**Ten tables:** `companies`, `users`, `teams`, `team_members`, `policeman_authority_grants`,
`agent_events`, `agent_chain_breaks`, `staff_tracking_configs`, `uninstall_tickets`,
`usb_session_tickets`.

---

## Entities

### `companies`

The tenant. One row per customer company.

| Column | Notes |
|---|---|
| `Id` | PK |
| `Name` (≤200), `AvatarUrl` (≤500) | Display |
| `TokenJti` | Company-token identity. Re-checked against the token payload at staff signup |
| `BusinessTimeZoneId` (≤100, default `UTC`) | **The entire company clock.** IANA id (`Europe/Berlin`) or fixed offset (`UTC+03:00`) |
| `OrgStructureVersion` (default 0) | Bumped on any team / membership / leader change, drives the SignalR org sync |
| `CreatedAt` | |

That is the whole table. Fourteen columns were dropped in
`20260808000000_CollapseBusinessClockAndDropDeadColumns`:

- **The clock, 10 columns** — `BusinessAnchorYear/Month/Day/Hour/Minute/Second`, `BusinessAnchorUtc`,
  `BusinessClockSynchronized`, `BusinessClockUpdatedAt`, `BusinessClockVersion`. §8.4 replaced the
  absolute anchor and the clock-synchronisation concept with a timezone dropdown, so company-local
  time is now a pure function of a real UTC instant plus `BusinessTimeZoneId`, computed at read time.
- **Four dead columns** — `TokenVersion`, `UninstallTotpSecret`, `UninstallTotpEnabled`,
  `UninstallTotpEnrolledAt`. Written, never read; the per-staff `AccessTotp*` columns superseded the
  company-level uninstall secret.

### `users`

One row per **machine**, not per person. See [02-REQUIREMENTS](02-REQUIREMENTS.md) §1.

| Column | Notes |
|---|---|
| `Id` | PK |
| `CompanyId` | Tenant. Cascade delete from `companies` |
| `DeviceKey` (≤128) | **Unique index.** Hardware-derived, stored trimmed + lowercased. The login identifier, and the only real guard against double enrollment |
| `Username` (≤200) | Display name. **Not unique** — two people may register the same name |
| `PasswordHash` (≤500) | Argon2id. Exists only so leaders / policemen / admins can log into the desktop app (§1.6) |
| `Role` | Stored as the string `Admin` or `Staff` (≤20). Leader and policeman are **derived**, never stored here |
| `AvatarUrl` (≤500), `CreatedAt` | |
| `LastHeartbeatAt`, `LastSeenAt`, `LastOnline` | Presence, written by heartbeat and by ingest of `heartbeat` / `connectivity` |
| `AccessTotpSecret` (≤512) | Per-staff TOTP seed, AES-GCM encrypted at rest with an `enc:v1:` prefix |
| `AccessTotpEnabled`, `AccessTotpEnrolledAt` | Enrollment state. `AccessTotpEnrolledAt` doubles as the `secretVersion` the agent uses to decide whether to re-provision |
| `AccessTotpFailedAttempts` (default 0), `AccessTotpLockoutUntil` | Server-side brute-force lockout: 8 failures → 15 min |
| `AccessTotpLastUsedStepUsb`, `…Uninstall` (default 0) | Replay protection, tracked per purpose |
| `IsPoliceman` (default false), `PolicemanUpdatedAt`, `AuthorityVersion` (default 0) | Policeman state |

`SessionVersion` and `IsDisabled` were **added and then dropped**. They were added in
`20260807010000_AddUserSessionVersion` (which reached production) and dropped again in
`20260808000000_…` once §1.7 was confirmed: there is no employee lifecycle, therefore no disable, no
delete and no session revocation. Dropping `SessionVersion` also removed a database round-trip from
every authenticated request, and the `session_ver` JWT claim that went with it.

### `teams` / `team_members`

`teams`: `Id`, `CompanyId`, `Name` (≤200), `LeaderUserId?`, `CreatedAt`, `UpdatedAt`.

- `LeaderUserId` is nullable and **uniquely indexed** — one team per leader
- `(CompanyId, Name)` is unique
- The leader relationship uses `DeleteBehavior.Restrict`, the **only non-cascade relationship in the
  model**. Deleting a user who leads a team fails rather than orphaning the team

`team_members`: composite PK `(TeamId, StaffUserId)`, plus a **unique index on `StaffUserId` alone**,
which physically enforces one team per staff member.

### `policeman_authority_grants`

Composite PK `(StaffUserId, PackageId)`, one row per granted package, plus `GrantedAt` and
`GrantedByUserId?`. `PackageId` (≤64) is separately indexed. Cascades from `users`.

Granted packages are deliberately kept distinct from a team leader's *inherent* views, which are
computed and never stored. See [08-SECURITY](08-SECURITY.md).

### `agent_events`

The entire telemetry store. Every captured record is a row here.

| Column | Notes |
|---|---|
| `Id` | PK |
| `CompanyId`, `UserId` | Tenant + machine, both cascade |
| `ClientEventId` | **Unique with `UserId`.** The idempotency key, and the *only* dedup rule |
| `EventType` (≤64) | Checked against the allowlist below |
| `OccurredAt` | The real UTC instant the event happened. For `timetrack` this is the **end** of the window |
| `ReceivedAt` | When the server stored it |
| `PayloadJson` | `text`, no length limit at the database. Ingest caps it at 2 000 000 characters |
| `VaultSequence?`, `ChainHash?` (≤128) | The agent's vault position, lifted out of the payload at ingest. Feeds server-side chain anchoring |
| `SegmentStartedAt?`, `WorkedSeconds?`, `IdleSeconds?` | Denormalized timetrack window — see below |

**Event types** (enforced allowlist, `AgentEventTypes`): `heartbeat`, `connectivity`, `timetrack`,
`screenshot_meta`, `browser_history`, `usb_event`, `vault_alert`, `registration`, `power_off`,
`uninstall`, `app_broken`.

Two of these are written **by the server**, not the agent: `registration` at staff signup, and
`uninstall` when an uninstall ticket is consumed.

#### The denormalized timetrack columns

`SegmentStartedAt` / `WorkedSeconds` / `IdleSeconds` are populated at ingest, on `timetrack` rows
only, by parsing the payload once. They are **null on every other event type, and on rows ingested
before `20260808010000_AddTimeTrackAggregateColumns`** — every read path has to tolerate that (the
aggregate falls back to `OccurredAt` for a missing `SegmentStartedAt`).

They exist because three features are otherwise unaffordable:

- **Today's overview** (§14.1) and the **leaderboard** (§14.2) become one
  `SUM(...) ... GROUP BY UserId` in SQL. Without the columns, answering "hours worked today" means
  pulling and JSON-parsing roughly 1 440 payloads per staff member per day, for 20–50 staff, on every
  refresh. The desktop app's rejected fallback was one request per member per minute.
- The **gap query** (§12.4) reads the covered interval as two column reads with no JSON at all.

Because the gap query and the timeline both derive coverage from the same interval, the "data
missing" markers and the drawn timeline can never disagree.

`OccurredAt` remains the row's canonical instant; `SegmentStartedAt` is strictly the start of the
window `OccurredAt` closes.

#### What was removed

`BusinessOccurredAt`, `BusinessTimeZoneId` and `BusinessClockVersion` were dropped from
`agent_events`, along with the index `(CompanyId, BusinessOccurredAt)`.

`BusinessOccurredAt` stored a **wall clock labelled as an instant** — a `timestamptz` column holding
a value that was really company-local time. Company-local time is now derived at query time from
`OccurredAt` (a real instant) plus `companies.BusinessTimeZoneId`, and returned as
`businessOccurredAt` with `DateTimeKind.Unspecified`, because it is a clock face and must never
masquerade as an instant.

The consequence is accepted and recorded as E5 in [10-GAP-ANALYSIS](10-GAP-ANALYSIS.md): changing the
company timezone retroactively changes how historical data reads.

### `agent_chain_breaks`

New in `20260808020000_AddServerChainAnchoring`. It replaced `agent_sequence_states`, which was
dropped in the same migration.

| Column | Notes |
|---|---|
| `Id` | PK |
| `UserId` | Cascade from `users` |
| `AtSequence` | The vault sequence the two hashes disagree on. Everything before it is still anchored |
| `KnownChainHash` (≤128) | The hash the server accepted **first**, and still treats as the anchor |
| `ReportedChainHash` (≤128) | The hash that arrived afterwards claiming the same sequence |
| `OccurredAt` | When the conflicting record claims to have happened — this is where the §12.4 marker lands |
| `DetectedAt` | When the server noticed |

**What it is for.** The vault's HMAC key never leaves the staff machine, so the server can never
recompute a chain hash and can never verify the chain in the cryptographic sense. What it *can* do is
hold the agent to hashes it has already published:

- Re-sending a sequence under the **same** hash is an ordinary offline/reconnect replay. It passes
  silently. Rejecting that was the original defect — §13.1 forbids dropping anything.
- Re-sending the same sequence under a **different** hash means the vault was rewritten, or wiped and
  restarted from sequence 1. That is precisely the tamper an agent's own integrity report can never
  reveal, because after a wipe the fresh chain verifies perfectly against itself.

Either way the event is still stored. The break is recorded alongside it, and surfaces through
`GET /api/tracking/gaps` as a zero-duration point with `cause: "chain_break"`.

`(UserId, AtSequence)` is **unique** on purpose: the outbox retries, and one fork must not multiply
into a wall of identical markers.

The dropped `agent_sequence_states` table had `LastVaultSequence`, `LastChainHash`, `GapCount` and
`UpdatedAt`. `LastChainHash` was written and never read, `GapCount` could only ever increment, and
`LastVaultSequence` was used to *reject* late batches — which §13.1 forbids. Anchoring now reads
`agent_events` itself, already indexed by `(UserId, VaultSequence)`, and records only disagreements.

### `staff_tracking_configs`

PK is `StaffUserId` (a one-to-one with `users`). Columns: `CompanyId`, `ScreenshotQuality` (≤16,
`Low`/`Medium`/`High`), `ScreenshotPeriodSeconds` (default 180), `TimeTrackEnabled`,
`BrowserHistoryEnabled`, `ScreenshotEnabled`, `ConfigVersion` (default 1), `UpdatedAt`.

`ConfigVersion` increments on every admin change and drives the SignalR push. A row is created
lazily on first read if one does not exist.

### `uninstall_tickets` / `usb_session_tickets`

SHA-256 hex hashes of single-use approval tickets, uniquely indexed on `TicketHash`, with `ExpiresAt`
and a `Consumed` flag. Lifetimes: uninstall 10 minutes, USB 5 minutes.
`usb_session_tickets.DeviceInstanceId` (≤512) records which physical device the grant was for.
`uninstall_tickets.DeviceUserId` is nullable; the USB table's is not.

Both are garbage-collected by the retention job seven days past expiry.

---

## Indexes

The 22 secondary indexes the model declares, as `AppDbContextModelSnapshot.cs` has them. Primary
keys are not listed; nor are the ones EF generates for a foreign key it does not already find as an
index's leading column.

| Table | Index | Unique | Serves |
|---|---|---|---|
| `users` | `DeviceKey` | ✔ | Login, and the real guard against double enrollment |
| `users` | `CompanyId` | | Company-wide staff listing |
| `users` | `(CompanyId, IsPoliceman)` | | Policeman roster |
| `teams` | `CompanyId` | | Org tree |
| `teams` | `LeaderUserId` | ✔ | One team per leader |
| `teams` | `(CompanyId, Name)` | ✔ | No duplicate team names |
| `team_members` | `StaffUserId` | ✔ | **One team per staff member**, enforced physically |
| `policeman_authority_grants` | `PackageId` | | Grant lookups |
| `agent_events` | `(UserId, ClientEventId)` | ✔ | Ingest deduplication |
| `agent_events` | `(UserId, OccurredAt)` | | Per-staff timeline, gallery, gap coverage |
| `agent_events` | `(UserId, EventType, OccurredAt)` | | Every filtered per-type read — the hot one |
| `agent_events` | `(CompanyId, OccurredAt)` | | Company-wide reads |
| `agent_events` | `(UserId, VaultSequence)` | | Chain anchoring at ingest |
| `agent_events` | `EventType` | | Type scans |
| `agent_chain_breaks` | `(UserId, AtSequence)` | ✔ | One marker per rewritten sequence |
| `agent_chain_breaks` | `(UserId, OccurredAt)` | | Positioning markers in a period |
| `staff_tracking_configs` | `CompanyId` | | Company config sweep |
| `uninstall_tickets` | `TicketHash` | ✔ | Ticket consumption |
| `uninstall_tickets` | `CompanyId` | | |
| `usb_session_tickets` | `TicketHash` | ✔ | Ticket consumption |
| `usb_session_tickets` | `CompanyId`, `DeviceUserId` | | |

`(CompanyId, BusinessOccurredAt)` on `agent_events` was **dropped** with the column.

---

## Migrations

Fifteen, in order. **Never edit or delete one that has shipped** — a deployed migration is permanent
history. Before squashing or rewriting anything, check `__EFMigrationsHistory` on *every* environment
it may have reached.

| # | Migration | Committed | Applied in production |
|---|---|---|---|
| 1 | `20260804163819_InitialAuth` | ✔ | ✔ |
| 2 | `20260804234949_LifecycleTotp` | ✔ | ✔ |
| 3 | `20260805000709_AgentEventsIngest` | ✔ | ✔ |
| 4 | `20260805002949_TrackingVaultAndConfig` | ✔ | ✔ |
| 5 | `20260805005133_BusinessClock` | ✔ | ✔ |
| 6 | `20260805010638_StaffAccessTotpAndUsb` | ✔ | ✔ |
| 7 | `20260805014220_TeamsOrgStructure` | ✔ | ✔ |
| 8 | `20260805021235_AuthorityPackagesPolicemen` | ✔ | ✔ |
| 9 | `20260805153906_NullableTeamLeader` | ✔ | ✔ |
| 10 | `20260806194000_AddAgentEventUserOccurredIndexes` | **uncommitted** | **✔ applied** |
| 11 | `20260807010000_AddUserSessionVersion` | **uncommitted** | **✔ applied** |
| 12 | `20260807010100_WidenTotpSecretColumn` | **uncommitted** | **✔ applied** |
| 13 | `20260808000000_CollapseBusinessClockAndDropDeadColumns` | **uncommitted** | not applied |
| 14 | `20260808010000_AddTimeTrackAggregateColumns` | **uncommitted** | not applied |
| 15 | `20260808020000_AddServerChainAnchoring` | **uncommitted** | not applied |

Migrations 10–15 are additions from the reconstruction passes that are not yet in git. **Three of
them — 10, 11 and 12 — are already applied to the live database**, confirmed by querying
`__EFMigrationsHistory` there. That is why the add-then-drop churn between 11 and 13 must stay as it
is: 11 added `SessionVersion` / `IsDisabled` and 13 drops them again, which looks like squashable
debt, but a squashed replacement would be seen as unapplied and would try to re-add columns that
already exist, failing on startup.

Migrations 1–9 have `.Designer.cs` files; 10–15 do not. Harmless at runtime — EF only needs
`AppDbContextModelSnapshot.cs`, which is verified in sync — but inconsistent.

Migration integrity has been checked **empirically** rather than by inspection: the full chain was
replayed into a scratch PostgreSQL database and diffed against the model-derived DDL — 10 tables,
85 columns, 32 indexes, 24 constraints, all diffs empty. The in-memory test suite cannot perform this
check, and CI never applies a migration, so schema work still has no automated coverage
([10-GAP-ANALYSIS](10-GAP-ANALYSIS.md) C1).

> **Never point automated tooling at the live `teamscop` role.** A subagent doing exactly that took
> production down during pass 3. Provision a throwaway role and database instead. See
> [08-SECURITY](08-SECURITY.md).

---

## Screenshot storage

Screenshot JPEGs are **extracted from `PayloadJson` at ingest** and written to the filesystem under
`Storage:ScreenshotRoot` (production: `/var/lib/teamscop/screenshots`), laid out as:

```
{root}/{userId:N}/{eventId:N}/d{displayIndex}.jpg
```

Written temp-file-then-rename. The row keeps only compact metadata —
`{ storage: "file", displays: [{ displayIndex, width, height, size }], vaultSequence, chainHash }` —
so no read path ever drags image bytes through PostgreSQL to answer "how many displays, how big".
A blob write failure propagates rather than storing a half-capture the agent believes it delivered.

This matters because a single capture is roughly 89 KiB of base64 text if kept inline, and at 20–50
employees that fills the hottest table in the database very quickly.

---

## Volume and retention

Rough arithmetic per employee, assuming an 8-hour day and a 3-minute screenshot interval on a single
display:

| Signal | Rate | Daily volume |
|---|---|---|
| Heartbeat + connectivity | every 30 s | ~2 800 rows |
| Time track | one closed window every 60 s | ~480 rows |
| Screenshots | every 180 s | ~160 captures, ~8 MB of JPEG |
| Browser history | per poll | small |

**Retention is 30 days** (§13.2), bound to `Retention:AgentEventsDays` — note the section is
`Retention`, not `Storage`; `Storage__AgentEventsDays` binds to nothing. `0` disables pruning.

`RetentionHostedService` runs hourly, 30 seconds after startup so migration finishes first, and each
pass:

1. Deletes `agent_events` older than the cutoff in batches of 5 000, up to 200 000 rows per run, and
   deletes each row's screenshot directory
2. Deletes `agent_chain_breaks` older than the cutoff — a marker outlives nothing, once the events it
   sits between are gone there is no timeline left to mark
3. Garbage-collects `uninstall_tickets` and `usb_session_tickets` more than 7 days past expiry

The batching bound is not decoration. At 50 staff the inflow is roughly 172 000 rows a day, so the
earlier design — a single 5 000-row pass every six hours — could never keep up, and `agent_events`
grew without bound at *any* retention setting.

## Backups

`pg_dump` runs on a daily systemd timer. There is no point-in-time recovery.
`companies → users → agent_events` cascades on delete, so removing one company row destroys its
entire history irrecoverably between dumps.
