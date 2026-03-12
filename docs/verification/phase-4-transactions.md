# Phase 4: Transactions -- Verification Report

**Verdict: PASS**

**Reviewed by:** Reviewer Agent
**Date:** 2026-03-12

---

## Checklist

### 1. Does it work?

- **Build:** `dotnet build` succeeds with 0 errors.
- **Controller inherits `RestBaseController`:** Yes -- route prefix `api/rest/transactions`, auth filter, exception filter, camelCase serialization all inherited.

### 2. Does it match the plan?

Plan specifies 10 endpoints for Phase 4. All 10 are implemented:

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `POST /transactions` | `SendRaw()` | `[HttpPost]` | POST | Present |
| 2 | `POST /transactions/send` | `Send()` | `[HttpPost("send")]` | POST | Present |
| 3 | `GET /transactions?status=&page=` | `GetAll()` | `[HttpGet]` | GET | Present |
| 4 | `GET /transactions/{hash}` | `GetByHash()` | `[HttpGet("{hash}")]` | GET | Present |
| 5 | `GET /transactions/search/{hash}` | `SearchNetwork()` | `[HttpGet("search/{hash}")]` | GET | Present |
| 6 | `POST /transactions/verify` | `Verify()` | `[HttpPost("verify")]` | POST | Present |
| 7 | `POST /transactions/fee` | `EstimateFee()` | `[HttpPost("fee")]` | POST | Present |
| 8 | `POST /transactions/hash` | `CalculateHash()` | `[HttpPost("hash")]` | POST | Present |
| 9 | `POST /transactions/{hash}/replace` | `ReplaceByFee()` | `[HttpPost("{hash}/replace")]` | POST | Present |
| 10 | `GET /transactions/mempool` | `GetMempool()` | `[HttpGet("mempool")]` | GET | Present |

**No extra endpoints, no missing endpoints. HTTP verbs are correct.**

#### V1 Mapping Verification

| REST Method | V1 Source | Logic Match |
|---|---|---|
| `SendRaw` | `TXV1.SendRawTransaction` (line 994) | Correct -- identical JToken parse, Data field normalization, VerifyTX call with TKNZ_WD_OWNER skip, rating service, AddToPool + SendTXMempool. |
| `Send` | `V1.SendTransaction` (line 1081) | Correct -- validates address, calls `WalletService.SendTXOut()`. Amount is decimal in body (V1 parsed from string). |
| `GetAll` | `TXV1.GetPendingLocalTX` et al. | Correct -- switch on status query param to select pending/failed/success/mined/all. Adds pagination. |
| `GetByHash` | `TXV1.GetLocalTxByHash` (line 167) | Correct -- `TransactionData.GetTxByHash()`. |
| `SearchNetwork` | `TXV1.GetNetworkTXByHash` (line 400) | Correct -- parallel block search with core count check. |
| `Verify` | `TXV1.VerifyRawTransaction` (line 946) | Correct -- same parse + verify flow, returns verification result with error message. |
| `EstimateFee` | `TXV1.GetRawTxFee` (line 828) | Correct -- constructs transaction, calls `FeeCalcService.CalculateTXFee()`. |
| `CalculateHash` | `TXV1.GetTxHash` (line 870) | Correct -- deserializes, normalizes, calls `tx.Build()`, returns hash. |
| `ReplaceByFee` | `TXV1.ReplaceTransactionByFee` (line 1319) | Correct -- finds TX in mempool, calls `WalletService.SendTXOutRBF()`. |
| `GetMempool` | `V1.GetMempool` (line 1322) | Correct -- `TransactionData.GetMempool()`. |

### 3. Is it safe?

- **`Send` wallet-locked check:** The action name `Send` is in `EncryptionRequiredActions` in `RestApiAuthFilter`. Wallet-locked requests return 401 WALLET_LOCKED. Correct.
- **`Send` address validation:** Validates `ToAddress` via `AddressValidateUtility.ValidateAddress()` before calling service. Correct.
- **`SendRaw` transaction verification:** Runs `TransactionValidatorService.VerifyTX()` before adding to pool. Rejects invalid transactions. Correct.
- **`SearchNetwork` core check:** Requires minimum 4 cores (or `RunUnsafeCode` flag) before running parallel chain search. Prevents resource exhaustion on weak hardware. Correct.
- **Request DTOs:**
  - `SendTransactionRequest`: `[Required]` on FromAddress, ToAddress; `[Range(0.00000001, max)]` on Amount. Prevents zero/negative sends.
  - `ReplaceByFeeRequest`: `[Required]` + `[Range]` on NewFee. Prevents zero-fee RBF.
- **Error envelopes:** All failure paths use `Fail()`. No raw exception leakage.

### 4. Is it maintainable?

- **Raw TX parsing:** `SendRaw`, `Verify`, `EstimateFee`, and `CalculateHash` share the same JToken parse + Data field normalization pattern. This is duplicated from V1 and could be extracted to a helper, but is acceptable for a thin API layer that mirrors V1 patterns.
- **Status filtering:** `GetAll` uses a clean switch expression on the status query param. Good.
- **Pagination:** `GetAll` correctly paginates with `PaginationParams`.
- **`RawTransactionRequest` DTO:** Defined in `TransactionRequests.cs` but not used -- `SendRaw`, `Verify`, `EstimateFee`, and `CalculateHash` accept `object jsonData` directly. This matches V1's pattern and works with ASP.NET model binding. The unused DTO is harmless.

### 5. Is it performant?

- **`SearchNetwork` parallel search:** Uses `Parallel.ForEach` with capped parallelism (`MaxDegreeOfParallelism` 2 or 4). Matches V1. This is inherently expensive (full chain scan) but appropriately guarded by the core count check.
- **`GetAll` materializes all transactions then paginates:** `txList.ToList()` loads all matching TXs into memory before skip/take. For large transaction histories this could be slow, but matches V1 behavior and is bounded by local wallet transactions (not chain-wide).

---

## Warnings

1. **Unused `RawTransactionRequest` DTO:** Defined in `TransactionRequests.cs` but never referenced by any controller method. The raw TX endpoints accept `object jsonData` directly (matching V1 pattern). Not a bug, just dead code. Consider removing in a cleanup pass.

---

## Files Reviewed

- `ReserveBlockCore/Api/Rest/Controllers/TransactionsController.cs` (265 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/TransactionRequests.cs` (30 lines)
- `ReserveBlockCore/Api/Rest/Infrastructure/RestApiAuthFilter.cs` (EncryptionRequiredActions check)
- `ReserveBlockCore/Controllers/TXV1Controller.cs` (V1 reference: lines 126, 167, 400, 828, 870, 946, 994, 1319)
- `ReserveBlockCore/Controllers/V1Controller.cs` (V1 reference: lines 1081, 1322)

---

## Summary

Phase 4 implements all 10 planned transaction endpoints correctly. Each endpoint maps faithfully to TXV1/V1 logic. The `Send` action is properly guarded by the wallet-locked auth check. Address validation runs before sends. Raw transaction endpoints correctly verify before broadcasting. Request DTOs have appropriate `[Required]` and `[Range]` validation. Pagination works on the list endpoint. Build compiles cleanly. One minor warning about an unused DTO.
