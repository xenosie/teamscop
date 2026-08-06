# Phase 8 — Authority Packages & Policemen

## Locked rules

| Rule | Behavior |
|---|---|
| Packages | Individually assigned: screenshot / timetrack / browser / USB approval / uninstall approval / team management |
| Team Leader | Inherent `view_screenshot` / `view_timetrack` / `view_browser_history` for **led team members only** (live with `OrgStructureUpdated`). Never auto USB / uninstall / App history |
| Policeman | Any staff; may also be leader or member |
| Scope | Policeman packages apply to **whole company** staff |
| USB / Uninstall package | Generate 6-digit TOTP codes for any staff (`GET /api/lifecycle/totp/code/{id}`) |
| TOTP enroll | Admin only |
| Immediate | `AuthoritiesUpdated` (staff) + `PolicemenUpdated` (company) via SignalR |

## Package IDs

| Id | Label |
|---|---|
| `view_screenshot` | Screenshot view |
| `view_timetrack` | Timetrack view |
| `view_browser_history` | Browsing history view |
| `usb_approval` | USB approval (generate TOTP) + view `usb_event` App history |
| `uninstall_approval` | Uninstall approval (generate TOTP) + view lifecycle App history (`registration` / `power_off` / `uninstall` / `app_broken`) |
| `team_management` | Team management |

Admin implicitly has all packages.

## API

| Method | Path | Who |
|---|---|---|
| GET | `/api/police/packages` | Auth |
| GET | `/api/police/me` | Auth — effective packages |
| GET | `/api/police` | Admin — list policemen |
| PUT | `/api/police/{staffUserId}` | Admin — `{ packages: string[] }` |
| DELETE | `/api/police/{staffUserId}` | Admin — revoke |

Tracking/TOTP/teams endpoints enforce packages via `IAuthorityService`.

## Admin UI

Avalonia shells:

| Role | Window |
|---|---|
| Admin | `MainWindow` — company Settings (business clock + policemen), Teams, full staff monitoring |
| Team leader | `LeaderWorkspaceWindow` — **My team** only; Summary / Screenshot / Browsing / Time Track |
| Policeman | `PoliceWorkspaceWindow` — **Company staff**; package-gated tabs; **Codes** when `usb_approval` / `uninstall_approval` (generate 6-digit codes for staff); Teams if `team_management` |
| Leader + Policeman | `OfficerWorkspaceWindow` — merged chrome and nav |
| Plain staff | `TimeTrackStickerWindow` only — movable last-24h work/rest bar (no workspace, no numbers, no close). Self-read of own timetrack allowed without `view_timetrack`. |
| Leader/Police Close | Workspace Close → same sticker (process stays alive). |
| Self monitoring | Leaders/police **cannot** list or open their own tracking data (`CanViewStaff` denies self). |

Settings → Policemen list refreshes on `PolicemenUpdated`. Staff agents receive `AuthoritiesUpdated` (log/cache).

## AdminHost

`packages` · `police` · `police-set <staffId> <pkg…>` · `police-revoke <staffId>`
