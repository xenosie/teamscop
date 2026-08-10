# 02 — Requirements & Decisions

**This is the source of truth.** Where the code disagrees with this document, the code is wrong.

Confirmed by the owner on 2026-08-07. Items marked ⚠️ are unresolved.

---

## 1. Identity & devices

| # | Decision |
|---|---|
| 1.1 | Identity is **device-bound** — a hardware-derived `deviceKey`. One machine = one account. |
| 1.2 | No email addresses, no person-level accounts. |
| 1.3 | Hardware change (motherboard, disk, VM migration) → treat as a **brand-new machine**. Old history stays under the old account. No re-binding flow. |
| 1.4 | One employee with two PCs = two separate accounts. Acceptable; do not merge. |
| 1.5 | Two employees sharing one PC is rare. Merged data under one account is acceptable. |
| 1.6 | The staff password exists **only** so leaders and policemen can log into the desktop app. Plain staff never log in anywhere. |
| 1.7 | **No employee lifecycle.** Nobody is disabled, deleted, or offboarded. Do not build disable/enable/delete, revocation, approval queues, or archival. |

## 2. Enrollment

| # | Decision |
|---|---|
| 2.1 | The **employee self-installs** the agent. Therefore employees have local administrator rights, and this caps achievable tamper resistance. Accepted. |
| 2.2 | The company token is **permanent and reusable**. No expiry, no rotation, no per-employee invites. |
| 2.3 | A newly enrolled machine **starts reporting immediately**. No admin approval step. |

## 3. Sessions & login

| # | Decision |
|---|---|
| 3.1 | The desktop app stays logged in **indefinitely**. No session expiry, no idle lock. |
| 3.2 | A **login screen is required** and does not exist today. Credential: **device key + password**. |
| 3.3 | No token revocation, no forced logout, no password reset. |

## 4. Roles & permissions

| # | Decision |
|---|---|
| 4.1 | **Admin** — everything, company-wide. Owns all TOTP credentials. |
| 4.2 | **Team leader** — sees their own team's tracking data only. No team management. No approval rights. |
| 4.3 | **Policeman** — company-wide scope; the admin picks which data types via authority packages. |
| 4.4 | **Staff** — own sticker only. |
| 4.5 | Nobody can monitor themselves. The only exception is a staff member's own timetrack, which feeds their sticker. |
| 4.6 | Leaders and policemen are monitored staff. Closing their workspace window returns them to the staff sticker — **intended behaviour**. |
| 4.7 | **UI structure: one main window that adapts to role.** Replace the five duplicated top-level windows (Main / Leader / Police / Officer / NoAccess). |

## 5. Time tracking

| # | Decision |
|---|---|
| 5.1 | "Working" = keyboard or mouse input activity. Nothing else. |
| 5.2 | Idle threshold: **3 minutes**. |
| 5.3 | Tracking runs **always** — evenings, weekends, holidays included. No schedules. |
| 5.4 | PC off, asleep, or agent not running → rendered as **idle** in the timeline. Not a distinct state. |

## 6. Screenshots

| # | Decision |
|---|---|
| 6.1 | Purpose: **proof of work**. |
| 6.2 | Capture interval is **admin-configurable per staff member**. The per-staff config channel is therefore a core feature, not optional. |
| 6.3 | **All displays** are captured each cycle. |
| 6.4 | No capture indicator for the employee. No employee self-view. |

## 7. Browsing history

| # | Decision |
|---|---|
| 7.1 | **Chrome only.** Edge and Firefox are out of scope. |
| 7.2 | **Full URLs** are recorded and shown. |
| 7.3 | No window titles. No application-usage tracking. |
| 7.4 | "App history" means **Teamscop's own** lifecycle events — install, uninstall, USB, power-off. It is built as intended and is not a gap. |

## 8. Business clock

| # | Decision |
|---|---|
| 8.1 | Purpose: **one shared company timeline** so all staff data is comparable. |
| 8.2 | All data displayed in **company time, always**. Never employee-local, never viewer-local. |
| 8.3 | One timezone per company. Multi-timezone companies are not a scenario. |
| 8.4 | **Remove the absolute anchor.** The admin picks a timezone from a dropdown — nothing more. Delete the anchor columns and the clock-synchronisation concept. |

## 9. USB control

| # | Decision |
|---|---|
| 9.1 | Threat being defended against: **data theft**. |
| 9.2 | On insertion the device **must not appear in the PC at all**. Block at mount/device level, not merely deny read/write. |
| 9.3 | A small sticker with a 6-digit input appears on insertion. Correct code → USB becomes usable. |
| 9.4 | The block is **on for everyone, always**. No per-staff toggle. |
| 9.5 | Approval lasts **until that stick is removed**. This requires real device identity — **not the drive letter**. |
| 9.6 | Codes **must verify offline**. The agent validates locally with no server call. |

## 10. Approval codes (TOTP)

| # | Decision |
|---|---|
| 10.1 | **Staff never know their own credential.** The admin owns all TOTP secrets. |
| 10.2 | The admin generates the 6-digit code and sends it **out of band** — phone, Telegram. Not through this app. |
| 10.3 | The agent holds credentials **locally** so codes verify offline. The risk that a local-admin employee extracts the secret is **accepted**. |
| 10.4 ⚠️ | The owner asked for codes based on company timezone. TOTP is defined on UTC; timezone-relative codes add no security and break across DST transitions. **Recommendation: keep codes on UTC, display times in company time.** Not yet settled. |

## 11. Uninstall & tamper resistance

| # | Decision |
|---|---|
| 11.1 | Uninstall **is permitted**, with an admin-issued code. |
| 11.2 | Uninstall must work **offline**. |
| 11.3 | **Remove the `/cleanup` switch and `FORCE_CLEANUP.txt`.** Both bypass the code entirely. (The doc file is already deleted; the code switch is not.) |
| 11.4 | The service **auto-restarts** itself if stopped or killed. |
| 11.5 | Realistic bar: stop casual removal. A determined local admin can always win, and that is accepted. |

## 12. Vault & data integrity

| # | Decision |
|---|---|
| 12.1 | The vault serves **both** purposes: hide captured data from the employee, and prove it was not tampered with. |
| 12.2 | A chain break means **tampering**, not a routine health warning. |
| 12.3 | **No push alerts, ever.** |
| 12.4 | Instead, the admin sees data loss / deletion / tampering **inline** in each staff member's screenshot, browsing and timetrack views, with the **exact position** of the missing data marked. |
| 12.5 ⚠️ | 12.1 and 12.2 are not currently achievable: the vault key is derivable on the PC, and chain breaks fire on ordinary offline/reconnect cycles. Making the signal trustworthy requires **server-side chain anchoring**. Until then, gap markers should read "data missing", not "tampering". |

## 13. Sync & retention

| # | Decision |
|---|---|
| 13.1 | Offline buffering: **never drop anything**. Buffer until it uploads. |
| 13.2 | Data expires after **30 days**. |

## 14. Admin experience

| # | Decision |
|---|---|
| 14.1 | Home screen: **Today's overview** — who is working now, totals, anything wrong. Drill down from there. |
| 14.2 | **Leaderboard**: finish it as a ranking of staff by hours worked. |
| 14.3 | Any staff member, any period, chosen from a calendar, for every data view. |
| 14.4 | The staff sticker is **proof the tracking engine is running correctly**. It must reflect real engine health, not just draw a bar. |
| 14.5 | **No exports.** No CSV, no PDF, no reports. View in app only. |
| 14.6 | **Desktop only.** No web dashboard, no mobile. |

## 15. Scale & platform

| # | Decision |
|---|---|
| 15.1 | **20–50 employees** per company. Pagination is required in the staff and screenshot views. |
| 15.2 | **Windows 10 and 11, any edition**, on cheap low-end hardware. The agent must be light on CPU and RAM. |
| 15.3 | Antivirus: no problems observed so far. |
| 15.4 | A code-signing certificate is planned but not yet purchased. Without one, SmartScreen warns on install and antivirus may quarantine the agent. |

---

## Owner's assessment of what is broken

All five areas are in scope for repair:

1. Tracking engine
2. Admin app UI
3. Install / uninstall
4. USB / TOTP flow
5. Team leader and policeman UI

---

## Explicitly deferred

No decision exists for: product positioning, the legal/consent model (disclosure to employees,
GDPR, jurisdiction), the hosting model (SaaS vs self-hosted), or licensing and pricing.

The hosting question has an architectural consequence: the agent hardcodes
`https://teamscop.com` in roughly 25 places, which is correct only for single-tenant SaaS.
