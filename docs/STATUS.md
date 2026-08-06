# Teamscop — Project Status

Living matrix of what exists. Values: **Spec** · **Engine** · **API** · **App** · **Tests**.  
“App” means Avalonia `Teamscop.App` unless noted.

| Area | Spec | Engine | API | App | Tests | Notes |
|------|------|--------|-----|-----|-------|-------|
| Phase 1 Auth | [PHASE1](PHASE1_AUTH.md) | Yes | Yes | Register / Join live | Yes | Login API exists; App login screen **deferred** |
| Phase 2 Lifecycle | [PHASE2](PHASE2_LIFECYCLE.md) | Yes | Yes | Staff UI always live | Yes | Service + SessionHelper + PS1/WiX installer |
| Phase 3 Sync | [PHASE3](PHASE3_SYNC.md) | Yes | Ingest | — | Yes | Outbox → `/api/ingest/batch`; heartbeat health fields |
| Phase 4 Tracking | [PHASE4](PHASE4_TRACKING.md) | Yes | Yes | Viewers + chain banner | Yes | Self sticker; chain API; session helper pipe |
| Phase 5 Business time | [PHASE5](PHASE5_BUSINESS_TIME.md) | Yes | Yes | Settings + company display | Yes | Company-wide stamp + admin UI; SignalR JWT wired |
| Phase 6 USB | [PHASE6](PHASE6_USB.md) | Yes | Yes | — | Yes | Block + TOTP session; Codes in police App; USB sticker console |
| Phase 7 Teams | [PHASE7](PHASE7_TEAMS.md) | Lifecycle client | Yes | Teams board live | Yes | Admin + `team_management`; leaders get scoped views |
| Phase 8 Authorities | [PHASE8](PHASE8_AUTHORITIES.md) | Client receive | Yes | Admin Settings + role workspaces | Yes | Self not monitorable; close → sticker |
| Phase 9 App history | [PHASE9](PHASE9_APP_HISTORY.md) | Allowlist + watchdog + power_off | registration/uninstall emit; others ingest | App history list | Yes | Per-type query; partial packages tolerated |
| Avalonia admin shell | [UI preview](UI_DEV_PREVIEW.md) | — | — | Shell + Teams + Settings | — | Leaderboard **deferred** (nav hidden) |
| Staff detail viewers | PHASE4/9 | — | Events + query/media APIs | All major tabs live | Partial | Demo seed: `deploy/seed-fake-tracking.py` |
| Windows staff installer | [INSTALLER](../deploy/windows/INSTALLER.md) | — | — | — | — | `publish-staff.ps1` + `install-staff.ps1` + WiX |
| GitHub E2E CI/CD | [CI_CD_E2E](CI_CD_E2E.md) | — | — | — | Yes | `.github/workflows/e2e.yml` — Linux tests, Windows agent/UI, live-smoke |

## Admin App (Avalonia) feature notes

| Feature | State |
|---------|--------|
| Register / Join business | Live |
| Login screen | **Deferred** (API `POST /api/auth/login` + Engine client exist) |
| Session resume (admin / staff) | Live |
| Staffs expand list | Live (scoped by role/packages; **self excluded**) |
| Staff sub-sidebar | Summary / Screenshot / Browsing / Time Track / App history / Settings — gated by packages |
| Chain / online banner | Live on Screenshot / Time Track / Browsing when unhealthy |
| Teams board | Live for admin and `team_management` policemen |
| Settings — business clock | Live + SignalR |
| Settings — policemen & packages | Live + SignalR |
| Team leader workspace | Live (`LeaderWorkspaceWindow`; Close → sticker) |
| Policeman workspace | Live (`PoliceWorkspaceWindow`; Close → sticker) |
| Leader + Policeman | Live merged (`OfficerWorkspaceWindow`) |
| Codes (USB/uninstall TOTP) | Live for `usb_approval` / `uninstall_approval` packages |
| Staff timetrack sticker | Live (`TimeTrackStickerWindow` — last 24h bar, movable, no close) |
| Leaderboard | **Deferred** (nav hidden until productized) |
| TOTP enroll UI | AdminHost / admin only |
| USB approval sticker | Staff console sticker (not Avalonia) |

## Deploy (this VPS)

- API: `/opt/teamscop/api`, systemd `teamscop-api`, nginx → `https://teamscop.com`
- Avatars: `/var/lib/teamscop/avatars`
- UI preview: [`UI_DEV_PREVIEW.md`](UI_DEV_PREVIEW.md)

## Related hosts

| Host | Role |
|------|------|
| `Teamscop.App` | Avalonia admin / leader / police / staff sticker |
| `Teamscop.AdminHost` | Console admin (TOTP, teams, police) |
| `Teamscop.StaffService` | Windows Service — vault/sync/heartbeat/USB/pipe |
| `Teamscop.SessionHelper` | User-session capture → named pipe |
| `UsbApproval` / `UninstallGuard` | TOTP stickers |
