# Phase 3: Accounts & Balances -- Verification Report

**Verdict: PASS WITH WARNINGS**

**Reviewed by:** Reviewer Agent
**Date:** 2026-03-12

---

## Checklist

### 1. Does it work?

- **Build:** `dotnet build` succeeds with 0 errors.
- **Controller inherits `RestBaseController`:** Yes -- route prefix `api/rest/accounts`, auth filter, exception filter, camelCase serialization all inherited.

### 2. Does it match the plan?

Plan specifies 10 endpoints for Phase 3. All 10 are implemented:

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `GET /accounts` | `GetAll()` | `[HttpGet]` | GET | Present |
| 2 | `POST /accounts` | `Create()` | `[HttpPost]` | POST | Present |
| 3 | `GET /accounts/{address}` | `GetByAddress()` | `[HttpGet("{address}")]` | GET | Present |
| 4 | `GET /accounts/{address}/balance` | `GetBalance()` | `[HttpGet("{address}/balance")]` | GET | Present |
| 5 | `POST /accounts/import` | `ImportKey()` | `[HttpPost("import")]` | POST | Present |
| 6 | `GET /accounts/{address}/nonce` | `GetNonce()` | `[HttpGet("{address}/nonce")]` | GET | Present |
| 7 | `POST /accounts/{address}/rescan` | `Rescan()` | `[HttpPost("{address}/rescan")]` | POST | Present |
| 8 | `POST /accounts/sync-balances` | `SyncBalances()` | `[HttpPost("sync-balances")]` | POST | Present |
| 9 | `GET /accounts/{address}/nfts` | `GetNfts()` | `[HttpGet("{address}/nfts")]` | GET | Present |
| 10 | `GET /accounts/{address}/validate` | `Validate()` | `[HttpGet("{address}/validate")]` | GET | Present |

**No extra endpoints, no missing endpoints. HTTP verbs are correct (GET for reads, POST for mutations).**

#### V1 Mapping Verification

| REST Method | V1 Source | Logic Match |
|---|---|---|
| `GetAll` | `V1.GetAllAddresses` + `V2.GetBalances` | Correct -- combines account list with state data for balances. Adds pagination (V1 had none). |
| `Create` | `V1.GetNewAddress` (line 487) | Correct -- same HD check, same `AccountData.CreateNewAccount()` fallback, same log message. |
| `GetByAddress` | `V1.GetAddressInfo` + `V2.GetStateBalance` | Correct -- combines account lookup with state data. |
| `GetBalance` | `V2.GetStateBalance` (line 263) | Correct -- uses `StateData.GetSpecificAccountStateTrei()`. |
| `ImportKey` | `V1.ImportPrivateKey` (line 849) | Correct -- calls `AccountData.RestoreAccount()`. Private key in body, not URL. |
| `GetNonce` | `TXV1.GetAddressNonce` (line 379) | Correct -- uses `AccountStateTrei.GetNextNonce()`. |
| `Rescan` | `V1.RescanForTx` (line 905) | Correct -- fires rescan as background task. |
| `SyncBalances` | `V1.SyncBalances` (line 927) | Correct -- identical logic: iterates accounts + reserve accounts, updates from state. |
| `GetNfts` | Wallet NFT listing | Correct -- uses `SmartContractStateTrei.GetSmartContractsOwnedByAddress()`. |
| `Validate` | `V1.ValidateAddress` (line 1229) | Correct -- uses `AddressValidateUtility.ValidateAddress()`. |

### 3. Is it safe?

- **`ImportKey` auth:** The action name `ImportKey` is in `EncryptionRequiredActions` in `RestApiAuthFilter` -- returns 401 WALLET_LOCKED when wallet is encrypted and locked. Correct.
- **`ImportKey` request:** Private key sent in request body via `ImportKeyRequest` DTO with `[Required]` validation. Not in URL. This is a security improvement over V1's `ImportPrivateKey/{id}` pattern.
- **`Create` response:** Returns `{ address, privateKey }`. This matches V1 behavior (`V1Controller.GetNewAddress` line 503). The private key is returned to the caller who just created it. This is expected for a wallet CLI API.

**WARNING: `Create` endpoint returns the private key in the response body.** While this matches V1's behavior, callers should be aware this is sensitive data. The auth filter requires a valid API token for this endpoint, which mitigates unauthorized access. The private key is only shown at creation time (subsequent `GetByAddress` does not expose it). This is acceptable for the wallet CLI use case.

- **No SQL injection:** All data access uses LiteDB queries with parameterized lookups.
- **Error envelopes:** All failure paths return structured envelopes via `Fail()`. No raw exceptions leak.

### 4. Is it maintainable?

- **Pagination:** `GetAll` correctly uses `PaginationParams` with skip/take. Total count computed before paging.
- **Request DTOs:** `ImportKeyRequest` in separate file `AccountRequests.cs` with `[Required]` on `PrivateKey`, optional `Scan` defaulting to false. Matches plan spec.
- **Consistent response shapes:** Anonymous types used consistently. State data merged with account data where applicable.
- **Background task:** `Rescan` correctly fires `BlockchainRescanUtility.RescanForTransactions` on `Task.Run()` to avoid blocking the request.

### 5. Is it performant?

- **`GetAll` N+1 concern:** The method queries all accounts, then calls `StateData.GetSpecificAccountStateTrei()` per account in the `.Select()`. This is an N+1 pattern. For wallets with many accounts this could be slow. However, this matches the V1 pattern and wallet accounts are typically in the single digits to low hundreds. Acceptable for now, but worth noting for future optimization if account counts grow.
- **`SyncBalances`:** Same per-account state lookup pattern as V1. Acceptable.

---

## Warnings

1. **N+1 query in `GetAll`:** Each account triggers a separate `StateData.GetSpecificAccountStateTrei()` call. For typical wallet sizes (< 100 accounts) this is fine. If account counts grow significantly, consider a batch state lookup. Non-blocking.

2. **Private key in `Create` response:** The `POST /accounts` endpoint returns the private key in the response. This matches V1 behavior and is expected for wallet account creation, but should be documented for API consumers so they handle the response securely (e.g., not logging it).

---

## Files Reviewed

- `ReserveBlockCore/Api/Rest/Controllers/AccountsController.cs` (228 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/AccountRequests.cs` (12 lines)
- `ReserveBlockCore/Api/Rest/Infrastructure/RestApiAuthFilter.cs` (lines 15-19, EncryptionRequiredActions)
- `ReserveBlockCore/Controllers/V1Controller.cs` (V1 reference: lines 487-512, 609+, 807+, 849-897, 905+, 927-966, 1229+)
- `ReserveBlockCore/Controllers/V2Controller.cs` (V1 reference: lines 35, 263)
- `ReserveBlockCore/Controllers/TXV1Controller.cs` (V1 reference: line 379)

---

## Summary

Phase 3 implements all 10 planned account endpoints correctly. Each endpoint maps faithfully to V1/V2/TXV1 logic. Pagination is implemented on the list endpoint. Private key is accepted in request body (not URL) for import. Request validation uses `[Required]` attributes. HTTP verbs are correctly assigned. Build compiles cleanly. Two non-blocking warnings: N+1 state lookups on list endpoint (acceptable for wallet sizes), and private key exposure in create response (matches V1, expected for wallet creation).
