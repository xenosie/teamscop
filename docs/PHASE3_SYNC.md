# Phase 3 — Connectivity + Durable Sync

## Goal

Staff agents keep collecting events offline and push them when the API is reachable.

## Components

| Piece | Responsibility |
|---|---|
| `ConnectivityProbe` | GET `/health` — internet/API reachability + latency |
| `FileOutboxQueue` | Durable pending/sent JSON files under agent data dir |
| `SyncEngine` | Probe → enqueue connectivity → flush batch |
| `POST /api/ingest/batch` | Auth’d batch ingest with `clientEventId` idempotency |
| `StaffAgentWorker` | Runs sync loop every `Agent:SyncSeconds` |

## Flow

```
probe /health
  → enqueue connectivity event
  → enqueue heartbeat (helperAlive, trackingOk, pendingOutbox, vaultTipSequence)
  → if API reachable: POST /api/lifecycle/heartbeat + POST /api/ingest/batch
  → ack accepted + duplicate ids locally
```

Offline: outbox + vault accumulate on disk. Online: flush preserves vault sequence / chain hash so server gap detection matches local chain.

Heartbeat is the liveness signal: missing heartbeat ⇒ agent offline in chain health banners.

## Event types (extensible)

`heartbeat`, `connectivity`, `timetrack`, `screenshot_meta`, `browser_history`, `usb_event`, `vault_alert`, `app_broken`, `power_off`

Future capability modules only enqueue into the same outbox.
