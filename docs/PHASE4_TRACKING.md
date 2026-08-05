# Phase 4 — Time / Screenshot / Chrome + Secure Local Vault

## Requirements mapping

| Requirement | Implementation |
|---|---|
| Time track via mouse/keyboard | `TimeTrackEngine` uses Win32 `GetLastInputInfo` (idle-based). Hysteresis: active ≤60s idle → Working; ≥180s idle → Rest. No per-key hooks (avoids slowing input). |
| Screenshot on admin signal | Admin `PUT /api/tracking/config/{staffId}` → SignalR `TrackingConfigUpdated` to staff immediately. |
| Quality budgets | Low ≤30KB, Med ≤50KB, High ≤70KB JPEG via binary-search quality; all displays captured. |
| Chrome history all profiles | `ChromeHistoryWatcher` copies each profile `History` DB and reads visits after install watermark. |
| Local compressed+encrypted | `SecureVault`: Brotli → AES-256-GCM → HMAC-SHA256 hash chain + HMAC tip. |
| Offline accumulate / online push | Vault first, then existing `FileOutboxQueue` + `SyncEngine` flush. |
| Tamper / deletion detect | Sequence gaps + HMAC chain + tip MAC; alerts via `vault_alert` events. |
| Gap-free central DB | Ingest stores `vaultSequence`/`chainHash`; `agent_sequence_states` tracks gaps. |
| Must not slow PC | BelowNormal thread; 15–30s loops; no hooks; Chrome copy+read capped 500 rows; screenshots only on period. |

## Crypto

- Master key: HKDF-SHA256(deviceKey \|\| companyTokenKey)
- Record keys: HKDF → enc + mac
- Record file: header \| kind \| nonce \| cipher \| tag \| prevHash \| chainHash
