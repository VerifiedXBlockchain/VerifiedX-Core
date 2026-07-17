# vBTC V2 Transfer Guide

## Overview

A **vBTC V2 transfer** moves tokenized Bitcoin balance between VFX addresses entirely on the VFX chain — no Bitcoin transaction is required. The vBTC balance lives in the smart contract's state ledger; transferring it updates that ledger without touching the underlying Bitcoin held in the Taproot deposit address.

There are two distinct transfer concepts:

1. **Token Transfer** — Send a vBTC balance amount to another VFX address (either the contract owner sending to a recipient, or a recipient forwarding to someone else).
2. **Contract Ownership Transfer** — Transfer the entire vBTC V2 smart contract (including its deposit address control) to a new owner address.

---

## Concept: Balance Accounting

vBTC V2 uses a two-layer balance model:

| Role | Available Balance |
|------|-----------------|
| **Contract owner** | BTC deposit balance + ledger delta |
| **Non-owner recipient** | Ledger delta only (received − sent) |

The **deposit balance** is the actual BTC held at the Taproot address (scanned via Electrum every 5 minutes). The **ledger** is the `SCStateTreiTokenizationTXes` list stored in the smart contract state — it records every on-chain transfer.

When the owner sends 0.5 vBTC to Alice, the ledger records:
- `FromAddress = owner, ToAddress = Alice, Amount = -0.5` (owner's ledger goes down)
- Alice's ledger balance becomes +0.5, which she can transfer or withdraw

---

## Part 1: Token Transfer (Balance to Balance)

### Flow Diagram

```
Sender Node
    │
    ├─ 1. POST TransferVBTC
    │      → Validate payload (SCUID, FromAddress, ToAddress, Amount > 0)
    │      → Load account (must own FromAddress locally)
    │      → Load VBTCContractV2 from local DB
    │      → Load SmartContractStateTrei (shared consensus state)
    │      → Calculate available balance:
    │          ├─ If owner: depositBalance + ledgerDelta
    │          └─ If non-owner: ledgerDelta (received + sent)
    │      → Check: availableBalance >= Amount
    │      → Build VBTC_V2_TRANSFER transaction
    │          Data: { Function, ContractUID, FromAddress, ToAddress, Amount }
    │          Amount (VFX) = 0  ← no VFX moved, only vBTC
    │          Fee = calculated from TX size
    │      → Sign with FromAddress private key
    │      → VerifyTX (local validation)
    │      → AddTxToWallet → AddToPool → SendTXMempool (P2P broadcast)
    │      → Return: { Success, TransactionHash, From, To, Amount }
    │
    └─ Network processes TX → StateData updates SCStateTreiTokenizationTXes
```

### Step-by-Step API Call

**Transfer vBTC tokens:**

```
POST http://localhost:{APIPort}/vbtcapi/vbtc/TransferVBTC
Content-Type: application/json

{
  "SmartContractUID": "abc123def456:1773285700",
  "FromAddress": "xSenderVFXAddress...",
  "ToAddress": "xRecipientVFXAddress...",
  "Amount": 0.5
}
```

Response (success):
```json
{
  "Success": true,
  "Message": "vBTC V2 transfer transaction created and broadcast successfully",
  "TransactionHash": "0x1a2b3c...",
  "From": "xSenderVFXAddress...",
  "To": "xRecipientVFXAddress...",
  "Amount": 0.5,
  "SmartContractUID": "abc123def456:1773285700"
}
```

Response (insufficient balance):
```json
{
  "Success": false,
  "Message": "Insufficient balance. Available: 0.3, Requested: 0.5"
}
```

### Balance Rules

- The `FromAddress` account **must exist locally** on the node making the call. You cannot send from an address your node doesn't own.
- Amount must be `> 0`.
- `ToAddress` is normalized before use (whitespace removed, address format standardized).
- Transaction fee is paid in VFX by the sender; the vBTC amount itself is free to transfer.

---

## Part 2: Contract Ownership Transfer

This transfers the **entire vBTC V2 smart contract** — including control of the deposit address — to a new VFX owner. It follows the standard VFX NFT/smart contract transfer protocol via a beacon upload.

### When to Use

- Selling or gifting a vBTC V2 contract to another party
- Moving contract control between wallets you own
- Any scenario where the new owner should be the one to authorize future withdrawals

### Requirements

- Contract must have **balance > 0** (cannot transfer an empty contract)
- Your node must have **beacon connectivity**
- The `FromAddress` (current owner) must exist locally

### Flow Diagram

```
Current Owner Node
    │
    ├─ 1. GET TransferOwnership/{scUID}/{toAddress}
    │      → Load VBTCContractV2 and SmartContractMain
    │      → Validate TokenizationV2 feature exists
    │      → Get SmartContractStateTrei → verify current owner account exists locally
    │      → Validate balance > 0 (deposit + ledger must be positive)
    │      → Check beacon connectivity
    │          └─ If no beacons: attempt EstablishBeaconConnection()
    │      → Normalize toAddress
    │      → Get contract assets + MD5 hash list
    │
    ├─ 2a. Remote recipient (not a local account):
    │      → BeaconUploadRequest (10-second timeout)
    │      → If upload fails → return error
    │
    ├─ 2b. Local recipient (address exists on this node):
    │      → Skip beacon upload (no upload needed)
    │
    └─ 3. On upload success:
           → CreateAssetQueueItem (TransferType.Upload)
           → SmartContractService.TransferSmartContract (background task)
               → Builds TKNZ_TX transaction
               → Signs and broadcasts to network
           → Return: { Success, Message }
```

### Step-by-Step API Call

```
GET http://localhost:{APIPort}/vbtcapi/vbtc/TransferOwnership/{scUID}/{toAddress}
```

Example:
```
GET http://localhost:17292/vbtcapi/vbtc/TransferOwnership/abc123def456:1773285700/xNewOwnerAddress...
```

Response:
```json
{
  "Success": true,
  "Message": "vBTC V2 Contract Transfer has been started."
}
```

Response (zero balance):
```json
{
  "Success": false,
  "Message": "Cannot transfer a token with zero balance."
}
```

Response (no beacon):
```json
{
  "Success": false,
  "Message": "Error - You failed to connect to any beacons."
}
```

### Notes

- The transfer is **asynchronous** — the `SmartContractService.TransferSmartContract` runs in a background task. The response `"Transfer has been started"` means the process was queued, not that it completed.
- The new owner receives the contract via the beacon network and their node processes the incoming `TKNZ_TX` transaction.
- After transfer, the new owner's address becomes the `OwnerAddress` in the contract state. All future withdrawals must be initiated by the new owner.

---

## Transaction Types

| Operation | Transaction Type | VFX Amount |
|-----------|-----------------|------------|
| Token balance transfer | `VBTC_V2_TRANSFER` | 0 (fee only) |
| Contract ownership transfer | `TKNZ_TX` | 0 (fee only) |

---

## Error Reference

| Error | Cause |
|-------|-------|
| `Account not found` | `FromAddress` does not exist on this node |
| `vBTC V2 contract not found` | `SmartContractUID` not in local DB |
| `Smart contract state not found` | Consensus state missing (node not synced) |
| `Insufficient balance` | Available balance < requested amount |
| `Required fields cannot be null` | Missing SCUID, FromAddress, or ToAddress |
| `Amount must be greater than zero` | Amount was 0 or negative |
| `TX Signature Failed` | Private key unavailable or signing error |
| `TX Verify Failed` | Local transaction validation rejected the TX |
| `Cannot transfer a token with zero balance` | Contract has no BTC balance (ownership transfer) |
| `You do not have any beacons stored` | Node has no beacon records (ownership transfer) |
| `Failed to connect to any beacons` | All beacon connections refused (ownership transfer) |

---

## Complete Example: Send vBTC to Another User

```bash
# Transfer 0.25 vBTC from your address to a recipient
curl -X POST http://localhost:17292/vbtcapi/vbtc/TransferVBTC \
  -H "Content-Type: application/json" \
  -d '{
    "SmartContractUID": "abc123def456:1773285700",
    "FromAddress": "xMyWalletAddress123",
    "ToAddress": "xFriendAddress456",
    "Amount": 0.25
  }'
```

After the transaction is confirmed on the VFX chain, the recipient can:
- Check their balance via `GetContractBalance`
- Transfer it further to someone else
- Withdraw it to a Bitcoin address (see `VBTC_V2_WITHDRAWAL.md`)
