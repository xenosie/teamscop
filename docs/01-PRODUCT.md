# 01 — Product

## What Teamscop is

Teamscop monitors employees' Windows PCs for a small company. It continuously records:

- **Work hours** — a work/idle timeline derived from keyboard and mouse activity
- **Screenshots** — periodic captures of every display, as proof of work
- **Browsing history** — full URLs visited in Chrome
- **Its own lifecycle** — installs, uninstalls, power-off events, USB approvals

It also **controls USB removable storage**: sticks are blocked until an employee enters a
6-digit code that only an administrator can produce.

The people who review this data use a Windows desktop app. There is no web interface and no
mobile app.

## Who uses it

| Person | What they do |
|---|---|
| **Admin** | Owns the company. Sees everything, configures tracking per employee, holds all TOTP credentials, issues approval codes. |
| **Team leader** | A staff member who leads a team. Sees tracking data for **their own team's members only**. |
| **Policeman** | A staff member granted **company-wide** viewing or approval rights. The admin chooses exactly which rights via authority packages. |
| **Staff** | A monitored employee. Sees only their own sticker. |

Leaders and policemen are themselves monitored staff. Nobody can monitor themselves.

## Scale

- **20–50 employees** per company
- Cheap, low-end Windows 10/11 PCs — the agent must be light on CPU and RAM
- Data expires after **30 days**

## Core concepts

### Device-bound identity

An account is a **machine**, not a person. Identity is a `deviceKey` derived from the PC's
hardware serials. There are no email addresses. Consequences, all deliberate:

- One employee with two PCs appears as two accounts
- Replacing a motherboard creates a new account; old history stays under the old one
- Two people sharing a PC produce merged data under one account

### Company token

A company is created by an admin signing up. That produces an offline, encrypted
**company token** (`TS1.…`) which staff paste when they install the agent. It is permanent and
reusable — no expiry, no rotation, no per-employee invites.

### Authority packages

Rather than fixed roles, viewing and approval rights are granted as named packages:

| Package | Grants |
|---|---|
| `view_screenshot` | See screenshot data |
| `view_timetrack` | See work-hour data |
| `view_browser_history` | See browsing data |
| `usb_approval` | Read USB approval codes |
| `uninstall_approval` | Read uninstall approval codes |
| `team_management` | Create and edit teams |

An admin holds all packages implicitly. A team leader gets the three viewing packages
automatically but scoped to their own team. A policeman gets whatever the admin grants,
company-wide.

### Business clock

Every company has **one timezone**. All data is displayed in company time, always, so that
every employee's day lines up on the same timeline. Employees are not in different timezones.

### The staff sticker

A small always-on-top window on every staff PC. Its purpose is to be **proof that the tracking
engine is running correctly** — a health indicator, not decoration. It cannot be closed.

### TOTP approval codes

USB access and uninstall both require a 6-digit code. Critically:

- **Staff never know their own credential.** The admin holds all TOTP secrets.
- The admin generates the code and sends it to the employee **out of band** — phone, Telegram,
  anything that is not this app.
- The agent verifies the code **locally**, so it works with no internet connection. This means the
  machine holds a derived secret; that trade is accepted (§10.3) and spelled out in
  [08-SECURITY](08-SECURITY.md).
- USB and uninstall codes come from **separate derived streams**, so a USB code can never open an
  uninstall.

## What Teamscop deliberately does not do

These are decisions, not gaps. Do not implement them.

- **No employee lifecycle.** Nobody is disabled, deleted, or offboarded.
- **No application-usage tracking.** "App history" means Teamscop's own install/uninstall/USB
  events, not which programs an employee runs.
- **No window titles, no keystroke logging, no clipboard capture.**
- **No browsers other than Chrome.**
- **No exports, reports, CSV or PDF.** Everything is viewed in the app.
- **No web dashboard, no mobile app.**
- **No push alerts or notifications.** Problems are visible inline in the data views instead.

## Undecided

The following were explicitly deferred and have no answer yet:

- Product positioning and competitive framing
- Legal and consent model — whether monitoring is disclosed to employees, which jurisdictions
  apply, whether GDPR obligations exist
- Hosting model — whether this is SaaS on teamscop.com or self-hosted per customer
- Licensing, pricing, and seat enforcement

These affect architecture. In particular, the hosting question determines whether the ~25
hardcoded `https://teamscop.com` references in the agent are correct or a defect.

## Glossary

| Term | Meaning |
|---|---|
| **Agent** | The software on a staff PC: the Windows service plus session helper |
| **Chain** | Hash-linked sequence over vault records. Verified locally by the agent, and **anchored server-side** so a wiped-and-restarted vault is detectable ([08-SECURITY](08-SECURITY.md)) |
| **Company token** | Encrypted `TS1.…` string that lets a machine join a company |
| **Device key** | Hardware-derived identifier that acts as the account identity |
| **Outbox** | On-disk queue of events waiting to upload |
| **Policeman** | Staff member with admin-granted company-wide rights |
| **Sticker** | Small always-on-top window (timetrack health, USB approval, uninstall approval) |
| **Vault** | Encrypted, hash-chained local store of captured data before upload |
