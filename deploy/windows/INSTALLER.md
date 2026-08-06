# Windows Installer Contract (Staff + Admin)

## Products

| Package | Role | Close behavior | Process model |
|---|---|---|---|
| Teamscop Admin | Admin | Close quits process | Desktop app (`Teamscop.App` / `Teamscop.AdminHost`) — **no tracking service** |
| Teamscop Staff Agent | Staff (plain / leader / police) | App UI always live (workspace or sticker); Service has no Close UI | Windows Service `TeamscopStaff` + SessionHelper + App |

## Staff process model (always-on)

```
Boot → SCM TeamscopStaff (auto + failure restart/5s)
         ├─ vault / outbox / heartbeat / USB policy / SessionHelper pipe
Logon → Teamscop.SessionHelper.exe (Run key) — capture in user session
      → Teamscop.App.exe (Run key) — sticker or leader/police workspace
```

**App running** = recent heartbeat + tracking healthy + staff UI live.

## Install now (PowerShell)

From a Windows admin shell, after cloning the repo:

```powershell
cd deploy\windows
.\publish-staff.ps1
.\install-staff.ps1 -SourceDir ..\..\artifacts\staff-agent -ApiBaseUrl https://teamscop.com
```

Uninstall (TOTP via UninstallGuard when present):

```powershell
.\uninstall-staff.ps1
```

Scripts:

| File | Role |
|------|------|
| [`publish-staff.ps1`](publish-staff.ps1) | `dotnet publish` staff binaries → `artifacts/staff-agent` |
| [`install-staff.ps1`](install-staff.ps1) | Copy → `%ProgramData%\Teamscop\Agent`, `sc create/failure/start`, Run-at-logon |
| [`uninstall-staff.ps1`](uninstall-staff.ps1) | Guard → stop/delete service → remove Run keys + files |
| [`ServiceInstallerHints.ps1`](ServiceInstallerHints.ps1) | SCM contract (mirrors C# `ServiceInstallerHints`) |
| [`wix/Teamscop.StaffAgent.wxs`](wix/Teamscop.StaffAgent.wxs) | WiX 4 MSI source (build on Windows/CI) |

## Staff install layout

- Files: `%ProgramData%\Teamscop\Agent\`
- Includes: `Teamscop.StaffService.exe`, `Teamscop.SessionHelper.exe`, `Teamscop.App.exe`, `Teamscop.UsbApproval.exe`, `Teamscop.UninstallGuard.exe`
- No Desktop / Start Menu shortcuts
- ARP / Uninstall registry: **Teamscop Staff Agent**
- Service: Automatic start + failure restart (`sc failure ... actions= restart/5000/...`)

## Uninstall gate (Staff)

1. User opens **Settings → Apps → Teamscop Staff Agent → Uninstall** (or `uninstall-staff.ps1`)
2. `Teamscop.UninstallGuard.exe` prompts for admin **6-digit TOTP**
3. Guard calls `POST /api/lifecycle/uninstall/verify` then ticket consume
4. Scripts/MSI stop+delete service and remove files

## USB mass-storage gate (Staff)

- On service start: Removable Disks Deny_Read/Write/Execute policy
- On USB storage insert: launch `Teamscop.UsbApproval.exe` → TOTP → session unlock
- On removal: re-apply deny

## Windows smoke checklist

1. `publish-staff.ps1` then `install-staff.ps1` as Administrator
2. Reboot → `TeamscopStaff` running; SessionHelper + App start at logon
3. Kill `Teamscop.StaffService` → SCM restarts within ~5s; heartbeat resumes
4. Plain staff → sticker only (no close); leader close workspace → sticker
5. Offline: outbox grows; online: flush; chain banner on break in tracking panels

## Hard boundary

Do **not** add Task Manager hiding, process cloaking, or file-system rootkits in the installer.
