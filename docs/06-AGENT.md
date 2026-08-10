# 06 — The Windows Agent

Everything that runs on a monitored staff PC. Constraint from
[02-REQUIREMENTS](02-REQUIREMENTS.md) §15.2: **cheap, low-end Windows 10/11 hardware.** CPU and
memory cost is a design consideration in every component here.

---

## Processes

| Process | Account | Autostart | Role |
|---|---|---|---|
| `Teamscop.StaffService.exe` | LocalSystem | Windows service, auto-restart on failure | Coordinator: vault, outbox, sync, heartbeat, USB policy, pipe server |
| `Teamscop.SessionHelper.exe` | Logged-in user | Run key | Screen capture, time-track sampling, Chrome reading |
| `Teamscop.App.exe` | Logged-in user | Run key | Staff sticker; full console for leaders/policemen |
| `Teamscop.UsbApproval.exe` | Logged-in user | Launched on demand | USB approval sticker |
| `Teamscop.UninstallGuard.exe` | Elevated | Launched by setup | Uninstall approval sticker |

### Why a separate session helper

Windows session isolation prevents a session-0 service from capturing an interactive desktop.
The helper runs inside the user's session and is the **only** process that can take a screenshot.
It forwards captured records to the service over a named pipe.

**Consequence:** if the helper dies, capture would stop. `TrackingCoordinator` therefore falls back
rather than latching into a permanently idle state: it captures **in-process by default**, switches
to helper-fed capture the first time a helper connects, and reverts to in-process if the helper goes
90 seconds without reporting. `IsTrackingHealthy` additionally requires a successful capture within
5 minutes, and that is what the sticker's `trackingOk` reflects.

In-process capture cannot reach an interactive desktop from session 0, so the fallback keeps
time-track, Chrome history and the heartbeat alive but does not restore screenshots.

---

## Identity — `Engine.Auth`

`DeviceKeyProvider` derives a stable machine identifier from hardware serials (SMBIOS, baseboard,
BIOS), hashed. This is the account identity — there is no email.

`CompanyTokenCodec` implements the offline company token: `TS1.` + base64url of
`nonce ‖ ciphertext ‖ tag` under AES-256-GCM. The key must match the server's
`CompanyToken__Key`. The cryptography is sound; **key distribution is the weak point** — see
[08-SECURITY](08-SECURITY.md).

---

## Local state — `Engine.Lifecycle`

`LocalAgentStore` persists the agent's identity and access token under
`%ProgramData%\Teamscop\Agent\agent-state.json`. Written atomically (temp file + rename) and
protected with DPAPI on Windows. The install applies a DACL granting only `SYSTEM` and
`Administrators`, with inheritance disabled.

`TotpGenerator` implements RFC 6238 (SHA-1, 30-second step, 6 digits) with constant-time
comparison, plus `DerivePurposeSecret` — an HKDF-SHA256 derivation that gives USB and uninstall
separate code streams from one stored secret.

### Offline approval secrets

`LocalApprovalSecretStore` holds this machine's two **purpose-derived** TOTP secrets in
`%ProgramData%\Teamscop\Agent\approval.bin`: DPAPI LocalMachine, entropy bound to the device key,
written temp-file-then-rename. On Windows, if sealing fails the file is **not written** — refusing to
store beats storing a TOTP secret in the clear.

They are provisioned from `GET /api/lifecycle/totp/me/secrets`, which is self-scoped by token. The
root secret never leaves the server, so a leaked USB secret cannot open an uninstall.
`ApprovalSecrets.SecretVersion` (the enrollment timestamp) lets the agent skip a rewrite when
nothing changed and re-provision when an admin re-enrolls a compromised machine.

`LocalApprovalVerifier` is the offline check both stickers call. It enforces the same rules as the
server: ±1 step window, single-use per purpose by matched time step, 8 failures → 15-minute lockout,
and a counter that **decays** — cleared on success, on lockout expiry, and when the last failure is
older than the window, so an attacker cannot park it at 7 and have the employee's next typo lock
them out. TOTP runs on UTC, as RFC 6238 defines; §10.4 is parameterised at the single construction
site if that decision changes.

The security trade this represents — the machine holds material that can generate its own approval
codes — is accepted by §10.3 and spelled out in [08-SECURITY](08-SECURITY.md).

---

## Capture — `Engine.Tracking`

### Time track

`TimeTrackEngine` polls the OS idle timer and emits work/rest transitions with hysteresis.

| Setting | Value |
|---|---|
| Idle threshold | **3 minutes** (spec §5.2) |
| Coverage | Always — no schedule, no business-hours gating |
| PC off / asleep / agent stopped | Rendered as **idle** downstream, not a distinct state |

Idle is computed from `GetLastInputInfo` against `Environment.TickCount`, with unchecked
arithmetic so the 32-bit tick counter wrapping every ~49 days does not produce a false zero.

### Screenshots

`WindowsScreenCapture` enumerates monitors with `EnumDisplayMonitors` and copies each with
`BitBlt`. Every GDI handle is released in a `finally`, including on the failure path — handle
leaks here would exhaust the desktop heap on a long-running machine.

`ScreenshotEngine` encodes to JPEG, binary-searching quality to hit a target byte size.

| Setting | Value |
|---|---|
| Interval | **Admin-configurable per staff** (spec §6.2) |
| Displays | All (spec §6.3) |
| Employee indicator | None (spec §6.4) |

### Browser history

`ChromeHistoryWatcher` reads Chrome's history SQLite database. Chrome holds it open, so the
watcher copies it to a scratch file first.

Requirements that this component must respect, each learned from a real failure:

- The connection string must include `Pooling=False`, or the file descriptor outlives the
  connection and the scratch file cannot be deleted
- The scratch file lives under the agent root, not `%TEMP%`, and is named deterministically per
  profile so it is overwritten rather than accumulated
- Freshness is checked against **both** the main database and its `-wal` sidecar — in WAL mode
  the main file's timestamp does not move until a checkpoint
- Stale scratch files are swept at startup

**Chrome only** (spec §7.1). **Full URLs** are recorded (§7.2).

### Session helper pipe

`SessionHelperPipe` carries 4-byte length-prefixed UTF-8 JSON over a named pipe. Message types
cover liveness (`ping`), captured records (`capture`), and configuration delivery to the helper.

`CapturePipeServer` creates the pipe with an explicit `PipeSecurity` limiting access to `SYSTEM` and
the interactive user, and validates inbound payloads against the event-type allowlist. Both are
load-bearing: without them any local process can inject fabricated telemetry that the service then
signs into the vault as authentic. (On non-Windows the pipe is a Unix socket and access is governed
by the socket file's permissions — there is no `PipeSecurity` to apply.)

---

## The vault — `Engine.Tracking/SecureVault`

Every captured record is appended to an encrypted, hash-chained local store before it is queued
for upload.

**Per record:** Brotli compress → AES-256-GCM encrypt → append an **HMAC-SHA256** chain link over
the previous record's hash plus the record header, nonce, ciphertext and tag. A separate tip file
records the current sequence number and chain hash, so appending is O(1) — no folder scan on the hot
path. Encryption and MAC keys are separate HKDF derivations of the master key.

`Verify` runs on every tick as a cheap tip check, and a full scan hourly.

**Purpose** (spec §12.1): both to hide captured data from the employee and to prove it was not
tampered with.

**Crash consistency:** the tip and the record must not be able to disagree. If a crash lands
between the two writes, startup recovery detects the orphan and repairs rather than failing
verification forever.

> ⚠️ **The local "prove" half still does not hold on its own.** The master key is
> `HKDF(deviceKey ‖ companyTokenKeyBase64)` — both available on the machine, and the employee has
> local admin rights. Anyone who can read the vault can also forge chain links and re-sign the tip.
> A vault integrity report that says "Ok" therefore proves nothing against the stated adversary.

**Server-side chain anchoring now backs it up.** Every record's `vaultSequence` and `chainHash` ride
along in the ingest envelope, and the server takes the first hash it sees for a sequence as an
anchor. Re-sending a sequence under the *same* hash is an ordinary offline replay and passes
silently; re-sending it under a *different* hash writes a row to `agent_chain_breaks`. That catches
the one case a local report structurally cannot — a vault wiped and restarted, whose fresh chain
verifies perfectly against itself.

The event is stored either way (§13.1 never drops), and the marker surfaces at its exact position
through `GET /api/tracking/gaps`. What anchoring does **not** catch — consistently forged forward
chains, data withheld before it is ever sent, altered payloads — is enumerated in
[08-SECURITY](08-SECURITY.md). Per §12.5 the app still labels these markers "data missing", not
"tampering".

---

## Sync — `Engine.Sync`

`FileOutboxQueue` is a directory of JSON files, one per pending event. Filenames are prefixed with a
monotonic timestamp so ordinal sorting yields **FIFO** delivery.

> FIFO is now a quality property, not a correctness one. The server used to reject a rewound
> `vaultSequence` as replay, so out-of-order delivery produced false tamper signals; anchoring
> replaced that watermark, and an out-of-order or late batch is simply stored. §13.1 never drops.

`SyncEngine` probes connectivity, peeks a batch of pending items, posts them to
`/api/ingest/batch`, and moves accepted items aside.

On startup, any record committed to the vault but not confirmed enqueued before a crash is
re-delivered, reusing the original record id so the server deduplicates on
`(UserId, ClientEventId)` rather than double-counting — and so the server's chain anchor sees a
complete sequence rather than a hole.

| Setting | Value |
|---|---|
| Offline buffering | **Never drop anything** (spec §13.1) |
| Cycle | `Agent:SyncSeconds`, default 30 s, floor 5 s |
| Delivered items | Moved to `outbox/sent`, which is capped at 500 files |

> ⚠️ **There is no local expiry.** §13.2's 30 days is a *server* retention window
> (`Retention:AgentEventsDays`); nothing on the agent prunes the pending outbox or the vault, which
> both grow without bound. That is correct for the outbox — §13.1 forbids dropping — but it means a
> long offline period on a screenshot-heavy machine reaches hundreds of megabytes, and the vault
> never shrinks at all. Disk pressure is a real, unmitigated failure mode on the cheap hardware this
> targets.

---

## USB control — `Engine.Usb`

Built as §9 specifies:

1. `WindowsUsbDeviceWatcher` detects an inserted removable device
2. `WindowsDeviceGate` **disables the device node** via SetupDi, so no drive letter is ever
   presented — the stick does not appear in the PC at all (§9.2), rather than merely being
   read-denied
3. `Teamscop.UsbApproval.exe` shows a sticker with a 6-digit input
4. The employee obtains the code from the admin out of band and types it
5. `LocalUsbAccessVerifier` **verifies it locally** — no server call, works offline (§9.6)
6. On success the block lifts for that **specific device**, keyed on device instance identity, until
   it is removed (§9.5). The grant covers every node of a multi-LUN device, so no sibling node is
   left ungated
7. A `usb_event` record goes to the durable outbox and uploads whenever connectivity returns

Device identity is the device instance path, never the drive letter: Windows reassigns letters, so a
quick swap would let an unapproved stick inherit an approval.

A failure of `IDeviceGate` propagates rather than being discarded — an ungated device that reported
itself as blocked was worse than a visible failure.

The block is always on for every machine (§9.4). There is no per-staff toggle.

---

## Uninstall guard

Built as §11 specifies:

1. `Teamscop_setup.exe /uninstall` runs elevated
2. Setup determines whether this is a staff machine from the SCM service and an HKLM marker —
   never from a file the monitored user can delete
3. `Teamscop.UninstallGuard.exe` collects the code
4. The code is verified **locally**, so uninstall works offline
5. On success, removal proceeds and an `uninstall` event is recorded
6. On failure or cancellation, **nothing is removed**

The service configures Windows service recovery (`sc failure`: 5 s / 10 s / 30 s, 24 h reset) so it
restarts itself if stopped or killed (§11.4).

**Lifting the USB block and destroying the offline credential happen only under
`--restore-machine`**, which the uninstaller invokes once removal is actually under way. Doing them
on code entry meant a correct code degraded the machine even when the uninstall was then cancelled.

> The `/cleanup` switch and `FORCE_CLEANUP.txt` are **deleted** (§11.3). Both bypassed the code
> entirely. Zero occurrences remain in the tree.

---

## Configuration delivery

The agent learns its per-staff tracking configuration two ways, and **both work**:

1. **Pull** — `GET /api/tracking/config/me` on its own clock (`Agent:ConfigPullSeconds`, default
   180 s, floor 30 s), deliberately *not* conditioned on the hub being up
2. **Push** — `TrackingConfigUpdated` over the SignalR hub for immediate effect

SignalR is a latency optimisation, not the delivery mechanism; the pull is not nested inside hub
startup, so an unreachable hub does not stop configuration arriving. The same cycle pulls five
snapshots — tracking config, business time, org placement, authorities and the offline approval
secrets — each in its own `try`, so one failing endpoint does not skip the rest. It re-runs on hub
reconnect.

Once received, the service applies the config locally **and forwards it to the session helper over
the pipe** — the helper is the process that actually captures, so a configuration the helper never
sees has no effect.

---

## What the agent must never do

- Capture window titles, keystrokes, or clipboard contents
- Track general application usage
- Read browsers other than Chrome
- Drop buffered data to save space
