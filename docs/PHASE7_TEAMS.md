# Phase 7 — Teams & Org Structure

## Rules (locked)

| Rule | Behavior |
|---|---|
| Teams inside company | Admin creates/renames/deletes teams anytime |
| One Team Leader | Exactly one staff leader per team; a person leads at most one team |
| Members | Any number of staff members; each staff in **at most one** team |
| Leaders ≠ members | Leaders are not members of any team |
| Leader visibility | **None by default** (Phase 8: all viewing goes through authority packages / Policemen) |
| Immediate | Every structure change bumps `OrgStructureVersion` and SignalR `OrgStructureUpdated` to the company group |

## Model

```
Company
  └── Team (name, leaderUserId unique)
        └── TeamMember[] (staffUserId unique across all teams)
```

## API

| Method | Path | Who |
|---|---|---|
| GET | `/api/org/structure` | Admin — full tree |
| GET | `/api/org/me` | Anyone — placement (`leader` / `member` / `unassigned` / `admin`) |
| POST | `/api/teams` | Admin — `{ name, leaderUserId }` |
| PUT | `/api/teams/{id}` | Admin — `{ name?, leaderUserId? }` |
| DELETE | `/api/teams/{id}` | Admin |
| PUT | `/api/teams/{id}/members` | Admin — replace `{ memberUserIds: [] }` |
| POST/DELETE | `/api/teams/{id}/members[/{staffUserId}]` | Admin |
| GET | `/api/tracking/staff` | Admin = all staff; Leader = members |
| GET | `/api/tracking/events?staffUserId=` | Admin or that member’s leader |
| GET | `/api/tracking/config/{staffUserId}` | Admin or that member’s leader (read) |

Hub: `/hubs/config` → `OrgStructureUpdated` (company group)

## AdminHost

`org` · `team-create` · `team-rename` · `team-leader` · `team-members` · `team-add` · `team-remove` · `team-delete`
