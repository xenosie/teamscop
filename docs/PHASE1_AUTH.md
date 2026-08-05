# Phase 1 — Auth

## Rules (locked)

| Rule | Behavior |
|------|----------|
| Identity | Hardware-bound `deviceKey` (not email) |
| Admin signup | Creates company + admin user; returns offline `companyToken` (`TS1.…`) |
| Staff signup | Requires valid company token; server re-validates AES payload |
| Login | `deviceKey` + password → JWT access token |
| Avatars | Optional image; stored under avatar root; public path `/media/avatars/…` |

## API

| Method | Path | Who |
|--------|------|-----|
| POST | `/api/auth/admin/signup` | Public (multipart) |
| POST | `/api/auth/staff/signup` | Public (multipart + companyToken) |
| POST | `/api/auth/login` | Public |
| GET | `/api/auth/me` | Bearer |
| POST | `/api/auth/company-token/reveal` | Admin Bearer |

## Clients

- `Teamscop.Engine.Auth` — `AuthApiClient`, company-token codec, device key
- `Teamscop.App` — Register Business / Join Business UI
- Shared secret: API `CompanyToken__Key` must match agent build key

## See also

- [STATUS.md](STATUS.md)
- Root [README.md](../README.md) API index
