# Phase 2 — Agent Lifecycle Policy

## Your rules → engineering mapping

| Your rule | How we implement it | Rejected alternative |
|---|---|---|
| Admin Close ends the app | Separate **AdminHost** desktop process; Close/Exit = full process exit | — |
| Staff never broken / background | **Windows Service** (`TeamscopStaff`) with SCM recovery (restart on failure) | Hidden rootkit / unkillable process |
| Not in Task Manager | **Not implemented.** Services remain visible as normal OS processes | Process cloaking / rootkit |
| Files not findable | No Start Menu/Desktop shortcuts; install under `%ProgramData%\Teamscop\Agent` | File-system cloaking / ADS hiding |
| Cannot pause/finish | Service recovery + boot auto-start; staff has no Close UI | Blocking Task Manager End Task |
| Instant respawn after reboot | Service `StartType=Automatic` (+ delayed-auto optional) | — |
| Uninstall only via Settings → Apps | MSI/AppX ARP entry; no portable delete path supported | — |
| Uninstall needs admin 6-digit TOTP | **UninstallGuard** modal → API verifies **per-staff** TOTP → short-lived uninstall ticket | — |
| USB storage blocked (not HID) | Removable Disks policy + **UsbApproval** sticker; same per-staff TOTP; session-only | USB filter rootkit |

## Hard boundary

Teamscop will **not** ship malware techniques: Task Manager hiding, process cloaking, kernel rootkits, or making End Task impossible. Those break Windows security model, AV/EDR policy, and often local law even for “employer monitoring.”

Enterprise equivalent (what we ship): always-on service, no staff UI, auto-restart, boot start, ARP-visible uninstall gated by admin TOTP.

## Ticket endpoints (threat model)

`POST /api/lifecycle/uninstall/verify|consume` and `POST /api/lifecycle/usb/verify|consume` are **unauthenticated by design**:

- **Verify** proves deviceKey + fresh TOTP and mints a short-lived opaque ticket.
- **Consume** is bearer-of-ticket: whoever holds a valid unused ticket may complete the action once.
- Mitigation: short TTL, single-use consume, TOTP window, rate limits on verify/consume.

Admin desktop UI is Avalonia `Teamscop.App`; `AdminHost` remains the console tool for TOTP/teams/police.

## Process model

```
Admin machine:  Teamscop.AdminHost.exe     (normal UI, Close = quit; per-staff TOTP generator)
Staff machine:  Teamscop.StaffService.exe  (Windows Service, no UI; USB gate)
USB sticker:    Teamscop.UsbApproval.exe   → TOTP → session unlock mass storage
Uninstall:      Teamscop.UninstallGuard.exe → TOTP → allowed uninstall
```

## Stack

- Keep **C# /.NET 8** for service + hosts (fits Auth engine, faster iteration).
- Reserve **C++** for later USB filter / driver work only.
