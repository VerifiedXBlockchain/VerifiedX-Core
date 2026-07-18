# Bitcoin

> Base path: `/api/rest/Bitcoin`

## GET /Bitcoin/accounts

List Bitcoin accounts. Keys are omitted unless omitKeys=false is passed explicitly
(v1 defaults to including keys; v2 is safe-by-default).

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `omitKeys` | query | boolean | No | — |

### Response

`200` Success

---

## POST /Bitcoin/accounts

Create a new Bitcoin address (returns the new private key material)

### Response

`200` Success

---

## POST /Bitcoin/accounts/import

Import a Bitcoin address from a private key (hex or WIF)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `addressFormat` | string | No | — |
| `privateKey` | string | Yes | minLength: 50 |

### Response

`200` Success

---

## POST /Bitcoin/accounts/link-evm

Link (or clear) an EVM address on a BTC account

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `btcAddress` | string | Yes | minLength: 1 |
| `evmAddress` | string | No | — |

### Response

`200` Success

---

## POST /Bitcoin/accounts/reset

Reset Bitcoin accounts and their UTXOs (rate-limited to once per 5 minutes,
shared with the v1 endpoint via Globals.LastRanBTCReset)

### Response

`200` Success

---

## GET /Bitcoin/accounts/{address}

Get a Bitcoin account by address. Keys omitted unless omitKeys=false.

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |
| `omitKeys` | query | boolean | No | — |

### Response

`200` Success

---

## GET /Bitcoin/addresses/type-default

Default Bitcoin address type for new accounts

### Response

`200` Success

---

## GET /Bitcoin/addresses/{address}/transactions

Transaction list for an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Bitcoin/addresses/{address}/utxos

UTXO list for an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Bitcoin/adnr

Create a BTC ADNR and associate it to an address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `btcAddress` | string | Yes | minLength: 1 |
| `name` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Bitcoin/adnr/delete

Permanently remove a BTC ADNR from an address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `btcFromAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Bitcoin/adnr/transfer

Transfer a BTC ADNR to another address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `btcFromAddress` | string | Yes | minLength: 1 |
| `btcToAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Bitcoin/base-balances

ETH + vBTC.b balances on Base for each BTC account with a linked EVM address

### Response

`200` Success

---

## GET /Bitcoin/default-image

Default vBTC image (Base64)

### Response

`200` Success

---

## GET /Bitcoin/electrumx/state

ElectrumX connection state

### Response

`200` Success

---

## GET /Bitcoin/sync/last

Last account sync time and next scheduled check

### Response

`200` Success

---

## GET /Bitcoin/sync/status

BTC chain sync status

### Response

`200` Success

---

## POST /Bitcoin/tokenize

Tokenize Bitcoin (mints a vBTC v1 arbiter-model token contract)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `description` | string | No | — |
| `features` | SmartContractFeatures[] | No | — |
| `fileLocation` | string | No | — |
| `name` | string | No | — |
| `rbxAddress` | string | No | — |

### Response

`200` Success

---

## GET /Bitcoin/tokenize/details/{vfxAddress}

Get tokenization details (arbiter deposit address + proof) for a VFX address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `vfxAddress` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Bitcoin/tokenize/list

List tokenized BTC contracts

### Response

`200` Success

---

## POST /Bitcoin/tokenize/{scUID}/transfer-ownership

Transfer ownership of a tokenized BTC contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `backupURL` | string | No | — |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Bitcoin/tokenized-balances/{address}

All tokenized BTC balances and contract IDs for an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Bitcoin/tokenized-balances/{address}/{scUID}

Tokenized BTC balance for an address in a specific contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Bitcoin/transactions

All local Bitcoin transactions.
Parity note: v1's includeTokens=false branch computes a filtered list and then
discards it — every call returns all TXs. Mirrored; the parameter is accepted
for interface parity only.

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `includeTokens` | query | boolean | No | — |

### Response

`200` Success

---

## POST /Bitcoin/transactions/broadcast

Broadcast a signed transaction hex

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `hex` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Bitcoin/transactions/fee

Calculate the fee for a prospective transaction

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `from` | query | string | No | — |
| `to` | query | string | No | — |
| `amount` | query | double | No | — |
| `feeRate` | query | int32 | No | — |

### Response

`200` Success

---

## POST /Bitcoin/transactions/send

Send a Bitcoin transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `feeRate` | int32 | No | min: 1, max: 2147483647 |
| `fromAddress` | string | Yes | minLength: 1 |
| `overrideInternalSend` | boolean | No | — |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Bitcoin/transactions/{txid}/rebroadcast

Rebroadcast a locally known transaction by txid

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `txid` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Bitcoin/transactions/{txid}/replace

Replace a transaction by fee (RBF)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `txid` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `feeRate` | int32 | No | min: 1, max: 2147483647 |

### Response

`200` Success

---

## POST /Bitcoin/transfer

Transfer tokenized BTC to a VFX address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `chosenFeeRate` | int64 | No | — |
| `fromAddress` | string | No | — |
| `scuid` | string | No | — |
| `toAddress` | string | No | — |

### Response

`200` Success

---

## POST /Bitcoin/transfer-multi

Transfer tokenized BTC to multiple recipients

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | No | — |
| `toAddress` | string | No | — |
| `vBTCInputAmount` | double | No | — |
| `vBTCInputs` | VBTCTransferInput[] | No | — |

### Response

`200` Success

---

## POST /Bitcoin/withdraw

Withdraw tokenized BTC to a Bitcoin address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `chosenFeeRate` | int64 | No | — |
| `fromAddress` | string | No | — |
| `scuid` | string | No | — |
| `toAddress` | string | No | — |

### Response

`200` Success

---

## POST /Bitcoin/withdraw/raw

Withdraw tokenized BTC with a pre-signed external request

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | No | — |
| `btcToAddress` | string | No | — |
| `chosenFeeRate` | int64 | No | — |
| `isTest` | boolean | No | — |
| `smartContractUID` | string | No | — |
| `timestamp` | int64 | No | — |
| `uniqueId` | string | No | — |
| `vfxAddress` | string | No | — |
| `vfxSignature` | string | No | — |

### Response

`200` Success
