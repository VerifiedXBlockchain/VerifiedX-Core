# Phase 6: Signatures & ADNR -- Verification Report

**Verdict: PASS**

**Reviewed by:** Reviewer Agent
**Date:** 2026-03-12

---

## Checklist

### 1. Does it work?

- **Build:** `dotnet build` succeeds with 0 errors.
- **Both controllers inherit `RestBaseController`:** Yes -- `SignaturesController` at `api/rest/signatures`, `AdnrController` at `api/rest/adnr`.

### 2. Does it match the plan?

Plan specifies 7 endpoints (2 signatures + 5 ADNR). All 7 implemented:

#### SignaturesController (2 endpoints)

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `POST /signatures` | `CreateSignature()` | `[HttpPost]` | POST | Present |
| 2 | `POST /signatures/verify` | `VerifySignature()` | `[HttpPost("verify")]` | POST | Present |

#### AdnrController (5 endpoints)

| # | Plan Endpoint | Method | Route | Verb | Status |
|---|---|---|---|---|---|
| 1 | `POST /adnr` | `CreateAdnr()` | `[HttpPost]` | POST | Present |
| 2 | `POST /adnr/transfer` | `TransferAdnr()` | `[HttpPost("transfer")]` | POST | Present |
| 3 | `DELETE /adnr/{address}` | `DeleteAdnr()` | `[HttpDelete("{address}")]` | DELETE | Present |
| 4 | `GET /adnr/resolve/{name}` | `Resolve()` | `[HttpGet("resolve/{name}")]` | GET | Present |
| 5 | `GET /adnr/reverse/{address}` | `ReverseLookup()` | `[HttpGet("reverse/{address}")]` | GET | Present |

**No extra endpoints, no missing endpoints. HTTP verbs correct including DELETE for ADNR deletion.**

#### V1 Mapping Verification

| REST Method | V1 Source | Logic Match |
|---|---|---|
| `Signatures.CreateSignature` | `V1.CreateSignature` (line 1247) | Correct -- gets account by address, calls `SignatureService.CreateSignature()` with private key and public key. Params in body instead of URL. |
| `Signatures.VerifySignature` | `V1.ValidateSignature` (line 1303) | Correct -- calls `SignatureService.VerifySignature()` with address, message, signature. All params in body. |
| `Adnr.CreateAdnr` | `TXV1.CreateAdnr` (line 1084) | Correct -- identical validation flow: account check, existing ADNR check, name length limit (ADNRLimit), alphanumeric regex, name uniqueness check, then `Adnr.CreateAdnrTx()`. |
| `Adnr.TransferAdnr` | `TXV1.TransferAdnr` (line 1173) | Correct -- validates both accounts, checks source has ADNR, validates target address, checks target has no ADNR, calls `Adnr.TransferAdnrTx()`. |
| `Adnr.DeleteAdnr` | `TXV1.DeleteAdnr` (line 1247) | Correct -- validates account, checks ADNR exists, calls `Adnr.DeleteAdnrTx()`. |
| `Adnr.Resolve` | `V2.ResolveAdnr` (line 290) | Correct -- checks .vfx/.rbx suffix, calls `Adnr.GetAddress()`. |
| `Adnr.ReverseLookup` | `V2.ResolveAddressAdnr` (line 332) | Correct -- calls `Adnr.GetAdnr(address)` for reverse lookup. |

### 3. Is it safe?

- **Wallet-locked protection:** All 4 write actions are in `EncryptionRequiredActions`:
  - `CreateSignature` -- in set, returns 401 when wallet locked
  - `CreateAdnr` -- in set, returns 401 when wallet locked
  - `TransferAdnr` -- in set, returns 401 when wallet locked
  - `DeleteAdnr` -- in set, returns 401 when wallet locked
- **ADNR validation:** `CreateAdnr` validates:
  - Account exists
  - No existing ADNR on address
  - Name length <= ADNRLimit (65 chars)
  - Alphanumeric-only regex
  - Name not already taken
- **Transfer validation:** Validates source has ADNR, target address valid, target has no ADNR.
- **Resolve input:** Validates .vfx/.rbx suffix before lookup.
- **Request DTOs:** All have `[Required]` on all fields:
  - `CreateSignatureRequest`: Address, Message
  - `VerifySignatureRequest`: Address, Message, Signature
  - `CreateAdnrRequest`: Address, Name
  - `TransferAdnrRequest`: FromAddress, ToAddress
- **No sensitive data exposure:** Signature creation returns only the signature string, not the private key.

### 4. Is it maintainable?

- **Clean separation:** Signatures and ADNR in separate controllers with separate request DTOs in separate files. Good.
- **ADNR validation logic:** Faithfully mirrors V1 validation chain but with cleaner early-return pattern instead of deeply nested if/else.
- **Error codes:** Descriptive and specific (`NAME_TOO_LONG`, `INVALID_NAME`, `NAME_TAKEN`, `TARGET_HAS_ADNR`, etc.).

### 5. Is it performant?

- All operations are single-record lookups or lightweight service calls. No bulk operations or N+1 patterns.
- No concerns.

---

## Warnings

None.

---

## Files Reviewed

- `ReserveBlockCore/Api/Rest/Controllers/SignaturesController.cs` (37 lines)
- `ReserveBlockCore/Api/Rest/Controllers/AdnrController.cs` (147 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/SignatureRequests.cs` (25 lines)
- `ReserveBlockCore/Api/Rest/Models/Requests/AdnrRequests.cs` (22 lines)
- `ReserveBlockCore/Api/Rest/Infrastructure/RestApiAuthFilter.cs` (EncryptionRequiredActions verification)
- `ReserveBlockCore/Controllers/V1Controller.cs` (V1 reference: lines 1247, 1303)
- `ReserveBlockCore/Controllers/TXV1Controller.cs` (V1 reference: lines 1084, 1173, 1247)
- `ReserveBlockCore/Controllers/V2Controller.cs` (V1 reference: lines 290, 332)

---

## Summary

Phase 6 implements all 7 planned endpoints across SignaturesController (2) and AdnrController (5). All map correctly to V1/TXV1/V2 sources. HTTP verbs are correct including DELETE for ADNR deletion. All 4 write actions are wallet-locked protected via the auth filter. ADNR creation includes comprehensive validation (length, regex, uniqueness). Request DTOs have proper `[Required]` validation on all fields. Build compiles cleanly. No warnings or blockers.
