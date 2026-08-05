# Windows Installer Contract (Staff + Admin)

## Products

| Package | Role | Close behavior | Process model |
|---|---|---|---|
| Teamscop Admin | Admin | Close quits process | Desktop app (`Teamscop.AdminHost`) |
| Teamscop Staff Agent | Staff | No Close UI | Windows Service `TeamscopStaff` |

## Staff install layout

- Files: `%ProgramData%\Teamscop\Agent\`
- No Desktop / Start Menu shortcuts
- ARP / Settings → Apps entry: **Teamscop Staff Agent** (required uninstall path)
- Service: Automatic start + failure restart (`sc failure ... actions= restart/5000/...`)

## Uninstall gate (Staff)

1. User opens **Settings → Apps → Teamscop Staff Agent → Uninstall**
2. MSI runs custom action: `Teamscop.UninstallGuard.exe`
3. Guard prompts for admin **6-digit TOTP**
4. Guard calls `POST /api/lifecycle/uninstall/verify`
5. On success, writes ticket and exits 0; MSI continues (`sc stop/delete`, remove files)
6. MSI/custom action calls `POST /api/lifecycle/uninstall/consume` with ticket

Admin enrolls **per-staff** TOTP via AdminHost `enroll-totp <staffId>` or `POST /api/lifecycle/totp/enroll` with `{ staffUserId }`. Same secret covers USB approve + uninstall. Generate live codes with `code <staffId>`.

## USB mass-storage gate (Staff)

- On service start: Removable Disks Deny_Read/Write/Execute policy (mouse/keyboard unaffected)
- On USB storage insert: launch `Teamscop.UsbApproval.exe` sticker → TOTP → session unlock
- On removal: re-apply deny (next insert needs a new code)

## Hard boundary

Do **not** add Task Manager hiding, process cloaking, or file-system rootkits in the installer.
