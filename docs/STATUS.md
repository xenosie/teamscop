# Teamscop — Project Status

Living matrix of what exists. Values: **Spec** · **Engine** · **API** · **App** · **Tests**.  
“App” means Avalonia `Teamscop.App` unless noted.

| Area | Spec | Engine | API | App | Tests | Notes |
|------|------|--------|-----|-----|-------|-------|
| Phase 1 Auth | [PHASE1](PHASE1_AUTH.md) | Yes | Yes | Auth UI live | Yes | deviceKey identity |
| Phase 2 Lifecycle | [PHASE2](PHASE2_LIFECYCLE.md) | Yes | Yes | — | Yes | AdminHost console; StaffService |
| Phase 3 Sync | [PHASE3](PHASE3_SYNC.md) | Yes | Ingest | — | Yes | Outbox → `/api/ingest/batch` |
| Phase 4 Tracking | [PHASE4](PHASE4_TRACKING.md) | Yes | Yes | Staff tabs stub | Yes | Vault + screenshot/time/Chrome |
| Phase 5 Business time | [PHASE5](PHASE5_BUSINESS_TIME.md) | Yes | Yes | — | Yes | No App UI yet |
| Phase 6 USB | [PHASE6](PHASE6_USB.md) | Yes | Yes | — | Yes | Block + TOTP session |
| Phase 7 Teams | [PHASE7](PHASE7_TEAMS.md) | Lifecycle client | Yes | Teams board live | Yes | Nullable leader; clear/switch tested |
| Phase 8 Authorities | [PHASE8](PHASE8_AUTHORITIES.md) | Client | Yes | Toolbar stub | Yes | Packages / policemen |
| Phase 9 App history | [PHASE9](PHASE9_APP_HISTORY.md) | Allowlist + watchdog + power_off | Emit registration/uninstall/power_off | App history list | Yes | `app_broken` via StaffService tick |
| Avalonia admin shell | [UI preview](UI_DEV_PREVIEW.md) | — | — | Shell + Teams | — | Leaderboard/Settings empty |
| Staff detail viewers | PHASE4/9 | — | Events API | App history live; others stub | Partial | Screenshot/Browsing/Time still stub |

## Admin App (Avalonia) feature notes

| Feature | State |
|---------|--------|
| Register / Join business | Live |
| Session resume (admin) | Live |
| Staffs expand list | Live |
| Staff sub-sidebar (5 items) | Nav live; App history list live |
| Teams board | Live (add/switch leader, members, pool modal) |
| Leaderboard / Settings | Nav only |
| Police management | Not in App (AdminHost CLI) |

## Deploy (this VPS)

- API: `/opt/teamscop/api`, systemd `teamscop-api`, nginx → `https://teamscop.com`
- Avatars: `/var/lib/teamscop/avatars`
- UI preview: [`UI_DEV_PREVIEW.md`](UI_DEV_PREVIEW.md)

## Related hosts

| Host | Role |
|------|------|
| `Teamscop.App` | Avalonia admin desktop |
| `Teamscop.AdminHost` | Console admin (TOTP, teams, police) |
| `Teamscop.StaffService` | Windows Service agent |
| `UsbApproval` / `UninstallGuard` | TOTP stickers |
