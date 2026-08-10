# 05 — API Reference

ASP.NET Core 8 minimal API. Base URL in production: `https://teamscop.com`.

Verified against `backend/Teamscop.Api/Endpoints/*.cs`, `Program.cs`, `Hubs/ConfigHub.cs` and
`Errors/ExceptionHandlingMiddleware.cs` on 2026-08-07, after reconstruction passes 1–3.

Authentication is a bearer JWT. **Authorization is decided in the service layer, not by policies** —
every authenticated endpoint is mapped with a plain `RequireAuthorization()`, and the real check
happens inside the service, which throws `UnauthorizedAccessException` (→ 403) on denial. Endpoint
handlers carry no `try`/`catch`; the credential-checking handlers listed under
[Error contract](#error-contract) are the deliberate exceptions.

**Rate-limit policies** (`Program.cs`):

| Policy | Shape | Applied to |
|---|---|---|
| `auth` | Concurrency limiter per IP: 4 permits, queue 16 — protects Argon2id hashing | the whole `/api/auth` group |
| `api` | Fixed window per IP: 600/min, no queue | `/api/tracking`, `/api/business-time`, `/api/ingest`, `/api/org`, `/api/teams`, `/api/police`, `/api/media`, and the authenticated `/api/lifecycle` routes |
| `lifecycleAnon` | Fixed window per IP: 20/min, no queue | the four anonymous `/api/lifecycle/{usb,uninstall}/{verify,consume}` routes |

Client IP comes from `X-Forwarded-For` but only when the immediate peer is loopback —
`ForwardedHeadersOptions.KnownProxies` is cleared and then set to loopback only, so the header
cannot be spoofed from off-box.

---

## Access tokens

`JwtTokenService` issues a HS256 token with these claims and **no `exp` and no `nbf`**:

| Claim | Value |
|---|---|
| `sub` | user id |
| `jti` | random per token |
| `device_key` | the machine's device key |
| `username` | display name |
| `role` | `admin` or `staff`, lowercased |
| `company_id` | tenant id |

`ValidateLifetime` and `RequireExpirationTime` are both **false**: sessions never expire by time
(§3.1) and there is no revocation (§3.3). `expiresIn` in the login/signup response is always `0`.

> **There is no `session_ver` claim.** It was removed with the rest of employee lifecycle (§1.7),
> together with the per-request database round-trip in `OnTokenValidated` that validated it. A token
> is now verified by signature, issuer and audience alone. The only thing that invalidates a session
> is the account row disappearing, which surfaces as `SessionInvalidException` → **401**.

---

## Auth — `/api/auth`

Rate limit: `auth`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/admin/signup` | anonymous | Create a company + admin. **Multipart form**: `deviceKey`, `username`, `password`, `avatar?`. Returns access token + company token |
| POST | `/staff/signup` | anonymous | Join a company. Multipart, plus `companyToken`. Server re-decrypts the token and re-checks `TokenJti` |
| POST | `/login` | anonymous | JSON `{ deviceKey, password }` → access token. Admins also get a freshly minted company token |
| GET | `/me` | bearer | Current identity + company |
| POST | `/company-token/reveal` | bearer, admin | Re-mint and display the company token → `{ companyToken }` |
| POST | `/password/change` | bearer | JSON `{ currentPassword, newPassword }` → `{ ok: true }`. Min 8 characters |

Both signup routes call `DisableAntiforgery()` because they are multipart posts from a desktop
process, not a browser form.

`/login` and `/password/change` keep a **local `catch (UnauthorizedAccessException)` returning 401**,
not the middleware's 403: a rejected password is an *authentication* failure, and the desktop login
screen keys on 401.

Enrollment races are handled at the database: the unique index on `users.DeviceKey` is the only real
guard, and losing the race returns **400** with a human-readable message, not a 500.

> **Removed.** `POST /api/auth/users/{userId}/disable`, `POST …/enable` and `DELETE
> /api/auth/users/{userId}` no longer exist. §1.7 says there is no employee lifecycle, so there is no
> disable, delete or offboard path anywhere in the API.

## Lifecycle — `/api/lifecycle`

| Method | Route | Auth | Rate | Purpose |
|---|---|---|---|---|
| POST | `/totp/enroll` | bearer, **admin only** | `api` | JSON `{ staffUserId }`. Generates a fresh Base32 secret, returns it plus an `otpauth://` URI, and resets lockout + replay counters |
| GET | `/totp/staff` | bearer, `CanGenerateTotp` | `api` | Every staff member in the company with enrollment status |
| GET | `/totp/status/{staffUserId}` | bearer, `CanGenerateTotp` | `api` | Enrollment status for one staff member |
| GET | `/totp/code/{staffUserId}?purpose=usb\|uninstall` | bearer, `CanGenerateTotp` | `api` | **Current 6-digit code** plus `periodSeconds` and `remainingSeconds`. This is how the admin obtains a code to relay out of band |
| GET | `/totp/me/secrets` | bearer, **self, staff only** | `api` | The calling machine's own offline approval secrets |
| POST | `/uninstall/verify` | **anonymous** | `lifecycleAnon` | `{ deviceKey, totpCode }` → `{ uninstallTicket, expiresIn }` (10 min) |
| POST | `/uninstall/consume` | **anonymous** | `lifecycleAnon` | `{ uninstallTicket }` → `{ allowed }`. Also writes the synthetic `uninstall` agent event |
| POST | `/usb/verify` | **anonymous** | `lifecycleAnon` | `{ deviceKey, totpCode, deviceInstanceId? }` → `{ usbSessionTicket, expiresIn, deviceInstanceId }` (5 min) |
| POST | `/usb/consume` | **anonymous** | `lifecycleAnon` | `{ usbSessionTicket }` → `{ allowed }` |
| POST | `/heartbeat` | bearer | `api` | Agent liveness → `{ ok, at }` |

`CanGenerateTotp` = admin, or a policeman holding a **granted** `usb_approval` or
`uninstall_approval` package. When the actor holds both, `?purpose=` picks; otherwise the purpose is
implied by whichever package they hold, defaulting to `usb`.

### `GET /totp/me/secrets` — new, and load-bearing

```json
{ "usbSecret": "…", "uninstallSecret": "…", "secretVersion": 1754500000 }
```

Strictly self-scoped: the caller's identity comes from the token, and no id is accepted from the
request, so it cannot be pointed at another machine. Only the two **purpose-derived** secrets are
returned, never the root secret, so a leaked USB secret can never open an uninstall. An unenrolled
machine gets `null`s and `secretVersion: 0` rather than an error, so the agent keeps whatever it
already holds and retries. `secretVersion` is the enrollment timestamp, which lets the agent skip a
rewrite when nothing changed and re-provision when the admin re-enrolls.

Without this route every USB approval and every uninstall on an offline machine fails closed
forever — the agent verifies codes locally (§9.6, §11.2) and has nothing to verify against until it
has been provisioned once. Security consequences are in [08-SECURITY](08-SECURITY.md).

### Approval-code protections

RFC 6238 TOTP, SHA-1, 30-second step, 6 digits, constant-time comparison. `DerivePurposeSecret`
applies HKDF-SHA256 with the purpose as `info`, giving USB and uninstall independent code streams
from one stored secret.

Server-side, on the anonymous verify routes:

- 8 failed attempts → 15-minute lockout. When the lockout expires the counter is cleared, so a
  remote attacker cannot park the account at 7 failures and have the employee's next typo re-lock it
- Each code is single-use per purpose, tracked by matched time step
- 20 requests/min per IP (`lifecycleAnon`), plus a per-`(IP, deviceKey)` backoff counter that a
  rejected code feeds directly
- Every grant, denial and consumption is written to the audit log

A rejected code returns **401** from a local catch in the endpoint, not 403, and the same catch feeds
the backoff counter.

## Ingest — `/api/ingest`

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/batch` | bearer | Durable agent event push |

Bounds come from `IngestOptions` (all configurable):

| Bound | Default |
|---|---|
| `MaxBatchEvents` | 200 |
| `MaxPayloadChars` | 2 000 000 |
| `MaxBatchBytes` (aggregate, checked before parsing) | 8 MiB |
| Kestrel `MaxRequestBodySize` | 12 MiB — deliberately above `MaxBatchBytes`, so an oversized batch answers **400** instead of dying as a connection reset |

Behaviour:

- Deduplicated on the unique index `(UserId, ClientEventId)`. That index is the **only** dedup rule
- Event types are checked against the allowlist (see [04-DATA-MODEL](04-DATA-MODEL.md))
- Screenshot JPEGs are extracted to blob storage and replaced by compact metadata before the row is
  written; a blob write failure propagates rather than storing a half-capture
- `timetrack` payloads are parsed once at ingest into `SegmentStartedAt` / `WorkedSeconds` /
  `IdleSeconds`
- Chain anchoring runs here — see the note below
- Responses: `{ acceptedIds, duplicateIds, rejected: [{ clientEventId, reason }] }`

> **Vault sequence rewinds are no longer rejected.** §13.1 says never drop anything, and a late or
> out-of-order batch after a reconnect is normal. Instead the server *anchors*: the first chain hash
> it sees for a sequence is authoritative, re-sending that sequence under the **same** hash passes
> silently, and re-sending it under a **different** hash records a row in `agent_chain_breaks` — and
> still stores the event.

## Tracking — `/api/tracking`

Rate limit: `api`. Every route requires a bearer token.

| Method | Route | Authorization | Purpose |
|---|---|---|---|
| GET | `/config/me` | self | Own tracking config — the agent's HTTP pull path |
| GET | `/config/{staffUserId}` | can view that staff member | That machine's config |
| PUT | `/config/{staffUserId}` | **admin only** | Update config. Bumps `configVersion` and pushes `TrackingConfigUpdated` |
| GET | `/staff` | any viewer | Staff visible to the caller. Never includes self |
| GET | `/overview?date=` | `view_timetrack` | **Today's overview** (§14.1) |
| GET | `/leaderboard?from=&to=&page=&pageSize=` | `view_timetrack` | **Ranking by hours worked** (§14.2) |
| GET | `/gaps?staffUserId=&from=&to=` | any monitoring package | **Server-authoritative data gaps** (§12.4) |
| GET | `/health/me` | self | Engine-health proof for the staff sticker (§14.4) |
| GET | `/events?staffUserId=&from=&to=&eventType=&take=` | filtered per package | Raw events |
| GET | `/screenshots?staffUserId=&from=&to=&before=&take=` | `view_screenshot` | Capture list, cursor-pageable |
| GET | `/screenshots/{eventId}/thumb?display=&w=` | `view_screenshot` | Thumbnail JPEG |
| GET | `/screenshots/{eventId}/image?display=` | `view_screenshot` | Full JPEG |
| GET | `/browsing?staffUserId=&from=&to=&take=` | `view_browser_history` | Domains visited |
| GET | `/browsing/detail?staffUserId=&domain=&from=&to=&take=` | `view_browser_history` | Full URLs for one domain |
| GET | `/browsing/top-urls?staffUserId=&from=&to=&take=&takeEvents=` | `view_browser_history` | Most-visited URLs |
| GET | `/timetrack?staffUserId=&from=&to=` | `view_timetrack`, **or self** | Work / rest / gap timeline |

> **Removed.** `GET /api/tracking/chain/{staffUserId}` is gone. Chain and gap information is now
> served by `GET /api/tracking/gaps`, which is authoritative and carries positions rather than a
> single boolean.

### `GET /overview`

`?date=` is an optional **company-local** calendar day as `yyyy-MM-dd`; anything else is a 400.
Omitted means today in company time. A single `SUM … GROUP BY UserId` covers every visible staff
member, so the whole response costs a fixed handful of queries regardless of headcount — the
rejected alternative was one request per member per minute from the desktop app (§15.2).

```json
{
  "date": "2026-08-07",
  "timeZoneId": "Europe/Berlin",
  "from": "…", "to": "…", "generatedAt": "…",
  "elapsedSeconds": 46800,
  "staff": [{
    "userId": "…", "username": "…", "avatarUrl": null,
    "state": "working|idle|offline",
    "workedSeconds": 0, "idleSeconds": 0, "gapSeconds": 0,
    "dataMissing": false,
    "lastActivityAt": null, "lastHeartbeatAt": null, "lastSeenAt": null, "online": null
  }]
}
```

`elapsedSeconds` is the denominator for the row's bar only. `gapSeconds` and `dataMissing` are
measured **between the first and last segment the machine actually reported**, never from midnight —
measuring from midnight made every employee read "data missing" every morning. A machine that
reported nothing at all is only flagged when it is also failing to heartbeat; silence from an
otherwise healthy machine is someone who has not started yet, not data loss.

Rows are sorted problems-first: `offline`, then `working`, then `idle`.

### `GET /leaderboard`

`from` and `to` are **required** (400 otherwise), must not be inverted, and must span at most 31
days. `page` defaults to 0, `pageSize` to 25 and is clamped to 100. Ranking considers every visible
staff member and the page is cut *after* ordering, so `rank` is a true company rank.

```json
{ "from": "…", "to": "…", "timeZoneId": "…", "page": 0, "pageSize": 25, "total": 31,
  "rows": [{ "rank": 1, "userId": "…", "username": "…", "avatarUrl": null,
             "workedSeconds": 0, "idleSeconds": 0 }] }
```

Both `/overview` and `/leaderboard` require `view_timetrack`, and scope by **`CanViewCompanyData`** —
a team leader who also holds `usb_approval` stays scoped to their own team. See
[08-SECURITY](08-SECURITY.md).

### `GET /gaps`

`staffUserId`, `from` and `to` are required; period limit 31 days. Authorized as `heartbeat`, i.e.
"any monitoring package", so a screenshot-only policeman can see where their screenshots stop without
holding `view_timetrack`.

```json
{ "staffUserId": "…", "from": "…", "to": "…", "timeZoneId": "…",
  "missingSeconds": 900,
  "gaps": [{ "startUtc": "…", "endUtc": "…", "durationSeconds": 900,
             "cause": "agent_offline|helper_down|not_uploaded|chain_break",
             "afterSequence": null }] }
```

Coverage comes from the denormalized timetrack interval — the same numbers the timeline draws, so
the two can never disagree. Holes shorter than 2 minutes are flush jitter and are not reported. A
chain break is a point, so its start and end are equal and `durationSeconds` is 0; `afterSequence` is
the last sequence still trusted. Chain breaks are read even when coverage looks complete, because a
rewritten chain is exactly the case where the timeline has no holes.

Per §12.5 the client must label these **"data missing"**, never "tampering".

### `GET /health/me`

Self-scoped and carries **no captured data**, only liveness — which is why §4.5's self-monitoring ban
does not apply.

```json
{ "userId": "…", "status": "protected|catching_up|not_reporting|unknown",
  "statusDetail": null, "agentOffline": false, "online": true,
  "lastHeartbeatAt": "…", "lastSeenAt": "…", "lastTimeTrackAt": "…",
  "helperAlive": true, "trackingOk": true, "pendingOutbox": 0 }
```

Thresholds are shared with the overview and the gap query so "offline" cannot mean two different
things in two views: no heartbeat for **2 minutes** is offline; more than **50** queued events is
`catching_up`.

### Screenshot cursor paging

`GET /screenshots` accepts `before=<timestamp>` in addition to `from` / `to`. It returns only
captures **strictly older** than the cursor, so the gallery passes the last tile's `occurredAt` and
pages cannot drift as new captures arrive. `before` *narrows* the exclusive upper bound rather than
replacing it, so a cursor can never page outside the requested period. `take` defaults to 100 and is
clamped to 1–200.

Each row carries `businessOccurredAt` (company-local wall clock, computed at projection time) and
`businessTimeZoneId` alongside the UTC `occurredAt`, plus per-display `{ index, width, height, size }`.

Thumb and image responses are `Cache-Control: no-store`. **Authorization is resolved before any cache
lookup**, so a revoked viewer cannot replay a previously fetched URL. The thumbnail path reads the
JPEG header and refuses anything over 20 000 px on a side or 50 MP *before* decoding.

### Other tracking defaults

| Route | `take` default | Clamp |
|---|---|---|
| `/events` | 100 | 1–500 |
| `/screenshots` | 100 | 1–200 |
| `/browsing`, `/browsing/detail` | 200 | — |
| `/browsing/top-urls` | 3 urls, 200 events | urls 1–20 |

`/timetrack` requires both `from` and `to` (400 otherwise) and returns a fully covering list of
`working` / `rest` / `gap` segments — every second of the period is accounted for.

`PUT /config/{staffUserId}` body is the `StaffTrackingConfig` shape:
`{ screenshotQuality: Low|Medium|High, screenshotPeriodSeconds, timeTrackEnabled,
browserHistoryEnabled, screenshotEnabled }`. The period must be 30 s – 24 h.

## Business time — `/api/business-time`

Rate limit: `api`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/me` | bearer | The caller's company clock → `{ companyId, timeZoneId, displayName, currentOffset }` |
| GET | `/zones` | bearer | **The dropdown** — every system timezone as `{ id, displayName, currentOffset }`, ordered by offset |
| PUT | *(group root)* `/api/business-time` | bearer, **admin only** | JSON `{ timeZoneId }` → the same DTO. Broadcasts `BusinessTimeUpdated` |
| GET | `/now` | bearer | `{ companyId, timeZoneId, utc, businessLocal }` |

`timeZoneId` is an IANA id (`Europe/Berlin`) or a fixed offset (`UTC+03:00`, `+03:00`). Unknown ids
are rejected with **400** on write, never silently stored — a stored id that read back as UTC would
silently relabel every timestamp in the product.

> **Removed.** `POST /api/business-time/declare` is gone, along with the whole absolute-anchor
> concept (§8.4). Picking a timezone is the entire clock, so the operation is a `PUT` of one setting.
> Company-local times are computed at read time from `OccurredAt` + the company's *current* timezone;
> changing the timezone therefore retroactively changes how historical data reads, which is accepted
> (see [10-GAP-ANALYSIS](10-GAP-ANALYSIS.md) E5).

## Teams & org — `/api/org`, `/api/teams`

Rate limit: `api`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/api/org/structure` | admin or **granted** `team_management` | Full teams tree + unassigned staff |
| GET | `/api/org/me` | bearer | Caller's placement: `admin`, `leader`, `member` or `unassigned` |
| POST | `/api/teams` | admin or `team_management` | `{ name, leaderUserId? }` |
| PUT | `/api/teams/{teamId}` | admin or `team_management` | `{ name?, leaderUserId?, clearLeader? }` |
| DELETE | `/api/teams/{teamId}` | admin or `team_management` | → `{ deleted: true, teamId }` |
| PUT | `/api/teams/{teamId}/members` | admin or `team_management` | Replace the roster: `{ memberUserIds }` |
| POST | `/api/teams/{teamId}/members` | admin or `team_management` | Add one: `{ staffUserId }` |
| DELETE | `/api/teams/{teamId}/members/{staffUserId}` | admin or `team_management` | Remove one |

`team_management` is checked with `HasGranted`, not `Has`: a team leader must never inherit team
management by virtue of leading a team.

Every mutation bumps `companies.OrgStructureVersion`, invalidates the request's memoized
authorization contexts and every viewer's cached avatar reach, then broadcasts
`OrgStructureUpdated` to management and a fresh `AuthoritiesUpdated` to **every** staff member —
leaders gain and lose their inherent view packages the moment an assignment changes. The whole
company's effective sets are built in three batch queries; asking the access policy per member cost
two each, so one "add member" click in a 50-person company used to run ~100 queries inside the
request.

## Police — `/api/police`

Rate limit: `api`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/packages` | admin, or **granted** `team_management` / `usb_approval` / `uninstall_approval` | The authority-package catalog with labels |
| GET | `/api/police` | **admin only** | Policemen and their granted packages |
| GET | `/me` | bearer | The caller's **effective** authorities → `{ userId, isAdmin, isPoliceman, authorityVersion, packages }` |
| PUT | `/{staffUserId}` | **admin only** | `{ packages: [...] }` — promotes to policeman and replaces the grant set |
| DELETE | `/{staffUserId}` | **admin only** | Revoke → `{ revoked: true, staffUserId }` |

Packages: `view_screenshot`, `view_timetrack`, `view_browser_history`, `usb_approval`,
`uninstall_approval`, `team_management`. An unknown id is a 400. `PUT` returns the **granted** set
only — a leader's inherent views are never merged into the police grant list.

## Media — `/api/media`

Rate limit: `api`.

| Method | Route | Auth | Purpose |
|---|---|---|---|
| GET | `/avatars/{fileName}` | bearer | One avatar image |

Avatars used to be **static files on a public path**. They are staff data, and are now scoped like
staff data: a caller may fetch their own face, their company's badge, and the avatar of anyone they
may view. A caller who may not — and a file that does not exist — both get **404**; a distinct 403
would confirm which avatars exist.

The rule is `ViewerContext.CanViewStaff`, the same function the tracking endpoints use, never
re-implemented. Only the *decision inputs* are cached (the file's owner, and the viewer's reach),
and any change to leadership or a grant invalidates the company's cached reach immediately.

`fileName` must be a plain file name with a known image extension; the composed path is then
re-checked against the avatar root, so traversal cannot escape it. Responses carry `nosniff` and
`Cache-Control: private, max-age=86400` — private because it is one employee's face and proxies
are shared.

`Storage:PublicAvatarBasePath` therefore defaults to `/api/media/avatars`; the value is stored in
`AvatarUrl`, and resolution matches on the file name so rows written under an older base path still
resolve.

## Health

`GET /health` → `{"status":"ok","service":"teamscop-api"}`. Anonymous, no rate-limit policy.

> ⚠️ **This does not touch the database.** It is a static literal and returned 200 throughout a real
> production outage in which the database credential was broken. It is useless as a liveness check.
> Use `POST /api/auth/login` with a bogus credential — **401 means healthy**. See
> [08-SECURITY](08-SECURITY.md).

---

## SignalR — `/hubs/config`

`[Authorize]`. The JWT is accepted from the `Authorization` header, or from an `?access_token=`
query parameter when the path starts with `/hubs` — WebSocket clients cannot set headers on the
handshake.

### Groups — there are three

| Group | Membership | Carries |
|---|---|---|
| `staff:{userId:N}` | every connection joins its own | per-user payloads |
| `company:{companyId:N}` | **every connection in the company** | non-sensitive company-wide settings |
| `company:{companyId:N}:mgmt` | admins and holders of `team_management` | payloads REST restricts to those principals |

The company / management split is not cosmetic. Scoping the *whole* company group to management —
the obvious way to stop the org chart leaking to every employee — cut plain staff agents off from
`BusinessTimeUpdated`, which every agent subscribes to. Company-wide but non-sensitive broadcasts go
to `company:`; privileged ones go to `company::mgmt`. Three tests in `ConfigHubGroupTests` guard it:
a plain staff agent *does* receive `BusinessTimeUpdated`, does *not* receive
`OrgStructureUpdated`, and an admin does.

### Events

| Event | Group | Payload | Raised by |
|---|---|---|---|
| `TrackingConfigUpdated` | `staff:{id}` | `StaffTrackingConfig` for that machine | `PUT /api/tracking/config/{id}` |
| `AuthoritiesUpdated` | `staff:{id}` | that user's effective authorities | police grant/revoke, and any org change |
| `BusinessTimeUpdated` | `company:{id}` | the company clock DTO | `PUT /api/business-time` |
| `OrgStructureUpdated` | `company:{id}:mgmt` | full teams tree | any team or membership change |
| `PolicemenUpdated` | `company:{id}:mgmt` | policeman roster and grants | police grant/revoke |

> **nginx must proxy `/hubs/`** with `proxy_http_version 1.1`, the `Upgrade` and `Connection`
> headers, and a long read timeout. Miss any of them and SignalR negotiation fails, taking every
> push path with it. See [09-INSTALL-DEPLOY](09-INSTALL-DEPLOY.md).

---

## Error contract

`ExceptionHandlingMiddleware` is the **single place** an exception becomes a status code. The
mapping, in the order the switch evaluates:

| Exception | Status | Meaning |
|---|---|---|
| `OperationCanceledException` while `RequestAborted` | *(none)* | The viewer navigated away mid-request. Logged at Debug, no response written |
| `SessionInvalidException` | **401** | The token verified, but the account behind it no longer resolves |
| `ObjectDisposedException` | **500** | A disposed `DbContext` or stream — a scope-lifetime bug on our side |
| `UnauthorizedAccessException` | **403** | Authenticated, but not permitted |
| `NotFoundException` | **404** | Does not exist, or is not visible to the caller |
| `InvalidOperationException`, `ArgumentException` | **400** | Validation failure raised by a service |
| anything else | **500** | Unhandled |
| *(no exception — the rate limiter short-circuits)* | **429** | Over the policy limit |

Body is always `{"error": "…"}`. On 500 the message is replaced with `"An unexpected error
occurred."` outside Development, and a `traceId` is added. Only 500 is logged at Error; everything
else is logged at Information.

### 401 vs 403 is load-bearing

This distinction is a contract with the desktop app, not an implementation detail.

- **403** means *"you are someone, but not allowed here."* The shell keeps the session and falls back
  to cached data.
- **401** means *"you are nobody."* It is the only signal that sends the shell back to the login
  screen (§3.2).

Returning 403 for a dead session left the app showing "Working from a cached session" and retrying
forever, with no way for the user to sign in again. Every service that resolves a caller and finds no
row therefore throws `SessionInvalidException`, not `UnauthorizedAccessException`.

The `ObjectDisposedException` arm exists for a related reason: it derives from
`InvalidOperationException`, so without an explicit earlier arm a disposed `DbContext` surfaced as
**400** — a client-error status nothing alerts on and the logs treat as routine.

Four handlers additionally catch `UnauthorizedAccessException` locally and return **401** instead of
403, because in each of them a rejected *credential* is an authentication failure:

- `POST /api/auth/login`
- `POST /api/auth/password/change`
- `POST /api/lifecycle/uninstall/verify`
- `POST /api/lifecycle/usb/verify`

The two lifecycle handlers also record the failure against the per-`(IP, deviceKey)` backoff counter
in the same catch.
