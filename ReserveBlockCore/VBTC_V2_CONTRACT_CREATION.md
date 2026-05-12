# vBTC V2 Contract Creation Guide

## Overview

A **vBTC V2 contract** is a VFX smart contract that tokenizes Bitcoin using FROST (Flexible Round-Optimized Schnorr Threshold) multi-party computation. Each contract has a unique Bitcoin **Taproot deposit address** co-managed by the validator set — no single party holds the private key.

Creating a vBTC V2 contract is a two-step process:

1. **Initiate an MPC Ceremony** — Runs a FROST Distributed Key Generation (DKG) to produce a shared Taproot address.
2. **Create the Contract** — Publishes a VFX smart contract on-chain that references the DKG result (deposit address, group public key, DKG proof, validator snapshot).

Both validator nodes and regular wallet nodes can create vBTC V2 contracts. The difference is *how* the MPC ceremony runs.

---

## Path 1: Validator Node (Direct Local Ceremony)

When the requesting node **is a validator**, the ceremony runs entirely locally. The validator coordinates the FROST DKG with the other active validators directly.

### Flow Diagram

```
Validator Node
    │
    ├─ 1. POST InitiateMPCCeremony/{ownerAddress}
    │      → Anti-spam checks pass
    │      → Ceremony state created (in-memory)
    │      → Background task starts
    │
    ├─ 2. ExecuteMPCCeremonyLocally()
    │      → Fetches active validators from local DB
    │      → Validates 75% quorum requirement
    │      → Takes validator snapshot
    │      → Calls FrostMPCService.CoordinateDKGCeremony()
    │          ├─ Round 1: Commitment exchange
    │          ├─ Round 2: Share distribution
    │          └─ Round 3: Key aggregation
    │      → Stores result: Taproot address, group public key, DKG proof
    │      → Status → Completed
    │
    ├─ 3. GET GetCeremonyStatus/{ceremonyId}
    │      → Returns: Completed, DepositAddress, FrostGroupPublicKey, DKGProof
    │
    └─ 4. POST CreateVBTCContract (or CreateVBTCContractRaw)
           → Validates ceremony is complete
           → Builds SmartContractMain with TokenizationV2Feature
           → Writes smart contract code
           → Saves to databases
           → Broadcasts mint transaction
           → Marks contract as published
           → Removes ceremony from memory
```

### Step-by-Step API Calls

**Step 1: Initiate the ceremony**

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/InitiateMPCCeremony/{yourVFXAddress}
```

Response:
```json
{
  "Success": true,
  "Message": "MPC ceremony initiated successfully. Use GetCeremonyStatus to check progress.",
  "CeremonyId": "a1b2c3d4-...",
  "Status": "Initiated",
  "InitiatedTimestamp": 1773285600
}
```

**Step 2: Poll for completion**

```
GET http://localhost:{APIPort}/vbtcapi/vbtc/GetCeremonyStatus/{ceremonyId}
```

Response (when complete):
```json
{
  "Success": true,
  "Status": "Completed",
  "ProgressPercentage": 100,
  "DepositAddress": "tb1p...",
  "FrostGroupPublicKey": "02abc...",
  "DKGProof": "proof_data...",
  "ValidatorCount": 5,
  "RequiredThreshold": 51,
  "ProofBlockHeight": 1054100
}
```

**Step 3: Create the contract**

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CreateVBTCContract
Content-Type: application/json

{
  "OwnerAddress": "xYourVFXAddress...",
  "Name": "My vBTC Token",
  "Description": "MPC-secured tokenized Bitcoin",
  "Ticker": "vBTC",
  "CeremonyId": "a1b2c3d4-..."
}
```

Response:
```json
{
  "Success": true,
  "Message": "vBTC V2 contract created and published to blockchain successfully",
  "SmartContractUID": "abc123:1773285700",
  "TransactionHash": "0x...",
  "DepositAddress": "tb1p...",
  "FrostGroupPublicKey": "02abc...",
  "DKGProof": "proof_data...",
  "ValidatorCount": 5
}
```

---

## Path 2: Non-Validator Wallet Node (Delegated Ceremony)

When the requesting node **is not a validator** (a regular wallet), it cannot run FROST DKG directly because it lacks the validator signing credentials. Instead, it **delegates** the ceremony to an active validator on the network.

### Flow Diagram

```
Wallet Node                                          Remote Validator
    │                                                       │
    ├─ 1. POST InitiateMPCCeremony/{ownerAddress}          │
    │      → Anti-spam checks pass                          │
    │      → Ceremony state created locally                 │
    │      → Background task starts                         │
    │                                                       │
    ├─ 2. ExecuteMPCCeremonyViaRemoteValidator()            │
    │      → Discovers active validators from network       │
    │      → Tries each validator until one accepts:        │
    │          │                                            │
    │          ├─ POST InitiateMPCCeremony/{ownerAddress} ──┤
    │          │                                            ├─ Creates ceremony locally
    │          │                                            ├─ Runs FROST DKG (Round 1→2→3)
    │          │                                            │
    │      → Stores remote validator IP + remote ceremonyId │
    │      → Polls every 2 seconds (up to 4 minutes):      │
    │          │                                            │
    │          ├─ GET GetCeremonyStatus/{remoteCeremonyId} ─┤
    │          │  ← Status, Progress, Round, Results        │
    │          │                                            │
    │      → Syncs progress locally (status, %, round)      │
    │      → On Completed: syncs DepositAddress,            │
    │        FrostGroupPublicKey, DKGProof, ProofBlockHeight│
    │      → Local status → Completed                       │
    │                                                       │
    ├─ 3. GET GetCeremonyStatus/{localCeremonyId}           │
    │      → Returns: Completed with all synced results     │
    │                                                       │
    └─ 4. POST CreateVBTCContract                           │
           → Same as validator path from here               │
           → Contract created locally with ceremony results │
           → Ceremony removed from local memory             │
```

### Step-by-Step API Calls

The API calls from the wallet user's perspective are **identical** to the validator path. The delegation happens transparently in the background.

**Step 1: Initiate the ceremony** (same endpoint)

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/InitiateMPCCeremony/{yourVFXAddress}
```

The node automatically detects it's not a validator and delegates to a remote validator. The response is the same:

```json
{
  "Success": true,
  "CeremonyId": "e5f6g7h8-...",
  "Status": "Initiated"
}
```

**Step 2: Poll for completion** (same endpoint)

```
GET http://localhost:{APIPort}/vbtcapi/vbtc/GetCeremonyStatus/{ceremonyId}
```

Progress updates are synced from the remote validator in real time. You'll see the same status progression:
- `Initiated` → `ValidatingValidators` → `Round1InProgress` → `Round2InProgress` → `Round3InProgress` → `Completed`

**Step 3: Create the contract** (same endpoint)

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CreateVBTCContract
```

Same payload and response as the validator path.

### Key Differences (Behind the Scenes)

| Aspect | Validator Node | Wallet Node |
|--------|---------------|-------------|
| FROST DKG execution | Local (coordinates directly with validators) | Delegated to a remote validator |
| Validator discovery | Reads from local DB | Fetches from network peers via `FetchActiveValidatorsFromNetwork()` |
| Ceremony location | Runs in the same process | Runs on the remote validator's process |
| Progress tracking | Direct callback from FROST library | Polled via HTTP every 2 seconds |
| Max poll time | N/A | 4 minutes (120 polls × 2s) |
| Ceremony state fields | `IsRemote = false` | `IsRemote = true`, `RemoteValidatorIP`, `RemoteCeremonyId` |

---

## Raw Contract Creation (Pre-Signed Requests)

For external integrations (e.g., a separate frontend or mobile app), the `CreateVBTCContractRaw` endpoint accepts a **pre-signed** payload with replay attack protection.

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CreateVBTCContractRaw
Content-Type: application/json

{
  "OwnerAddress": "xYourVFXAddress...",
  "Name": "My vBTC Token",
  "Description": "MPC-secured tokenized Bitcoin",
  "Ticker": "vBTC",
  "CeremonyId": "a1b2c3d4-...",
  "Timestamp": 1773285700,
  "UniqueId": "unique-request-id-123",
  "OwnerSignature": "sig_of_all_fields..."
}
```

### Security Features
- **Timestamp validation**: Rejects requests older than 5 minutes
- **Signature verification**: Owner must sign all request fields with their VFX private key
- **Signature data format**: `{OwnerAddress}{Name}{Description}{Ticker}{CeremonyId}{Timestamp}{UniqueId}`

---

## Safety Mechanisms

### Anti-Spam Protection

| Guard | Limit | Scope |
|-------|-------|-------|
| **Per-owner limit** | 1 active ceremony per owner address | Per `ownerAddress` |
| **Global concurrent cap** | 100 active ceremonies max | Entire validator node |

If an owner already has an active ceremony, `InitiateMPCCeremony` returns:
```json
{
  "Success": false,
  "Message": "You already have an active MPC ceremony in progress. Complete or cancel it before starting a new one.",
  "ExistingCeremonyId": "...",
  "Status": "Round2InProgress",
  "ProgressPercentage": 65
}
```

### Cancel a Ceremony

An owner can cancel their ceremony at any time:

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/CancelCeremony/{ceremonyId}/{ownerAddress}
```

- Only the original `ownerAddress` can cancel
- The ceremony is immediately removed from memory
- If the ceremony was already in a terminal state (Completed/Failed/TimedOut), it is simply cleaned up

### Automatic Cleanup (1-Hour TTL)

The `CeremonyCleanupService` runs every 10 minutes on all nodes:

| Ceremony State | Action |
|----------------|--------|
| Active (non-terminal) and older than **1 hour** | Force-expired → `TimedOut` |
| Terminal (Completed/Failed/TimedOut) and older than **1 hour** | Removed from memory entirely |

This prevents memory leaks from abandoned ceremonies (e.g., a wallet that initiates a ceremony but never calls `CreateVBTCContract`).

### Immediate Removal After Contract Creation

When `CreateVBTCContract` or `CreateVBTCContractRaw` successfully publishes the smart contract, the ceremony is **immediately removed from memory**. The data has been persisted on-chain — no reason to keep it in RAM.

---

## Ceremony Status Lifecycle

```
Initiated
    │
    ▼
ValidatingValidators ──── (fails quorum check) ──→ Failed
    │
    ▼
Round1InProgress
    │
    ▼
Round2InProgress
    │
    ▼
Round3InProgress
    │
    ├── (success) ──→ Completed ──→ (used by CreateVBTCContract) ──→ Removed
    │
    ├── (error) ────→ Failed
    │
    └── (1hr TTL) ──→ TimedOut ──→ (cleanup) ──→ Removed
```

### Status Descriptions

| Status | Description |
|--------|-------------|
| `Initiated` | Ceremony created, background task starting |
| `ValidatingValidators` | Checking active validator count and quorum |
| `Round1InProgress` | FROST DKG Round 1: Commitment exchange |
| `Round2InProgress` | FROST DKG Round 2: Secret share distribution |
| `Round3InProgress` | FROST DKG Round 3: Key aggregation and proof |
| `Completed` | Taproot address generated successfully |
| `Failed` | An error occurred or ceremony was cancelled |
| `TimedOut` | Ceremony exceeded the 1-hour TTL |

---

## Complete Example: Wallet User Creates a vBTC Contract

```bash
# 1. Initiate ceremony (delegation happens automatically if not a validator)
curl -X POST http://localhost:17292/vbtcapi/vbtc/InitiateMPCCeremony/xMyWalletAddress123

# 2. Poll until complete (repeat until Status == "Completed")
curl http://localhost:17292/vbtcapi/vbtc/GetCeremonyStatus/a1b2c3d4-e5f6-...

# 3. Create the contract
curl -X POST http://localhost:17292/vbtcapi/vbtc/CreateVBTCContract \
  -H "Content-Type: application/json" \
  -d '{
    "OwnerAddress": "xMyWalletAddress123",
