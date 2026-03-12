# vBTC V2 Withdrawal Guide

## Overview

A **vBTC V2 withdrawal** redeems tokenized Bitcoin back to a real Bitcoin address. It is a two-step process:

1. **Request Withdrawal** — Publish a VFX on-chain transaction declaring the intent: how much vBTC, to which BTC address, and at what fee rate.
2. **Complete Withdrawal** — Coordinate a FROST threshold signing ceremony among the active validators to produce a valid Bitcoin transaction, then broadcast it to the Bitcoin network.

Any VFX address with a vBTC balance in a contract can request a withdrawal — not just the contract owner.

---

## Prerequisites

- You must have a **vBTC balance** in the target smart contract (either as the owner or a ledger recipient)
- The FROST validator set must have sufficient **active validators** online to meet the threshold
- Only **one active withdrawal** is allowed per user/contract pair at a time

---

## Withdrawal Status Lifecycle

```
Requested
    │
    ▼
Pending_BTC ──── (FROST signing + BTC broadcast) ──→ Completed
    │
    └── (BTC TX fails or stalls) ──→ CancelWithdrawal ──→ Validator Voting ──→ Cancelled
```

---

## Part 1: Request Withdrawal

### Flow Diagram

```
Requester Node
    │
    ├─ 1. POST RequestWithdrawal
    │      → Validate payload (SCUID, RequestorAddress, BTCAddress, Amount > 0, FeeRate > 0)
    │      → Load account (RequestorAddress must exist locally)
    │      → Load VBTCContractV2 from local DB
    │      → Check: no existing active withdrawal for this user + contract (per-user guard)
    │      → Load SmartContractStateTrei → calculate available balance:
    │          ├─ If owner: depositBalance + ledgerDelta
    │          └─ If non-owner: ledgerDelta (received + sent)
    │      → Check: availableBalance >= Amount
    │      → Normalize BTCAddress
    │      → Build VBTC_V2_WITHDRAWAL_REQUEST transaction
    │          Data: { Function, ContractUID, RequestorAddress, BTCAddress, Amount, FeeRate }
    │          FromAddress = ToAddress = RequestorAddress (self-transaction)
    │          Amount (VFX) = 0
    │      → Sign with RequestorAddress private key
    │      → VerifyTX → AddTxToWallet → AddToPool → SendTXMempool
    │      → Return: { Success, RequestHash, SmartContractUID, Amount, Destination, FeeRate, Status }
```

### Step-by-Step API Call

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/RequestWithdrawal
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "RequestorAddress": "xYourVFXAddress...",
  "BTCAddress": "tb1qyourbitcoinaddress...",
  "Amount": 0.01,
  "FeeRate": 10
}
```

Response (success):
```json
{
  "Success": true,
  "Message": "vBTC V2 withdrawal request created successfully",
  "RequestHash": "0x4d5e6f...",
  "SmartContractUID": "abc123def456:1773285700",
  "Amount": 0.01,
  "Destination": "tb1qyourbitcoinaddress...",
  "FeeRate": 10,
  "Status": "Requested"
}
```

Response (active withdrawal already pending):
```json
{
  "Success": false,
  "Message": "You already have an active withdrawal request. Complete it before starting a new one. Request Hash: 0x..."
}
```

### FeeRate

`FeeRate` is the Bitcoin miner fee rate in **satoshis per virtual byte (sats/vB)**. Common values:

| Priority | FeeRate (sats/vB) |
|----------|-----------------|
| Low | 1–5 |
| Medium | 10–20 |
| High | 50+ |

Check a Bitcoin fee estimator to choose an appropriate rate at the time of withdrawal.

---

## Part 2: Complete Withdrawal

This step coordinates the **FROST threshold signing ceremony** to produce a valid Bitcoin transaction, then broadcasts it to the Bitcoin network.

### Node Type Matters

| Node Type | What Happens |
|-----------|-------------|
| **Validator node** | FROST signing runs locally — the node coordinates the ceremony directly with other active validators |
| **Wallet (non-validator) node** | Cannot sign directly — delegates to a remote validator via the FROST port |

### Flow: Validator Node

```
Validator Node
    │
    ├─ 1. POST CompleteWithdrawal
    │      → Validate: node is a validator (Globals.ValidatorAddress is set)
    │      → Load VBTCContractV2 (or reconstruct from State Trei if not in local DB)
    │      → Load VBTCWithdrawalRequest by RequestHash
    │      → Validate: request is not already completed
    │      → Get active validators → check quorum
    │      → Calculate adjusted threshold (VBTCThresholdCalculator):
    │          ├─ Base: 51% of originally registered validators
    │          └─ Adjusted down if validators have been inactive for many blocks
    │      → Calculate requiredValidators = threshold% of active count
    │      → Check: activeValidators.Count >= requiredValidators
    │      → Resolve withdrawal details (Amount, BTCDestination) from:
    │          1. contract.ActiveWithdrawalAmount / ActiveWithdrawalBTCDestination
    │          2. VBTCWithdrawalRequest record (fallback)
    │      → BitcoinTransactionService.ExecuteFROSTWithdrawal()
    │          ├─ Build unsigned Bitcoin TX (Taproot input from deposit address)
    │          ├─ Distribute signing nonces to validators (Round 1)
    │          ├─ Collect partial signatures (Round 2)
    │          └─ Aggregate into final Schnorr signature (Round 3)
    │      → Broadcast signed BTC TX to Bitcoin network
    │      → Build VBTC_V2_WITHDRAWAL_COMPLETE VFX transaction
    │          Data: { Function, ContractUID, WithdrawalRequestHash, BTCTransactionHash, Amount, Destination }
    │      → Sign + broadcast VFX completion TX
    │      → Update contract.LastValidatorActivityBlock
    │      → Return: { Success, VFXTransactionHash, BTCTransactionHash, Status }
```

### Flow: Wallet (Non-Validator) Node

```
Wallet Node                                          Remote Validator
    │                                                       │
    ├─ 1. POST CompleteWithdrawal                           │
    │      → Detects: not a validator (no ValidatorAddress) │
    │      → DelegateWithdrawalToRemoteValidator()          │
    │          ├─ Look up local VBTCWithdrawalRequest        │
    │          │  (get Amount, BTCDestination, FeeRate)      │
    │          ├─ FetchActiveValidatorsFromNetwork()         │
    │          └─ Try each validator:                        │
    │              │                                         │
    │              ├─ POST /frost/mpc/withdrawal/complete ───┤
    │              │   Payload: { SCUID, RequestHash,        ├─ Runs FROST signing (signOnly mode)
    │              │              Amount, BTCDest, FeeRate } ├─ Returns SignedTxHex (NOT broadcast)
    │              │  ← { Success, SignedTxHex }             │
    │              │                                         │
    │          → Wallet node receives SignedTxHex            │
    │          → BitcoinTransactionService.BroadcastTransaction(signedTx)
    │          → Update local contract record                │
    │          → Build + broadcast VBTC_V2_WITHDRAWAL_COMPLETE VFX TX
    │      → Return: { Success, BTCTransactionHash, VFXTransactionHash }
```

### Step-by-Step API Call

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CompleteWithdrawal
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "WithdrawalRequestHash": "0x4d5e6f..."
}
```

Response (success):
```json
{
  "Success": true,
  "Message": "vBTC V2 withdrawal completed successfully with FROST signing",
  "VFXTransactionHash": "0x7a8b9c...",
  "BTCTransactionHash": "a1b2c3d4e5...",
  "Status": "Pending_BTC",
  "SmartContractUID": "abc123def456:1773285700"
}
```

Response (insufficient validators):
```json
{
  "Success": false,
  "Message": "Insufficient validators. Have: 3, Need: 4 (Adjusted threshold: 51%)"
}
```

Response (non-validator, delegation failed):
```json
{
  "Success": false,
  "Message": "Failed to delegate withdrawal to any validator. No validator accepted the request."
}
```

### `Status: "Pending_BTC"` Explained

After `CompleteWithdrawal` returns, the Bitcoin transaction has been **submitted to the Bitcoin network** but is not yet confirmed. The `Pending_BTC` status means:
- The BTC TX was broadcast successfully
- It is waiting for Bitcoin block confirmations
- The VFX ledger already reflects the withdrawal (VFX TX confirmed)

---

## Threshold Calculation

The FROST threshold is **dynamic** — it adjusts based on how long validators have been active since the contract's last use.

| Scenario | Threshold Behavior |
|----------|------------------|
| Validators recently active | Standard 51% threshold |
| Long inactivity (many blocks since last withdrawal) | Threshold can adjust downward to accommodate validator churn |
| Fewer validators than required even at reduced threshold | Withdrawal blocked until more validators come online |

The `VBTCThresholdCalculator` computes:
1. **Adjusted threshold %** from `totalRegisteredValidators`, `activeValidators.Count`, `lastValidatorActivityBlock`, and current block height
2. **Required validators** = `ceil(adjustedThreshold% × activeValidators.Count)`

---

## Cancellation

If a withdrawal gets stuck (e.g. the Bitcoin TX was never broadcast or failed), the contract owner can submit a cancellation request. Validators vote on whether to approve it. A **75% approval** from active validators is required for the cancellation to be processed.

### Request Cancellation

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CancelWithdrawal
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "OwnerAddress": "xYourVFXAddress...",
  "WithdrawalRequestHash": "0x4d5e6f...",
  "BTCTxHash": "a1b2c3...",
  "FailureProof": "describe why this withdrawal failed..."
}
```

Response:
```json
{
  "Success": true,
  "Message": "Cancellation request created. Awaiting validator votes (75% required).",
  "CancellationUID": "f1a2b3c4-...",
  "RequiredVotes": "75%"
}
```

### Validator Vote on Cancellation

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/VoteOnCancellation
Content-Type: application/json

{
  "CancellationUID": "f1a2b3c4-...",
  "ValidatorAddress": "xValidatorAddress...",
  "Approve": true
}
```

Response:
```json
{
  "Success": true,
  "Message": "Vote recorded",
  "CancellationUID": "f1a2b3c4-...",
  "ApproveCount": 4,
  "RejectCount": 1,
  "TotalValidators": 7,
  "ApprovalPercentage": 57,
  "IsApproved": false
}
```

---

## Raw Endpoints (External / Pre-Signed Requests)

For external integrations (separate frontend, mobile app, or signing service), the Raw endpoints accept pre-signed payloads with replay attack protection.

### Raw Withdrawal Request

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/RequestWithdrawalRaw
Content-Type: application/json

{
  "VFXAddress": "xYourVFXAddress...",
  "BTCAddress": "tb1qyourbitcoinaddress...",
  "SmartContractUID": "abc123def456:1773285700",
  "Amount": 0.01,
  "FeeRate": 10,
  "Timestamp": 1773285700,
  "UniqueId": "unique-request-id-123",
  "VFXSignature": "sig_of_all_fields..."
}
```

**Signature data format:** `{VFXAddress}{BTCAddress}{SmartContractUID}{Amount}{FeeRate}{Timestamp}{UniqueId}`

Response:
```json
{
  "Success": true,
  "Message": "Raw withdrawal request created successfully",
  "RequestHash": "xYourVFX_unique12_1773285750",
  "SmartContractUID": "abc123def456:1773285700",
  "Amount": 0.01,
  "Destination": "tb1qyourbitcoinaddress...",
  "Status": "Requested",
  "UniqueId": "unique-request-id-123",
  "Timestamp": 1773285750
}
```

### Raw Withdrawal Completion

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CompleteWithdrawalRaw
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "WithdrawalRequestHash": "xYourVFX_unique12_1773285750",
  "ValidatorAddress": "xValidatorVFXAddress...",
  "Timestamp": 1773285800,
  "UniqueId": "unique-completion-id-456",
  "ValidatorSignature": "sig_of_all_fields..."
}
```

**Signature data format:** `{SmartContractUID}{WithdrawalRequestHash}{ValidatorAddress}{Timestamp}{UniqueId}`

### Raw Cancellation

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CancelWithdrawalRaw
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "OwnerAddress": "xYourVFXAddress...",
  "WithdrawalRequestHash": "xYourVFX_unique12_1773285750",
  "BTCTxHash": "a1b2c3...",
  "FailureProof": "TX never confirmed...",
  "Timestamp": 1773286000,
  "UniqueId": "unique-cancel-id-789",
  "OwnerSignature": "sig_of_all_fields..."
}
```

**Signature data format:** `{SmartContractUID}{OwnerAddress}{WithdrawalRequestHash}{BTCTxHash}{FailureProof}{Timestamp}{UniqueId}`

### Raw Security Features

| Guard | Detail |
|-------|--------|
| **Timestamp validation** | Rejects requests older than 5 minutes |
| **Signature verification** | Owner/validator must sign all request fields with their VFX private key |
| **Replay attack prevention** | `UniqueId` is stored; duplicate `UniqueId` for same address + contract is rejected |
| **Incomplete withdrawal guard** | Only 1 active withdrawal per user/contract pair (Raw path checks this too) |

---

## Key Differences: Standard vs. Raw Endpoints

| Aspect | Standard | Raw |
|--------|----------|-----|
| Who signs | Node signs automatically (uses local key) | Caller provides pre-made signature |
| Replay protection | Active withdrawal guard (per-user) | UniqueId deduplication + per-user guard |
| Balance check | State Trei + local contract | State Trei only |
| Completion auth | No additional auth required | Validator must sign the completion request |
| Use case | Direct wallet interaction | External apps, mobile, or hardware signing |

---

## Transaction Types Reference

| Step | Transaction Type | Actor | VFX Amount |
|------|-----------------|-------|------------|
| Step 1: Request | `VBTC_V2_WITHDRAWAL_REQUEST` | Requestor | 0 (fee only) |
| Step 2: Complete | `VBTC_V2_WITHDRAWAL_COMPLETE` | Validator / Requestor | 0 (fee only) |
| BTC broadcast | Bitcoin network TX | FROST validator set | n/a |

---

## Error Reference

| Error | Cause |
|-------|-------|
| `Account not found` | `RequestorAddress` not in local wallet |
| `vBTC V2 contract not found` | `SmartContractUID` not in local DB |
| `Smart contract state not found` | Consensus state missing (node not synced) |
| `You already have an active withdrawal request` | Per-user guard: complete or cancel first |
| `Insufficient balance` | Available vBTC < requested amount |
| `Amount/FeeRate must be greater than zero` | Invalid numeric input |
| `This node is not a validator` | Called `CompleteWithdrawal` directly on non-validator; use delegation automatically |
| `No active validators available for FROST signing` | No validators are currently online |
| `Insufficient validators` | Active count below adjusted threshold requirement |
| `Withdrawal request already completed` | Attempted to complete an already-finished withdrawal |
| `Invalid withdrawal details` | Amount/destination not found in contract or request record |
| `Bitcoin transaction failed` | FROST signing succeeded but BTC broadcast was rejected |
| `Failed to delegate withdrawal to any validator` | Wallet node tried all known validators; none accepted |
| `Request timestamp is too old` | Raw endpoint: request is older than 5 minutes |
| `Duplicate request detected` | Raw endpoint: `UniqueId` already used for this address + contract |
| `Invalid VFX signature` | Raw endpoint: signature verification failed |

---

## Complete Example: End-to-End Withdrawal

```bash
# Step 1: Request the withdrawal
curl -X POST http://localhost:17292/vbtcapi/vbtc/RequestWithdrawal \
  -H "Content-Type: application/json" \
  -d '{
    "SmartContractUID": "abc123def456:1773285700",
    "RequestorAddress": "xMyWalletAddress123",
    "BTCAddress": "tb1qmybitcoinaddress...",
    "Amount": 0.005,
    "FeeRate": 10
  }'
# → Save the returned "RequestHash"

# Step 2: Complete the withdrawal (triggers FROST signing)
curl -X POST http://localhost:17292/vbtcapi/vbtc/CompleteWithdrawal \
  -H "Content-Type: application/json" \
  -d '{
    "SmartContractUID": "abc123def456:1773285700",
    "WithdrawalRequestHash": "0x4d5e6f..."
  }'
# → Returns BTCTransactionHash once FROST signing succeeds
# → Bitcoin TX is now pending confirmation on the Bitcoin network
```
