# 07 — Desktop App

Avalonia. The only user interface — no web, no mobile ([02-REQUIREMENTS](02-REQUIREMENTS.md) §14.6).

One executable, `Teamscop.App.exe`, serves every role. What it shows depends on who is running it.

---

## Window structure

**One main window that adapts to role** (§4.7), built in reconstruction pass 2. `ShellWindow` +
`ShellViewModel` + `ShellCapabilities` replaced five near-duplicate top-level windows — `MainWindow`,
`LeaderWorkspaceWindow`, `PoliceWorkspaceWindow`, `OfficerWorkspaceWindow` and
`NoAccessWorkspaceWindow` — and the router between them. Net −3 742 lines in the app.

Sections are shown or hidden from the caller's effective authorities rather than by picking a
window class.

Two start-up rules, both learned from defects introduced during that refactor:

- **Do not assign the shell to `desktop.MainWindow`.** Avalonia shows it automatically, so a
  workspace window popped on every staff machine at login and only disappeared after a network
  round-trip — i.e. never, on an offline machine.
- **Nothing in the shell constructor may touch hardware or the network.** Deriving the device key to
  pre-fill the login field spawned several hardware queries before the first frame.

Role resolution is asynchronous, with explicit HTTP timeouts, after a window is already on screen.

| Caller | Sees |
|---|---|
| Admin | Everything, company-wide |
| Team leader | Their own team's members and data |
| Policeman | Company-wide, limited to granted packages |
| Leader + policeman | Union of both |
| Plain staff | Sticker only |

---

## Screens

### Login

`LoginView`. Credential: **device key + password** (§3.2). Only leaders, policemen and admins ever
log in — plain staff have no reason to. The device key is pre-filled from the machine
*asynchronously*, so in practice the user types only a password.

The app otherwise stays logged in indefinitely (§3.1). The only thing that returns it here is a
**401** from the API; a 403 means the session is fine and the caller simply is not permitted, so the
shell keeps the session and falls back to cached data. See [05-API](05-API.md).

### Today's overview — home

`TodayOverviewView`, the landing route (§14.1). Fed by a single aggregate,
`GET /api/tracking/overview`, so the cost does not scale with headcount.

Per staff member: state (`working` / `idle` / `offline`), worked and idle seconds, gap seconds, and a
`dataMissing` flag. Rows sort problems-first — offline, then working, then idle. Drill down from here
into an individual.

> Coverage is measured **between the first and last segment the machine reported**, never from
> midnight. Measuring from midnight made every employee read "data missing" every morning — at 09:00
> someone who started at 08:00 showed eight unreported overnight hours — so the Missing tile read
> N of N and a genuine mid-day hole was invisible in the noise.

### Staff list

`StaffDirectoryView`. Machines visible to the caller, from `GET /api/tracking/staff`, scoped by
role. **Self is always excluded** — nobody monitors themselves (§4.5).

Row virtualisation, cursor paging and lazy thumbnails, per §15.1's 20–50 employees.

### Staff detail

Sub-sections, each gated by the caller's authority packages:

| Section | Package | Content |
|---|---|---|
| Summary | any view package | Totals for the selected period |
| Screenshots | `view_screenshot` | Thumbnail gallery, click to enlarge |
| Browsing | `view_browser_history` | Domains, drill into full URLs |
| Time track | `view_timetrack` | Work/idle timeline |
| App history | `uninstall_approval` | Teamscop's own install/uninstall/USB/power-off events |
| Settings | admin | Per-staff tracking configuration |

Every section takes **any staff member and any period, chosen from a calendar** (§14.3).

### Data-gap display

§12.3–12.4: **no push alerts, ever.** Missing data is shown **inline** in the screenshot, browsing
and time-track views, marking the **exact position** of the loss — a visible break in the timeline
labelled with its range, or a gap marker in the gallery between two captures. `DataGapListView`,
`DataGapText` and `DataGapVisuals` render it; the old "chain broken" banner, which reported a problem
without saying where it was, is gone.

**Gaps are server-authoritative.** The app calls `GET /api/tracking/gaps` and renders what it
returns. It must never infer a gap from elapsed time: an earlier version did exactly that and turned
every lunch break and every overnight shutdown into "Data missing". Only the server can tell an agent
that was off (`agent_offline`) from a helper that died (`helper_down`) from an upload that has not
arrived yet (`not_uploaded`) from a rewritten vault (`chain_break`).

> ⚠️ Label these markers **"data missing"**, not "tampering" (§12.5). Server-side chain anchoring now
> exists, and a `chain_break` is genuinely good evidence that something rewrote the vault — but the
> same view carries coverage holes with entirely innocent causes, and anchoring still cannot see a
> consistently forged chain. The accusation would sometimes be wrong, so it is not made.

### Teams board

Create teams, assign a leader, add and remove members. Admin and `team_management` holders only.
Team leaders do **not** manage their own teams (§4.2).

### Settings

- **Business clock** — a **timezone dropdown**, nothing more (§8.4). `TimeZoneCatalog` fills it from
  `GET /api/business-time/zones`; saving is `PUT /api/business-time`. The absolute-anchor UI is gone.
- **Policemen & packages** — promote a staff member and tick which packages they hold.
- **Per-staff tracking** — screenshot interval (30 s – 24 h) and quality, and the three enable flags.

### Codes

For admins and holders of `usb_approval` / `uninstall_approval`. Shows the **current 6-digit
code** for a chosen staff member and purpose, with a countdown.

This is the mechanism by which an admin obtains a code to relay to an employee by phone or
Telegram (§10.1–10.2). Staff never see their own code here.

### Leaderboard

`LeaderboardView` — a ranking of staff by hours worked over a chosen period (§14.2), gated on
`view_timetrack`. Rank is a true company rank: the server orders every visible staff member and cuts
the page afterwards.

---

## The staff sticker

A small always-on-top window on every staff PC. It cannot be closed.

**Its purpose is to be proof that the tracking engine is running correctly** (§14.4) — a health
indicator, not decoration. It reads the locally written `agent-health.json`, and falls back to
`GET /api/tracking/health/me` when that is unavailable. Status is one of `protected`,
`catching_up` (more than 50 events still queued), `not_reporting` (no heartbeat for 2 minutes, or the
helper is down, or tracking tasks are unhealthy) or `unknown` (no heartbeat yet).

The health endpoint is self-scoped and carries **liveness only, no captured data**, which is why
§4.5's self-monitoring ban does not apply to it. The same is true of the sticker's own rolling
timetrack.

Leaders and policemen are monitored staff too. Closing their workspace window returns them to
the sticker rather than exiting the app — **this is intended** (§4.6).

---

## Live updates

The app holds a SignalR connection to `/hubs/config` and reacts to:

| Event | Group it arrives on | Effect |
|---|---|---|
| `AuthoritiesUpdated` | `staff:{ownId}` | The caller's own rights changed |
| `OrgStructureUpdated` | `company:{id}:mgmt` | Refresh the teams board and staff list |
| `PolicemenUpdated` | `company:{id}:mgmt` | Refresh the policemen settings |

The hub has **three** groups, not two — every connection joins `company:{id}` as well, which is how
the *agent* receives `BusinessTimeUpdated`. Only admins and `team_management` holders join
`company:{id}:mgmt`, so the org chart and policeman roster never reach an ordinary employee's
connection. See [05-API](05-API.md).

`AuthorityState` treats authorities (from `/api/police/me`) and placement (from `/api/org/me`) as
mutable for the life of the session: both can change mid-session over SignalR, and the adaptive shell
re-evaluates what it shows rather than requiring a restart.

---

## UI conventions

- **No exports.** No CSV, PDF, or file save (§14.5).
- **No notifications or toasts for problems.** Problems appear inline in the data (§12.3).
- Every list that can hold 20–50 staff or a day of screenshots must paginate or virtualise.
- Long-running work never blocks the UI thread; failures surface visibly rather than being
  swallowed into an empty screen.
- All times displayed in **company time** (§8.2).
