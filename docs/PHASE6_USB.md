# Phase 6 — USB Mass Storage Gate

## Rules

| Rule | Implementation |
|---|---|
| Block USB only | Windows Removable Storage Access policy for **Removable Disks** class `{53f5630d-…}` |
| Do not block mouse/keyboard | HID class untouched; USB host controllers stay enabled |
| Do not damage USB | Reversible registry policy only (Deny_Read/Write/Execute = 1 → 0) |
| Sticker on insert | `Teamscop.UsbApproval` helper prompted by staff agent |
| Admin code | Per-staff TOTP (same secret for USB approve + uninstall) |
| Unlock duration | **This insert/session only** — re-block on removal; re-prompt next plug-in |

## Flow

```
Staff PC boots → StaffService applies Removable Disks Deny_* policy
User inserts USB flash/external disk
  → watcher sees DriveType.Removable
  → sticker: enter 6-digit code
AdminHost: `code <staffId>` (or authenticator) → reads current TOTP
Staff enters code → POST /api/lifecycle/usb/verify
  → agent consumes ticket → LiftBlock() for this session
User removes USB → ApplyBlock() again
```

## Admin TOTP (per staff)

```
POST /api/lifecycle/totp/enroll          { staffUserId }   (Admin only)
GET  /api/lifecycle/totp/staff                             (Admin, or usb/uninstall package)
GET  /api/lifecycle/totp/status/{staffUserId}              (Admin, or usb/uninstall package)
GET  /api/lifecycle/totp/code/{staffUserId}                (Admin, or usb_approval / uninstall_approval — PHASE8)
POST /api/lifecycle/usb/verify           { deviceKey, totpCode, deviceInstanceId? }
POST /api/lifecycle/usb/consume          { usbSessionTicket }
POST /api/lifecycle/uninstall/verify     { deviceKey, totpCode }   (same secret)
```

Avalonia App TOTP UI is **deferred** — use AdminHost: `staff` | `enroll-totp <id>` | `code <id>`

## Hard boundary

No USB filter driver / rootkit in this phase. Policy-based PC-side block only.
