# Phase 9: Validators & Reserve Accounts -- Verification Report

**Verdict: PASS**

**Reviewed by:** Reviewer Agent
**Date:** 2026-03-12

---

## Checklist

### 1. Does it work?

- **Build:** `dotnet build` succeeds with 0 errors.
- **Both controllers inherit `RestBaseController`:** Yes -- `ValidatorsController` at `api/rest/validators`, `ReserveAccountsController` with explicit `[Route("api/rest/reserve-accounts")]` (hyphenated).

### 2. Does it match the plan?

Plan specifies 19 endpoints (9 validators + 10 reserve accounts). All 19 implemented:

#### ValidatorsController (9 endpoints)

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `GET /validators` | `GetAll()` | `[HttpGet]` | GET | Present |
| 2 | `GET /validators/status` | `GetStatus()` | `[HttpGet("status")]` | GET | Present |
| 3 | `POST /validators/start` | `Start()` | `[HttpPost("start")]` | POST | Present |
| 4 | `POST /validators/stop` | `Stop()` | `[HttpPost("stop")]` | POST | Present |
| 5 | `GET /validators/{address}` | `GetInfo()` | `[HttpGet("{address}")]` | GET | Present |
| 6 | `POST /validators/register` | `Register()` | `[HttpPost("register")]` | POST | Present |
| 7 | `PUT /validators/name` | `ChangeName()` | `[HttpPut("name")]` | PUT | Present |
| 8 | `POST /validators/reset` | `Reset()` | `[HttpPost("reset")]` | POST | Present |
| 9 | `GET /validators/pool` | `GetPool()` | `[HttpGet("pool")]` | GET | Present |

#### ReserveAccountsController (10 endpoints)

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `GET /reserve-accounts` | `GetAll()` | `[HttpGet]` | GET | Present |
| 2 | `POST /reserve-accounts` | `Create()` | `[HttpPost]` | POST | Present |
| 3 | `GET /reserve-accounts/{address}` | `GetInfo()` | `[HttpGet("{address}")]` | GET | Present |
| 4 | `POST /reserve-accounts/{address}/publish` | `Publish()` | `[HttpPost("{address}/publish")]` | POST | Present |
| 5 | `POST /reserve-accounts/{address}/unlock` | `Unlock()` | `[HttpPost("{address}/unlock")]` | POST | Present |
| 6 | `POST /reserve-accounts/send` | `Send()` | `[HttpPost("send")]` | POST | Present |
| 7 | `POST /reserve-accounts/transfer-nft` | `TransferNft()` | `[HttpPost("transfer-nft")]` | POST | Present |
| 8 | `POST /reserve-accounts/{address}/recover` | `Recover()` | `[HttpPost("{address}/recover")]` | POST | Present |
| 9 | `POST /reserve-accounts/restore` | `Restore()` | `[HttpPost("restore")]` | POST | Present |
| 10 | `POST /reserve-accounts/{hash}/callback` | `Callback()` | `[HttpPost("{hash}/callback")]` | POST | Present |

**No extra endpoints, no missing endpoints. HTTP verbs correct including PUT for validator name change.**

#### V1 Mapping Verification

**Validators** -- map to `V1Controller` and `ValidatorController`:
- `GetAll` -> `V1.GetValidatorAddresses` -- calls `AccountData.GetPossibleValidatorAccounts()`
- `GetStatus` -> `V1.IsValidating` -- checks `Globals.ValidatorAddress` and related flags
- `Start` -> `V1.TurnOnValidator` -- validates account, checks balance against `ValidatorRequiredAmount()`, sets `IsValidating`
- `Stop` -> `V1.TurnOffValidator` -- calls `ValidatorService.DoMasterNodeStop()`
- `GetInfo` -> `V1.GetValidatorInfo` -- looks up from `Validators.Validator.GetAll()`
- `Register` -> `V1.StartValidating` -- checks eligible accounts, calls `ValidatorService.StartValidating()`
- `ChangeName` -> `V1.ChangeValidatorName` -- updates `UniqueName` in validator table
- `Reset` -> `V1.ResetValidator` -- calls `ValidatorService.ValidatorErrorReset()`
- `GetPool` -> `V2.ValidatorPool` -- returns `Globals.NetworkValidators`

**Reserve Accounts** -- map to `RSV1Controller`:
- `GetAll` -> `RSV1.GetAllReserveAccounts` -- `ReserveAccount.GetReserveAccounts()`
- `Create` -> `RSV1.NewReserveAddress` -- `ReserveAccount.CreateNewReserveAccount()`
- `GetInfo` -> `RSV1.GetReserveAccountInfo` -- `ReserveAccount.GetReserveAccountSingle()`
- `Publish` -> `RSV1.PublishReserveAccount` -- balance check, network protection check, creates TX
- `Unlock` -> `RSV1.UnlockReserveAccount` -- password verification, timed unlock key storage
- `Send` -> `RSV1.SendReserveTransaction` -- xRBX-to-xRBX block, address validation, creates TX
- `TransferNft` -> `RSV1.ReserveTransferNFT` -- key derivation, SC validation, creates NFT transfer TX
- `Recover` -> `RSV1.RecoverReserveAccountTx` -- creates recovery TX
- `Restore` -> `RSV1.RestoreReserveAddress` -- supports both full restore and recovery-only mode
- `Callback` -> `RSV1.CallBackReserveAccountTx` -- looks up reserve TX by hash, creates callback TX

### 3. Is it safe?

- **Validator balance check:** `Start` validates `stateTreiBalance >= ValidatorRequiredAmount()` before activating. Prevents underfunded validators.
- **Duplicate validator prevention:** `Start` checks for existing active validator before allowing activation. Returns 409 ALREADY_ACTIVE.
- **Reserve account passwords:** All password-sensitive operations (`Create`, `Publish`, `Unlock`, `Send`, `TransferNft`, `Recover`, `Restore`, `Callback`) accept passwords in request body, not URL.
- **xRBX-to-xRBX block:** `Send` and `TransferNft` prevent reserve-to-reserve transfers.
- **Address validation:** `Send` validates destination address via `AddressValidateUtility.ValidateAddress()`.
- **Key verification:** `Unlock` verifies password by deriving key and recreating account, checking address match. `TransferNft` derives private key and verifies before proceeding.
- **Publish guards:** `Publish` requires minimum 5 VFX balance and checks `IsNetworkProtected` flag to prevent double-publish.
- **Request DTOs:** All have `[Required]` on key fields. `SendReserveTransactionRequest` has `[Range]` on Amount. `UnlockReserveAccountRequest` has `[Range]` on UnlockTime.

### 4. Is it maintainable?

- **Validator DTOs:** 4 clean DTOs in `ValidatorRequests.cs`.
- **Reserve Account DTOs:** 8 DTOs in `ReserveAccountRequests.cs` covering all operations with appropriate optional fields (`StoreRecoveryAccount`, `RescanForTx`, `OnlyRestoreRecovery`, `BackupURL`).
- **Error codes:** Descriptive throughout (`INSUFFICIENT_BALANCE`, `ALREADY_ACTIVE`, `NOT_ACTIVE`, `INVALID_PASSWORD`, `INVALID_CREDENTIALS`, `ALREADY_PUBLISHED`, etc.).
- **Restore dual mode:** `Restore` cleanly handles both full restore and recovery-only mode via the `OnlyRestoreRecovery` flag.

### 5. Is it performant?

- All operations are single-record lookups or service calls. No bulk operations or N+1 patterns.
- `GetPool` returns from in-memory `Globals.NetworkValidators`. Fast.
- No concerns.

---

## Warnings

None. All 19 endpoints implemented correctly with appropriate validation, security checks, and error handling.

---

## Files Reviewed

- `ReserveBlockCore/Api/Rest/Controllers/ValidatorsController.cs` (179 lines)
- `ReserveBlockCore/Api/Rest/Controllers/ReserveAccountsController.cs` (259 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/ValidatorRequests.cs` (30 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/ReserveAccountRequests.cs` (78 lines)

---

## Summary

Phase 9 implements all 19 planned endpoints across ValidatorsController (9) and ReserveAccountsController (10). Validator lifecycle is complete: list eligible accounts, check status, start/stop, register, change name, reset, and pool view. Reserve account operations cover full CRUD plus publish, unlock (timed), send, NFT transfer, recover, restore (dual mode), and callback. All passwords in request body. Balance checks on validator start and reserve publish. xRBX-to-xRBX transfer prevention. PUT verb correctly used for validator name change. Build compiles cleanly. No warnings.
