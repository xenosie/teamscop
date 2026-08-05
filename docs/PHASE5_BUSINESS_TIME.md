# Phase 5 — Business Internal Timezone

## Goal

One synchronized company timeline for timetrack and the tracking data chain.

## Admin declare

`POST /api/business-time/declare` (Admin Bearer):

```json
{
  "timeZoneId": "UTC+03:00",
  "year": 2026,
  "month": 8,
  "day": 5,
  "hour": 14,
  "minute": 30,
  "second": 0
}
```

Server stores:

- `BusinessTimeZoneId`
- `AnchorUtc = now (UTC)`
- absolute business local components above
- increments `BusinessClockVersion`

## Immediate fan-out

SignalR hub `/hubs/config` event `BusinessTimeUpdated` → company group `company:{id}`.

All connected staff apply the new clock instantly. Offline staff pull `GET /api/business-time/me` on reconnect.

## Agent formula

```
businessLocal = anchorLocal + (utcNow - anchorUtc)
```

All staff with the same config produce identical business timestamps.

## Data chain stamping

Every outbox/vault tracking envelope includes:

- `occurredAtUtc`
- `businessLocal`
- `businessTimeZoneId`
- `businessClockVersion`
- `businessSynchronized`

Ingest persists `BusinessOccurredAt` for gap-free company-local ordering.
