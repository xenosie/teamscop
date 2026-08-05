# Phase 8 — Authority Packages & Policemen

## Locked rules

| Rule | Behavior |
|---|---|
| Packages | Individually assigned: screenshot / timetrack / browser / USB approval / uninstall approval / team management |
| Team Leader | **No** automatic tracking visibility (packages-only) |
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
| `usb_approval` | USB approval (generate TOTP) |
| `uninstall_approval` | Uninstall approval (generate TOTP) |
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

## AdminHost

`packages` · `police` · `police-set <staffId> <pkg…>` · `police-revoke <staffId>`
