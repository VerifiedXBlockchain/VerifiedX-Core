# Vbtc

> Base path: `/api/rest/Vbtc`

## GET /Vbtc/balances/{address}

All vBTC balances for an address across contracts

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/balances/{address}/{scUID}

vBTC balance for an address in a specific contract. Owner balance includes the
live BTC deposit-address balance (ElectrumX) plus the tokenization ledger.

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/base-address/{vfxAddress}

Deterministic Base (EVM) address for a VFX address (Keccak256 of the secp256k1 key)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `vfxAddress` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/base-balance/{evmAddress}

vBTC.b balance on Base for an EVM address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `evmAddress` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/config

Contract address, chainId, and ABI the frontend needs to call mintWithProof

### Response

`200` Success

---

## GET /Vbtc/bridge/contracts/{scUID}/locks

All bridge locks for a contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/locks/{lockId}

Bridge lock status by lock ID

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `lockId` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/locks/{lockId}/attestation

In-memory mint attestation progress for a lock ID on this node

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `lockId` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Vbtc/bridge/locks/{lockId}/force-retry

Force-retry a stuck bridge mint: reconstructs local tracking from on-chain state
if missing, checks the Base contract, then collects fresh attestations

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `lockId` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownerAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/bridge/locks/{lockId}/retry

Retry a failed bridge mint for a lock

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `lockId` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownerAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Vbtc/bridge/owners/{ownerAddress}/locks

All bridge locks for an owner address (across contracts)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ownerAddress` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/preflight/{ownerAddress}/{scUID}

Bridge preflight for an owner + contract: balance, gas, config readiness

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ownerAddress` | path | string | Yes | — |
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/bridge/status

Bridge configuration and operational status

### Response

`200` Success

---

## POST /Vbtc/bridge/to-base

Lock vBTC for bridging to Base (broadcasts a VBTC_V2_BRIDGE_LOCK signed by the local wallet)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `evmDestination` | string | Yes | minLength: 1 |
| `ownerAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/ceremonies

Initiate an MPC (FROST DKG) ceremony to generate a deposit address.
Runs in the background — poll GET ceremonies/{ceremonyId} for progress.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `forcePublic` | boolean | No | — |
| `ownerAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/ceremonies/execute-raw

Execute an MPC ceremony using pre-signed leader authentication from prepare-raw

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ceremonyId` | string | Yes | minLength: 1 |
| `ownerAddress` | string | Yes | minLength: 1 |
| `sessionId` | string | Yes | minLength: 1 |
| `shareDistributionSignature` | string | Yes | minLength: 1 |
| `shareDistributionTimestamp` | int64 | No | — |
| `startSignature` | string | Yes | minLength: 1 |
| `startTimestamp` | int64 | No | — |

### Response

`200` Success

---

## POST /Vbtc/ceremonies/prepare-raw

Prepare an MPC ceremony for web-wallet use: probes validators and returns the
exact leader-auth messages the wallet must sign, then call execute-raw.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownerAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Vbtc/ceremonies/{ceremonyId}

Status of an ongoing or completed MPC ceremony (v2-initiated ceremonies only)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ceremonyId` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Vbtc/ceremonies/{ceremonyId}/cancel

Cancel an active MPC ceremony. Only the original owner can cancel.

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ceremonyId` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownerAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Vbtc/contracts

List vBTC contracts, optionally filtered by owner address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `owner` | query | string | No | — |

### Response

`200` Success

---

## POST /Vbtc/contracts

Create a vBTC contract from a completed (v2-initiated) MPC ceremony.
Signs and broadcasts the mint transaction with the local wallet key.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ceremonyId` | string | Yes | minLength: 1 |
| `description` | string | No | — |
| `imageBase` | string | No | — |
| `linkedContractUID` | string | No | — |
| `name` | string | Yes | minLength: 1 |
| `ownerAddress` | string | Yes | minLength: 1 |
| `ticker` | string | No | — |

### Response

`200` Success

---

## POST /Vbtc/contracts/raw

Create a vBTC contract from a pre-signed external request (signature +
timestamp + replay protection). Still signs the mint TX with the local wallet key.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ceremonyId` | string | Yes | minLength: 1 |
| `description` | string | No | — |
| `imageBase` | string | No | — |
| `linkedContractUID` | string | No | — |
| `name` | string | Yes | minLength: 1 |
| `ownerAddress` | string | Yes | minLength: 1 |
| `ownerSignature` | string | No | — |
| `ticker` | string | No | — |
| `timestamp` | int64 | No | — |
| `uniqueId` | string | No | — |

### Response

`200` Success

---

## POST /Vbtc/contracts/raw-tx

Build an unsigned VBTC_V2_CONTRACT_CREATE transaction for offline signing.
Requires a completed (v2-initiated) MPC ceremony.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ceremonyId` | string | Yes | minLength: 1 |
| `description` | string | No | — |
| `imageBase` | string | No | — |
| `linkedContractUID` | string | No | — |
| `name` | string | Yes | minLength: 1 |
| `ownerAddress` | string | Yes | minLength: 1 |
| `ownerSignature` | string | No | — |
| `ticker` | string | No | — |
| `timestamp` | int64 | No | — |
| `uniqueId` | string | No | — |

### Response

`200` Success

---

## POST /Vbtc/contracts/raw-tx/send

Submit a pre-signed VBTC_V2_CONTRACT_CREATE transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hash` | string | Yes | minLength: 1 |
| `publicKey` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}

Get vBTC contract details

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}/deposit-address

Get the MPC-generated BTC deposit address for a vBTC contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}/health

Contract health: how many of the original DKG validators are still online,
and whether FROST withdrawals can currently be processed

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}/ownership-transfer-data/{toAddress}/{locator}

Ownership-transfer TX data for raw transaction building (web wallet flow).
Locator comes from the beacon upload request endpoint.

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |
| `toAddress` | path | string | Yes | — |
| `locator` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Vbtc/contracts/{scUID}/transfer-ownership

Transfer ownership of a vBTC contract to another address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}/withdrawal-status

Current withdrawal status for a contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/contracts/{scUID}/withdrawals

Withdrawal history for a contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Vbtc/default-image

Default vBTC image metadata (Base64)

### Response

`200` Success

---

## POST /Vbtc/private-transfer

Shielded-to-shielded vBTC transfer (Z→Z)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `paymentAmount` | double | No | — |
| `recipientZfxAddress` | string | No | — |
| `vbtcContractUid` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Vbtc/shield

Shield transparent vBTC into the shielded pool (T→Z). Signs with the local wallet.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | No | — |
| `memo` | string | No | — |
| `recipientZfxAddress` | string | No | — |
| `transparentFee` | double | No | — |
| `vbtcAmount` | double | No | — |
| `vbtcContractUid` | string | No | — |

### Response

`200` Success

---

## GET /Vbtc/shielded-balance

Shielded vBTC balance for a zfx address and contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `zfxAddress` | query | string | No | — |
| `scUID` | query | string | No | — |

### Response

`200` Success

---

## GET /Vbtc/shielded-pool/{scUID}

Shielded pool state for a vBTC contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Vbtc/transfers

Transfer vBTC (signs and broadcasts with the local wallet)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `fromAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/transfers/raw-tx

Build an unsigned VBTC_V2_TRANSFER transaction for offline signing

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `fromAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/transfers/raw-tx/send

Submit a pre-signed VBTC_V2_TRANSFER transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hash` | string | Yes | minLength: 1 |
| `publicKey` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/unshield

Unshield vBTC back to a transparent address (Z→T)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `transparentToAddress` | string | No | — |
| `transparentVbtcAmount` | double | No | — |
| `vbtcContractUid` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## GET /Vbtc/validators

List registered vBTC validators

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `activeOnly` | query | boolean | No | — |

### Response

`200` Success

---

## GET /Vbtc/validators/{validatorAddress}

Validator status and details

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `validatorAddress` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Vbtc/withdrawals

Request withdrawal of vBTC to a Bitcoin address (signs with the local wallet)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcAddress` | string | Yes | minLength: 1 |
| `feeRate` | int32 | No | min: 1, max: 2147483647 |
| `requestorAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/cancel

Request cancellation of a failed withdrawal (creates a validator-voted cancellation record)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `btcTxHash` | string | No | — |
| `failureProof` | string | No | — |
| `ownerAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/cancel-raw

Cancel a withdrawal with pre-signed owner authorization (validator-voted)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `btcTxHash` | string | No | — |
| `failureProof` | string | No | — |
| `ownerAddress` | string | Yes | minLength: 1 |
| `ownerSignature` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `timestamp` | int64 | No | — |
| `uniqueId` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/cancel-raw-tx

Build an unsigned VBTC_V2_WITHDRAWAL_CANCEL transaction for offline signing

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `requestorAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/cancel-raw-tx/send

Submit a pre-signed VBTC_V2_WITHDRAWAL_CANCEL transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hash` | string | Yes | minLength: 1 |
| `publicKey` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/cancel-tx

Cancel an active withdrawal by broadcasting a VBTC_V2_WITHDRAWAL_CANCEL
transaction signed with the local wallet (distinct from the validator-voted
cancellation record flow at withdrawals/cancel)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `requestorAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/complete

Complete a withdrawal: coordinates FROST MPC signing and broadcasts the BTC transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/complete-raw-tx

Build an unsigned VBTC_V2_WITHDRAWAL_COMPLETE transaction for offline signing
(records the completion on the VFX chain after the BTC broadcast)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcDestination` | string | Yes | minLength: 1 |
| `btcTransactionHash` | string | Yes | minLength: 1 |
| `fromAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/complete-raw-tx/send

Submit a pre-signed VBTC_V2_WITHDRAWAL_COMPLETE transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hash` | string | Yes | minLength: 1 |
| `publicKey` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/complete-raw/execute

Execute a complete-withdrawal using pre-signed leader authentication.
Returns the FROST-signed BTC transaction hex for the wallet to broadcast.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcDestination` | string | No | — |
| `feeRate` | int32 | No | — |
| `ownerAddress` | string | Yes | minLength: 1 |
| `sessionId` | string | Yes | minLength: 1 |
| `shareDistributionSignature` | string | Yes | minLength: 1 |
| `shareDistributionTimestamp` | int64 | No | — |
| `smartContractUID` | string | Yes | minLength: 1 |
| `startSignature` | string | Yes | minLength: 1 |
| `startTimestamp` | int64 | No | — |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/complete-raw/prepare

Prepare a complete-withdrawal FROST signing session for a web wallet.
Returns the exact leader-auth messages the wallet must sign; then call complete-raw/execute.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownerAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `withdrawalRequestHash` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/raw

Request withdrawal with a pre-signed external request (signature, timestamp,
and replay protection). Saves the request without a local wallet key.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcAddress` | string | Yes | minLength: 1 |
| `feeRate` | int32 | No | — |
| `isTest` | boolean | No | — |
| `smartContractUID` | string | Yes | minLength: 1 |
| `timestamp` | int64 | No | — |
| `uniqueId` | string | Yes | minLength: 1 |
| `vfxAddress` | string | Yes | minLength: 1 |
| `vfxSignature` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/request-raw-tx

Build an unsigned VBTC_V2_WITHDRAWAL_REQUEST blockchain transaction for offline signing

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcAddress` | string | Yes | minLength: 1 |
| `feeRate` | int32 | No | min: 1, max: 2147483647 |
| `requestorAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Vbtc/withdrawals/request-raw-tx/send

Submit a pre-signed VBTC_V2_WITHDRAWAL_REQUEST transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hash` | string | Yes | minLength: 1 |
| `publicKey` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success
