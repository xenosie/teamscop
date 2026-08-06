# Phase 7 — Teams & Org Structure

## Rules (locked)

| Rule | Behavior |
|------|----------|
| Teams inside company | Admin (or `team_management` package) creates/renames/deletes teams anytime |
| Team leader | At most one staff leader per team; **leader slot may be empty**; a person leads at most one team |
| Members | Any number of staff; each staff in **at most one** team |
| Leaders ≠ members | Leaders are not members of any team |
| Tracking visibility | Team leader **immediately** sees Summary / Screenshot / Browsing / Time Track for **their team members only**. Never USB approval or App history (unless separately granted as policeman). See [PHASE8_AUTHORITIES.md](PHASE8_AUTHORITIES.md) |
| Immediate | Structure changes bump `OrgStructureVersion` and SignalR `OrgStructureUpdated` |

## Model

```
Company
  └── Team (name, leaderUserId? nullable unique)
        └── TeamMember[] (staffUserId unique across all teams)
```

Unassigned staff = neither leader nor member of any team.

## API

| Method | Path | Who |
|--------|------|-----|
| GET | `/api/org/structure` | `team_management` (admin has all packages) |
| GET | `/api/org/me` | Anyone — placement (`leader` / `member` / `unassigned` / `admin`) |
| POST | `/api/teams` | `{ name, leaderUserId? }` |
| PUT | `/api/teams/{id}` | `{ name?, leaderUserId?, clearLeader? }` |
| DELETE | `/api/teams/{id}` | — |
| PUT | `/api/teams/{id}/members` | replace `{ memberUserIds: [] }` |
| POST/DELETE | `/api/teams/{id}/members[/{staffUserId}]` | — |

Hub: `/hubs/config` → `OrgStructureUpdated` (company group) + `AuthoritiesUpdated` (each staff, so leaders gain/lose view packages immediately)

Visible staff for monitoring: `GET /api/tracking/staff` (admin/policeman = company; team leader = members only).

## Clients

| Client | Role |
|--------|------|
| **Avalonia App** (`TeamsBoardView`) | Visual team board: add/switch leader, add/remove members, pool modal |
| **AdminHost** CLI | `org` · `team-create` · `team-rename` · `team-leader` · `team-members` · `team-add` · `team-remove` · `team-delete` |

## See also

- [PHASE8_AUTHORITIES.md](PHASE8_AUTHORITIES.md)
- [STATUS.md](STATUS.md)
