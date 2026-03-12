# Phase 2: Wallet & Health -- Verification Report

**Verdict: PASS**

**Reviewed by:** Reviewer Agent
**Date:** 2026-03-12

---

## Checklist

### 1. Does it work?

- **Build:** `dotnet build` succeeds with 0 errors.
- **Controller inherits `RestBaseController`:** Yes -- gets route prefix `api/rest/wallets`, auth filter, exception filter, camelCase serialization, and Swagger group assignment automatically.

### 2. Does it match the plan?

Plan specifies 8 endpoints for Phase 2. All 8 are implemented:

| # | Plan Endpoint | Method | Route | Status |
|---|---|---|---|---|
| 1 | `GET /wallets/status` | `GetStatus()` | `[HttpGet("status")]` | Present |
| 2 | `GET /wallets/info` | `GetInfo()` | `[HttpGet("info")]` | Present |
| 3 | `GET /wallets/version` | `GetVersion()` | `[HttpGet("version")]` | Present |
| 4 | `GET /wallets/encryption-status` | `GetEncryptionStatus()` | `[HttpGet("encryption-status")]` | Present |
| 5 | `POST /wallets/encrypt` | `Encrypt()` | `[HttpPost("encrypt")]` | Present |
| 6 | `POST /wallets/unlock` | `Unlock()` | `[HttpPost("unlock")]` | Present |
| 7 | `POST /wallets/lock` | `Lock()` | `[HttpPost("lock")]` | Present |
| 8 | `POST /wallets/hd` | `CreateHd()` | `[HttpPost("hd")]` | Present |

**No extra endpoints, no missing endpoints.**

#### V1 Mapping Verification

| REST Method | V1 Source | Logic Match |
|---|---|---|
| `GetStatus` | `V1Controller.CheckStatus` (line 305) | Correct -- returns "Online" |
| `GetInfo` | `V1Controller.GetWalletInfo` (line 519) | Correct -- same fields from Globals, uses `P2PClient.ArePeersConnected()`. Minor: REST returns typed values (int, bool) instead of V1's `.ToString()` strings -- this is an improvement. |
| `GetVersion` | `V1Controller.GetCLIVersion` (line 2017) | Correct -- returns `Globals.CLIVersion` |
| `GetEncryptionStatus` | `V1Controller.GetCheckEncryptionStatus` (line 183) | Correct -- returns encrypted state and password presence |
| `Encrypt` | `V1Controller.GetEncryptWallet` (line 368) | Correct -- same HD check, same `ToSecureString()`, same `GenerateKeystoreAddresses()` call |
| `Unlock` | `V1Controller.GetDecryptWallet` (line 427) | Correct -- same password verification via signature round-trip. Password in body instead of URL. |
| `Lock` | V1 locks via API password mechanism | REST implementation clears `EncryptPassword` directly, with validator-active guard. Correct for encryption lock semantics. |
| `CreateHd` | `V1Controller.GetHDWallet` (line 320) | Correct -- calls `HDWallet.HDWalletData.CreateHDWallet()` with strength and English wordlist |

### 3. Is it safe?

- **Auth bypass:** `GetStatus` is the action name, which is in `TokenBypassActions` in `RestApiAuthFilter` -- health check works without token. Correct.
- **Password handling:** `Encrypt` and `Unlock` accept password in request body (not URL) via `WalletPasswordRequest` DTO with `[Required]` validation. This is a security improvement over V1's password-in-URL pattern.
- **Unlock failure cleanup:** On wrong password, `Unlock` correctly disposes and resets `EncryptPassword` to a new empty `SecureString` (line 130-131). Matches V1 behavior.
- **Lock guard:** `Lock` prevents locking while validating (checks `ValidatorAddress` and `AdjudicateAccount`). Returns 409 CONFLICT. Correct.
- **No sensitive data in responses:** Encrypt returns generic success message. Unlock returns time-limited message. No passwords or keys in responses.
- **Error envelopes:** All failure paths use `Fail()` with descriptive error codes. No raw exceptions leak.

### 4. Is it maintainable?

- **Request DTOs:** `WalletPasswordRequest` and `CreateHdWalletRequest` defined in the same file, with proper `[Required]` and `[Range]` validation. Clean.
- **Response shapes:** Uses anonymous types for structured responses (`GetInfo`, `GetEncryptionStatus`, `CreateHd`). Acceptable for a thin API layer.
- **No duplication:** Controller methods are concise, calling directly into existing Globals/services.

### 5. Is it performant?

- `GetInfo` calls `P2PClient.ArePeersConnected()` (async) then reads Globals. Lightweight.
- `Unlock` has `Task.Delay(200)` matching V1 behavior -- intentional timing.
- No concerns.

---

## Warnings

1. **Missing endpoints from full route table:** The plan's route table lists `POST /wallets/exit`, `POST /wallets/restart`, and `POST /wallets/hd/restore`, but Phase 2's deliverable list explicitly says 8 endpoints and does not include these. The controller correctly implements only the 8 scoped endpoints. The remaining wallet endpoints should be added in a future phase or follow-up.

2. **`CreateHd` response contains a `Success` field inside `data`:** The response shape `{ success: true, data: { success: true, mnemonic: "..." } }` has a redundant `success` field inside `data`. This is because the anonymous type uses `Success = mnemonic.Item1` (which is a bool indicating HD wallet creation success). This could be confusing but is not incorrect -- the outer envelope `success` indicates the HTTP call succeeded, while `data.success` indicates the wallet operation result. Consider renaming to `created` or `isHd` in a future pass.

---

## Files Reviewed

- `ReserveBlockCore/Api/Rest/Controllers/WalletsController.cs` (186 lines)
- `ReserveBlockCore/Controllers/V1Controller.cs` (V1 reference: lines 86-185, 305-337, 368-385, 427-480, 519-552, 2017)

---

## Summary

Phase 2 implements all 8 planned wallet endpoints correctly. Each endpoint maps faithfully to the corresponding V1 logic while improving on the REST conventions (POST for mutations, password in body, structured error envelopes, camelCase responses). Auth bypass on `GetStatus` is correctly configured. Build compiles cleanly. No security concerns. Two minor non-blocking warnings noted.
