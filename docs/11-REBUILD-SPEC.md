# 11 — Rebuild Spec (round 2)

Owner decisions from real-machine testing, 2026-08-08. Supersedes anything in
[02-REQUIREMENTS](02-REQUIREMENTS.md) that contradicts it.

---

## 1. Removals

| # | Decision |
|---|---|
| 1.1 | **Delete tamper/deletion detection entirely.** Drop the vault hash chain, `agent_chain_breaks`, `/api/tracking/gaps`, chain health, `vault_alert`, and every "data missing" marker in the screenshot / browsing / timetrack views. |
| 1.2 | **Keep the vault's encryption.** Captured data must still be unreadable on disk by the employee. Encryption stays; only the chain and detection go. |
| 1.3 | **Delete the "Today" overview page.** The app opens on **Leaderboard**. |
| 1.4 | Codes page: **no enroll / re-enroll UI.** Codes are always available. |
| 1.5 | Codes page: **staff only.** Remove the business/company row. |

## 2. The time model — one frame of reference

**Requirement, stated directly by the owner:** *"the configured business timezone and the calendar
in admin should be synched perfectly, and there should be no mismatches and unclear parts on the
time concept in this project."*

| # | Decision |
|---|---|
| 2.1 | The company business timezone is the **only** frame of reference in the product. |
| 2.2 | The admin's calendar operates in company time. A selected day means that day **in company time**. |
| 2.3 | Period bounds sent to the server are company-local boundaries converted to UTC at one place. |
| 2.4 | Every displayed timestamp is company time. Nothing renders in viewer-local or machine-local time. |
| 2.5 | The timetrack bar spans **exactly the period selected in the calendar** — not a fixed day, not a rolling window. |

Treat calendar → period bounds → bar span → displayed timestamps as **one coherent chain**, not
per-screen conversions.

## 3. Screenshots

| # | Decision |
|---|---|
| 3.1 | Encode **WebP**, not JPEG — roughly half the bytes at the same visual quality. |
| 3.2 | Size budget is **per display**: High **100 KB**, Medium **60 KB**, Low **20 KB**. Maps onto the existing quality setting. |
| 3.3 | Quality must be genuinely legible at the budget. The current output is too poor to read. |
| 3.4 | **Rebuild the screenshot viewer.** Thumbnail grid to browse, plus a proper full-screen mode with zoom for reading text on the captured screen. Simplify it — the current one is buggy. |

## 4. Browsing history

| # | Decision |
|---|---|
| 4.1 | **Never read history from before the agent was installed.** Set a watermark on first run; earlier history is never read, including on the first poll. |
| 4.2 | Subdomains roll up under the registrable domain **for display**. Full URLs are still stored and shown when a domain is opened. |

## 5. Staff list

| # | Decision |
|---|---|
| 5.1 | A circle badge on each staff icon: **green = working**, **red = rest**, **grey = offline** (no heartbeat / PC off / agent not running). |
| 5.2 | Working means input within the 3-minute idle window (§5.2). |

## 6. Approval codes (USB + uninstall)

| # | Decision |
|---|---|
| 6.1 | The credential is **derived deterministically from the machine key** — no random secret, no storage, no enrolment step. Server, agent and engines share one derivation. |
| 6.2 | Derivation input: **device key alone**, per owner decision. ⚠️ See 6.5. |
| 6.3 | Codes are **derived from company local time**, per owner decision. ⚠️ See 6.6. |
| 6.4 | The agent verifies **offline**, with no server call, because both sides derive the same secret. |
| 6.5 | ⚠️ **Known weakness, accepted.** The device key is not secret — it is returned by `/api/auth/me`, carried as a JWT claim, and stored in plain text on the machine. Anyone who learns a device key can mint that machine's codes. Adding the company key to the derivation would close this with no cost to determinism, offline operation or the absence of enrolment. **Keep the derivation behind one function so this is a one-line change.** |
| 6.6 | ⚠️ **Known weakness, accepted.** TOTP is defined on UTC. Deriving from company local time means codes shift by an hour at each DST transition. Both sides must share one rule and one timezone source, or codes silently stop matching. |

## 7. Staff experience

| # | Decision |
|---|---|
| 7.1 | Plain staff (not leader, not policeman) **cannot open the main window**. They get the bar only. |
| 7.2 | The enrolment window is available until they have joined; afterwards, bar only. A later promotion to leader restores the full window. |
| 7.3 | USB insert and uninstall both raise a **small always-on-top box**: what is being asked, a 6-digit input, OK and Cancel. Verified offline. |

## 8. Admin machine

| # | Decision |
|---|---|
| 8.1 | **Tracking must never happen on an admin PC.** |
| 8.2 | Mechanism: install everything, then **the service removes itself** once the machine registers as admin. |

## 9. Delivery

One pass covering everything above, then a single new installer to test.

---

## Open items carried forward

- Legal/consent model, hosting model, licensing — still undecided
  ([01-PRODUCT](01-PRODUCT.md), "Undecided")
- Code-signing certificate — planned, not purchased
- Nothing in this project has been verified on Windows beyond the owner's manual testing
