# 08 — Security Model

What Teamscop defends against, what it deliberately does not, and where the implementation still
falls short of its own claims.

Verified against the tree on 2026-08-07, after reconstruction passes 1–3. The previous version of
this document earned its keep by *not overclaiming*; that discipline is kept here. Where something
got genuinely stronger — server-side chain anchoring, the audit log, the authorization split — the
new claim is stated together with what it still does not catch.

---

## Threat model

### Who the adversary is

The primary adversary is **the monitored employee**, who:

- Has **local administrator rights** on their own PC — they installed the agent themselves
  ([02-REQUIREMENTS](02-REQUIREMENTS.md) §2.1)
- Can read any file on the machine, stop services, edit the registry, and attach a debugger
- Wants to stop being monitored, remove evidence, or move data onto a USB stick

Secondary: someone who obtains a leaked company token, and anyone on the network path.

### The realistic bar

Because the employee is a local admin, **a determined technical user can always win on their own
machine.** This is accepted (§11.5). The goal is to stop casual circumvention and to make
circumvention *visible* — and, since pass 3, to make it visible **from the server**, where the
employee has no write access.

### Out of scope

- Malicious administrators — the admin is the customer
- Physical attacks: disk removal, cold boot, evil maid
- Nation-state or targeted attackers
- Employee lifecycle abuse — nobody is disabled or deleted (§1.7), so there is nothing to abuse

---

## Credentials and secrets

| Secret | Where it lives | Protection |
|---|---|---|
| Password | `users.PasswordHash` | Argon2id, **32 MiB**, t=3, p=2. Parameters are stored in the hash string (`argon2id$3$32768$2$salt$hash`), so they can be raised later without invalidating existing hashes |
| Access token (JWT) | Agent + desktop app, on disk | DPAPI on Windows; `%ProgramData%\Teamscop\Agent` carries an explicit DACL granting only `SYSTEM` and `Administrators`, inheritance disabled |
| JWT signing key | `/etc/teamscop/api.env` | Generated at install, mode 600. Fail-fast: the API refuses to start on a key under 32 characters |
| Company token key | Server config + **a constant compiled into the agent** | AES-256; must match on both sides |
| TOTP root secret | `users.AccessTotpSecret` | AES-256-GCM at rest, `enc:v1:` prefix. The wrapping key is HKDF-SHA256 over `Jwt:Key` — so the JWT key protects the TOTP secrets too, and rotating it orphans every enrollment |
| TOTP **purpose-derived** secrets | `%ProgramData%\Teamscop\Agent\approval.bin` on each staff PC | DPAPI LocalMachine with entropy bound to the device key. On Windows, if sealing fails the file is **not written** rather than written in the clear |
| Vault master key | Derived on the staff PC, never stored | HKDF-SHA256 over `deviceKey ‖ companyTokenKeyBase64` — **both present on the machine** |

### Access tokens

Tokens carry **no `exp` and no `nbf`**, and `ValidateLifetime` is off. Sessions never expire (§3.1)
and there is no revocation (§3.3).

> **The `session_ver` claim is gone.** It was added, reached production, and was then removed with
> the rest of employee lifecycle (§1.7). Nothing in the product ever bumped it, and validating it
> cost a database round-trip on every authenticated request. A token is now verified by signature,
> issuer and audience only.
>
> The honest consequence: **a stolen access token is valid forever.** There is no mechanism to
> invalidate one — not password change, not anything. §3.3 makes that a product decision rather than
> an oversight, but it is the single largest standing weakness in the credential model, and it should
> be re-opened if the hosting question (SaaS vs self-hosted) is ever answered in favour of SaaS.

The only thing that ends a session is the account row disappearing, which every service reports as
`SessionInvalidException` → **401**, so the desktop shell returns to the login screen instead of
looping on cached data. See the 401/403 contract in [05-API](05-API.md).

### Approval codes

RFC 6238 TOTP, SHA-1, 30-second step, 6 digits, constant-time comparison.

`DerivePurposeSecret` applies HKDF-SHA256 with a fixed salt and the purpose as `info`, giving USB and
uninstall **independent code streams from one stored secret**. A USB code cannot open an uninstall,
and neither derived secret can be inverted to recover the base secret.

**The distribution model is the important part.** Staff never learn their own credential. The admin
reads the current code from the app and relays it out of band — phone, Telegram — and the employee
types it into a sticker (§10.1–10.2). Every issue is audited, so who asked for a code is on record
even though the code itself leaves the system.

Defences exist on **both** sides, because both paths accept codes:

| | Server (`/api/lifecycle/*/verify`) | Agent (`LocalApprovalVerifier`) |
|---|---|---|
| Lockout | 8 failures → 15 min | 8 failures → 15 min |
| Counter decay | Cleared when the lockout expires | Cleared on success, on lockout expiry, and when the last failure is older than the window |
| Replay | Single-use per purpose, by matched time step | Single-use per purpose, by matched time step |
| Rate limit | 20/min per IP, plus per-`(IP, deviceKey)` backoff | n/a — local input only |
| Audit | Every grant, denial and consumption | Local state file only |

The counter decay is not cosmetic: without it an attacker parks the counter at 7 and the employee's
next genuine typo locks them out.

TOTP runs on **UTC** on both sides, which is what RFC 6238 defines. §10.4 is unresolved — the owner
asked about company-timezone codes — but timezone-relative codes add no security and break across DST
transitions. The single construction site is parameterised so the decision can be changed in one
place if it goes the other way.

### Offline approval codes: the accepted trade

§9.6 and §11.2 require that USB approval and uninstall verify **with no server call**. That is only
possible if the machine holds a verifying secret. So it does: `GET /api/lifecycle/totp/me/secrets`
provisions each staff machine with its two purpose-derived TOTP secrets.

Stated plainly:

- **The agent holds material that can generate valid approval codes for itself.** A local-admin
  employee who extracts `approval.bin` — and they can, DPAPI LocalMachine is decryptable by any
  administrator on that machine — can mint their own USB and uninstall codes indefinitely.
- **§10.3 accepts this.** Offline capability was judged worth more than a secret the machine's own
  operator was never going to be reliably denied anyway.

What the design still buys, and it is not nothing:

- Only the **derived** secrets are provisioned, never the root. Extracting the USB secret does not
  yield an uninstall code, and does not yield a future third purpose
- The route is strictly self-scoped — identity comes from the token and no id is accepted from the
  request — so it cannot be pointed at another machine
- `secretVersion` lets an admin re-enroll a compromised machine and have the agent re-provision
- Every USB session and uninstall still produces a `usb_event` / `uninstall` record in the durable
  outbox, which uploads whenever connectivity returns (§13.1). Offline approval is not invisible
  approval

Without this route the failure mode was worse, not better: the agent called an endpoint that did not
exist, so every USB approval and every uninstall **failed closed forever**.

---

## Data integrity

### On the staff PC

Every captured record goes to the vault before it is queued for upload: Brotli compress →
AES-256-GCM encrypt → HMAC-SHA256 chain link over the previous record's hash, with a separate tip
file holding the current sequence and hash. Crash recovery discards uncommitted records at startup so
the tip and the records cannot disagree permanently.

> ⚠️ **Local tamper-evidence still does not hold on its own.** The vault master key derives from the
> device key plus a compile-time constant, both available on the machine. An employee with local
> admin can decrypt the vault, forge chain links, and re-sign the tip. The encryption ("hide") works
> against a casual user; the local chain ("prove") does not work against the stated adversary.

### Server-side chain anchoring — what it does and does not catch

This is the change that makes §12.1–12.2 partly real rather than entirely aspirational. It works
**without the server ever holding the vault key.**

The mechanism: `agent_events` already stores `(UserId, VaultSequence, ChainHash)` for every record.
At ingest the server takes the **first** chain hash it has seen for a given sequence as the anchor,
and compares every later claim about that sequence against it.

| Case | Result |
|---|---|
| A sequence arrives that the server has never seen | Anchored. Stored |
| A sequence arrives again with the **same** hash | Ordinary offline/reconnect replay. Silently fine, stored, deduplicated by `(UserId, ClientEventId)` |
| A sequence arrives again with a **different** hash | Row written to `agent_chain_breaks`, surfaced at the exact position in `GET /api/tracking/gaps`. **The event is still stored** — §13.1 never drops |

**What it catches.** The one attack local verification structurally cannot see: a vault that was
wiped and restarted, or rewritten from some sequence onward. After a wipe the fresh chain verifies
perfectly against itself, and the agent's own integrity report says everything is fine. The server
notices because sequence *n* now carries a hash different from the one it already published.

It also removes a false positive that made the old signal worthless: sequence rewinds after a
reconnect are no longer treated as breaks. The previous design *rejected* late batches on a
`LastVaultSequence` watermark, which both violated §13.1 and fired on ordinary offline cycles.

**What it does not catch.** Be clear about all of this:

- **It is not cryptographic verification.** The server cannot recompute any hash. It only checks
  self-consistency of what the agent published, so it detects *contradiction*, not *forgery*
- **An employee who forges the chain consistently and never re-sends a sequence is not detected.**
  Rewrite the vault, keep going forward from the true tip with correctly-forged links, and every
  sequence the server sees is new. Nothing contradicts anything
- **Data withheld before it is ever sent is invisible here.** Suppressing a capture at the source
  leaves no sequence to disagree with. That shows up, if at all, as a coverage hole in
  `GET /api/tracking/gaps` — a *different* signal with a different meaning
- **It says nothing about payload contents.** A record whose payload was altered before the vault
  ever saw it is anchored just as happily as an honest one
- **Retention deletes markers.** `agent_chain_breaks` rows are pruned with the events they sit
  between, so evidence older than 30 days is gone

Because of the second and third points, §12.5 stands: the desktop app must label these markers
**"data missing"**, not "tampering". A chain break is now good evidence that *something* rewrote the
vault, but coverage holes — which share the same view — still have innocent explanations
(`agent_offline`, `helper_down`, `not_uploaded`).

### In transit

TLS terminated at nginx. Kestrel listens on loopback only. `X-Forwarded-For` is honoured **only from
loopback**: `KnownNetworks` and `KnownProxies` are cleared and then loopback alone is added, so the
client IP that drives rate-limit partitioning and audit records cannot be spoofed from off-box. This
closes a previously-listed gap.

### At rest on the server

PostgreSQL with no database-level encryption. Screenshot JPEGs are files under
`/var/lib/teamscop/screenshots`, reachable only through authorized endpoints, which resolve the
capture's owner and run the full authorization check **before any cache lookup** — so a revoked
viewer cannot replay a previously fetched thumbnail URL.

**Avatars are authenticated** as of B12. They were static files on a public path, so a URL that
leaked once stayed readable by anyone, forever, with no token. They now go through
`GET /api/media/avatars/{fileName}`, which requires a bearer token and scopes each image like the
staff data it is: your own face, your company's badge, and the avatar of anyone you may view.
Everything else — including a file that does not exist — is a **404**, so the response cannot be
used to enumerate which avatars exist.

The authorization rule is `ViewerContext.CanViewStaff`, the same function the tracking endpoints
use; only the decision inputs are cached, and a demotion or grant change invalidates the company's
cached reach immediately rather than at the end of a TTL. Nothing under the storage roots is
reachable without a token: `UseStaticFiles` over the avatar directory is gone.

This holds for the **deployed system** only once nginx is reloaded from the current config. The
reverse proxy used to alias `/media/avatars/` straight to disk, and nginx matches a location before
it proxies — so the API-side change alone did not close the hole. See
[09-INSTALL-DEPLOY](09-INSTALL-DEPLOY.md).

File names are still random GUIDs, deliberately not derived from the owner, and the resolver
rejects any name that is not a plain file with a known image extension before re-checking that the
composed path did not escape the avatar root.

---

## Authorization

Every decision flows through `IAccessPolicy`, which loads one `ViewerContext` per user per request
and answers every question from it as a pure function. It is registered **scoped, never singleton**,
so a grant change takes effect on the next request rather than the next process restart; org and
police mutations additionally call `Invalidate()` within the request that caused them.

Two rules hold across the entire REST surface:

1. **Tenant isolation.** Every `staffUserId`, `teamId` and `eventId` is resolved and compared against
   the caller's `CompanyId` before any data is returned.
2. **No self-monitoring.** `viewerId == targetId` is refused in `CanView` before anything else is
   considered. The one exception on a *data* path is a staff member's own
   `GET /api/tracking/timetrack`, which feeds their sticker — and it is expressed as an explicit
   `allowSelf: true` argument to `IStaffDataGuard`, not as a hole in the rule.

   `GET /api/tracking/config/me` and `GET /api/tracking/health/me` never reach `CanView` at all:
   they take the caller's id from the token, accept no id from the request, and return only that
   machine's own configuration and liveness. Neither carries captured data. Same for
   `GET /api/lifecycle/totp/me/secrets`.

### Granted vs inherent

A team leader inherently holds `view_screenshot`, `view_timetrack` and `view_browser_history`,
scoped to their own team (§4.2). They can never *inherit* `team_management`, `usb_approval` or
`uninstall_approval` — those are checked with `HasGranted`, which only consults the policeman grant
table.

### `CanViewCompanyStaff` vs `CanViewCompanyData`

Two company-wide predicates that look interchangeable and are not. Getting this wrong was a real
privilege-escalation bug, found in review after pass 3.

| | `CanViewCompanyStaff` | `CanViewCompanyData` |
|---|---|---|
| Question | "May they *list* every employee?" | "May they *read tracking data* for every employee?" |
| Qualifying packages | the three view packages **plus** `usb_approval`, `uninstall_approval` | the three view packages **only** |
| Source | granted packages | granted packages |

Two things make the split necessary:

1. **The approval packages must not carry sight of anyone's screen.** Issuing USB codes is a routine,
   low-privilege delegation. An approval-only policeman needs to *pick* an employee to issue a code
   for — so they need the roster — but they have no business reading that employee's screenshots.
2. **Both are keyed on GRANTED packages, not the effective set.** This is the part that was wrong.
   A team leader carries the three view packages *inherently*, scoped to their own team. If
   company-wide reach were decided by the effective set, granting that leader `usb_approval` — one
   routine delegation — would have silently promoted their inherent team-scoped views to the whole
   company and handed them every employee's screenshots and browsing.

`CanViewCompanyData` is what actually gates data today: `TeamService.ListVisibleStaffAsync` (which
drives every tracking view), `WorkSummaryService` (overview and leaderboard), and `ViewerContext.CanView`
(therefore every per-staff read behind `IStaffDataGuard`).

> **Honest note:** `CanViewCompanyStaff` is currently **defined but has no call site.** The roster
> screen it was written for — `GET /api/lifecycle/totp/staff` — is gated by `CanGenerateTotp`, which
> admits the same approval-only policemen, and view-only policemen reach the roster through
> `CanViewCompanyData` anyway. So the behaviour is right and the escalation is closed, but the named
> predicate is presently documentation-in-code rather than an enforced check. Anything new that lists
> the company roster should use it rather than reaching for `CanViewCompanyData`.

### Per-event-type gating

`ViewerContext.CanViewEventType` maps each of the eleven event types to a package:

| Event types | Requires |
|---|---|
| `screenshot_meta` | `view_screenshot` |
| `timetrack` | `view_timetrack` |
| `browser_history` | `view_browser_history` |
| `usb_event` | `usb_approval` |
| `registration`, `power_off`, `uninstall`, `app_broken` (app history) | `uninstall_approval` — **never inherent for a team leader** |
| `heartbeat`, `connectivity`, `vault_alert` | any one of the three view packages |

The last row is why `GET /api/tracking/gaps` is authorized as `heartbeat`: a screenshot-only
policeman must be able to see where their screenshots stop without also being handed timetrack.

---

## Audit log

`IAuditLog` records 21 action types (`Audit/AuditActions.cs`): company creation, staff enrollment,
password change, timezone change, police grant/revoke, six team operations, tracking-config change,
TOTP enrollment and code issuance, and grant/deny/consume for both USB and uninstall approvals.

Each record carries the action, actor user id, company id, **the real client IP** (read after
`UseForwardedHeaders`, so it is the agent's or console's address, not nginx's loopback hop) and a
structured subject object, under a `["audit"] = true` logging scope.

Limits worth stating:

- **It is a log, not a table.** Records go to `ILogger`, i.e. to the systemd unit's stdout and thence
  to journald. They are as durable and as tamper-resistant as that log — which, against a server root
  user, is not at all. Nothing in the product reads them back
- On the anonymous approval routes the recorded actor is the **device user**, because that is the
  only identity available; it is not necessarily whoever typed the code
- Read operations are not audited. Viewing an employee's screenshots leaves no trace

---

## Network exposure

| Surface | Auth |
|---|---|
| `GET /health` | anonymous |
| `POST /api/auth/admin/signup`, `/staff/signup`, `/login` | anonymous, `auth` concurrency limiter |
| `POST /api/lifecycle/{usb,uninstall}/{verify,consume}` | **anonymous**, 20/min per IP, per-source backoff, account lockout |
| Everything else, including `/api/media/avatars/*` | bearer JWT |
| `/hubs/config` | bearer JWT, header or `?access_token=` |

nginx applies its own zones on top: 10 r/s burst 20 on the auth paths, 50 r/s burst 100 on the API.

The lifecycle approval endpoints are anonymous **by necessity** — the uninstall guard runs during
removal, when the agent's identity may already be gone, and the USB sticker runs before any approval
exists. They are defended by lockout, replay rejection and rate limiting rather than authentication.
They are also now the *secondary* path: the agent verifies locally first, so a machine with
connectivity and a machine without behave the same way.

---

## Operational security

### The production-database incident

**During reconstruction pass 3 a subagent setting up a PostgreSQL test fixture ran
`ALTER ROLE teamscop WITH PASSWORD 'teamscop'` against the live role.** It broke the running API and
did not restore it. Root cause was the instruction as much as the agent: it was told "a real
PostgreSQL is running locally" with no ring-fence around the production role. Resolution was to
rotate to a fresh credential and rebuild the database, which held 0 users and 0 events at the time.

**Standing rule — automated tooling must never point at the live `teamscop` role or database.**
Anything that needs PostgreSQL creates its own throwaway role and database (`teamscop_test` or
similar) and drops it afterwards. Do not read `/etc/teamscop/api.env` to "check the connection
string"; needing it is itself the signal that the tool is aimed at the wrong database.

### `GET /health` is useless as a liveness check

`app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "teamscop-api" }))` — a static
literal. **It does not touch the database, or anything else.** It returned 200 throughout the entire
outage above, which is exactly when a liveness check is supposed to earn its keep.

Use **`POST /api/auth/login` with a bogus credential: `401` means healthy.** That path resolves
configuration, opens a database connection and runs a query, so a 500 or a timeout is a real signal.

CI's `live-smoke` job now does this and is **read-only by design**. It previously signed up an admin
and a staff member on production on every merge to `main`; since §1.7 guarantees there is no delete
path, every merge permanently added a company. Do not add any request to that job that writes.

---

## Known gaps

Tracked in [10-GAP-ANALYSIS](10-GAP-ANALYSIS.md), summarised here.

**Open:**

| Gap | Consequence |
|---|---|
| Vault key derivable on the machine | Local tamper-evidence is not real against a local admin. Server anchoring compensates only for the *contradiction* cases above |
| A stolen access token is valid forever | No revocation exists, by decision (§3.3) |
| Agent holds derived TOTP secrets | A local admin can mint their own approval codes. Accepted (§10.3) |
| Company token key ships as a build constant, and the checked-in default is all zeros | A leaked or unreplaced build key compromises offline token decryption. `CompanyTokenKey.IsAllZeroBase64` exists to detect the unreplaced case — replacing it at release time is still a manual step (B2) |
| Audit log is log-only | No stored, queryable, tamper-resistant trail; reads are not audited at all |
| No code-signing certificate | SmartScreen warns on install; antivirus may quarantine the agent |
| No agent-side integration tests | The capture path, pipe, outbox and USB gate — where the reported bugs are — have zero coverage (C2) |
| CI never applies a migration | Schema work has no automated coverage (C1) |

**Closed since the previous version of this document:**

| Was | Now |
|---|---|
| Chain breaks unverifiable server-side | `agent_chain_breaks`, anchored at ingest, with the limits stated above (B1) |
| `X-Forwarded-For` trusted from any source | Trusted from loopback only |
| Avatars served as unauthenticated static files | `GET /api/media/avatars/{fileName}`, bearer-only, scoped by `CanViewStaff` (B12) |
| `/cleanup` switch bypassing the uninstall code | Deleted; zero occurrences remain (§11.3, A7) |
| USB identity was the drive letter | Keyed on device instance identity; the grant covers every node of a multi-LUN device (§9.5, A5) |
| USB device mounted and was visible | Device-node disable via SetupDi — no drive letter is ever presented (§9.2, A4) |
| Uninstall guard lifted the USB block on code entry | Moved behind `--restore-machine`, invoked by the uninstaller once removal is actually happening |
| Dead sessions returned 403 | `SessionInvalidException` → 401, so the shell can return to login |
| `ObjectDisposedException` surfaced as 400 | Explicit 500 arm; a scope-lifetime bug no longer hides behind a client-error status |
| No structured audit trail | 21 action types, with the limits noted above (C6) |

---

## Undecided and consequential

The **legal and consent model** still has no decision ([01-PRODUCT](01-PRODUCT.md), "Undecided").
Whether monitoring is disclosed to employees, which jurisdictions apply, and whether GDPR obligations
exist all bear directly on this document — particularly on retention, on an employee's access to
their own data, and on whether covert operation is acceptable at all.

The **hosting model** is equally unsettled, and it interacts with the token-revocation gap above: a
non-expiring, non-revocable bearer token is a different risk on a shared SaaS instance than on a
single-customer box.
