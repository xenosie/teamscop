# Phase 9 — App History (lifecycle events)

Staff **App history** in the admin UI lists discrete lifecycle events for one staff member (not screenshots / browsing / time track).

## Event types (locked)

| Type | Meaning | Primary emitter |
|------|---------|-----------------|
| `registration` | Staff account created / first joined company | API on staff signup |
| `power_off` | Machine/session power-off or clean agent stop | Staff agent on orderly stop (`service_stop`) |
| `usb_event` | Removable storage session / block events | Existing USB + ingest (PHASE6) |
| `uninstall` | Uninstall ticket successfully consumed | API on uninstall consume |
| `app_broken` | Critical agent files removed/tampered under install root | Staff agent `AppBrokenWatchdog` |

## Storage

- Persist as `agent_events` rows (same table as tracking ingest).
- Server-side emitters (registration, uninstall) write directly; agents use `/api/ingest/batch` allowlist.
- Admin UI: `GET /api/tracking/events?staffUserId=&eventType=` (filtered by PHASE8 packages).
- Package map: `usb_event` → `usb_approval`; `registration` / `power_off` / `uninstall` / `app_broken` → `uninstall_approval`. Client loads types independently so partial packages still return allowed rows.

## Payloads (sketch)

```json
{ "kind": "registration", "username": "…", "deviceKeyPrefix": "…" }
{ "kind": "uninstall", "ticketId": "…", "consumedAt": "…" }
{ "kind": "power_off", "reason": "service_stop|shutdown" }
{ "kind": "app_broken", "missing": ["Teamscop.StaffService.exe"], "installRoot": "…" }
```

`usb_event` payloads follow PHASE6 / existing agent shape.

## Power off (shipped — clean stop)

On orderly `StaffAgentWorker` exit, `PowerOffEmitter` enqueues `power_off` with `{ kind, reason: "service_stop" }` and best-effort flushes the outbox (short timeout). Crash paths do not emit. `reason: "shutdown"` reserved for future SCM/session hooks.

## App broken (shipped — agent watchdog)

`Teamscop.Engine.Sync.AppBrokenWatchdog` runs each staff loop tick against `AppContext.BaseDirectory`:

- Required files: StaffService host + engine DLLs (see `DefaultRequiredRelativePaths`)
- Detects **missing** paths and **SHA-256 drift** vs baseline captured when healthy
- Emits `app_broken` **once per distinct incident fingerprint** (debounced until healthy again)
- Payload: `{ kind, missing[], altered[], installRoot }`

## UI contract

Staff → App history (Avalonia): chronological list (time + title + short detail) via `TrackingApiClient.QueryAppHistoryAsync`. Loading / empty / error states. No fake seed data.

## See also

- [PHASE3_SYNC.md](PHASE3_SYNC.md) ingest
- [PHASE6_USB.md](PHASE6_USB.md)
- [PHASE8_AUTHORITIES.md](PHASE8_AUTHORITIES.md) who may view events
- [STATUS.md](STATUS.md)
