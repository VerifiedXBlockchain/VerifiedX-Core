# Privacy

> Base path: `/api/rest/Privacy`

## GET /Privacy/addresses

List local shielded (zfx_) addresses

### Response

`200` Success

---

## POST /Privacy/addresses/from-account

Create a zfx_ shielded address from a transparent account's private key

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `transparentAddress` | string | No | — |
| `walletPassword` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/addresses/generate

Derive a zfx_ address from an HD seed (local HD wallet or explicit hex)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `addressIndex` | int32 | No | — |
| `coinType` | int32 | No | — |
| `useLocalHdWallet` | boolean | No | — |
| `walletSeedHex` | string | No | — |

### Response

`200` Success

---

## GET /Privacy/balance

Per-asset shielded balances from the local wallet row

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `zfxAddress` | query | string | No | — |
| `includeCommitments` | query | boolean | No | — |

### Response

`200` Success

---

## POST /Privacy/consolidate

Combine the two smallest unspent VFX notes into one (Z→Z to self)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## GET /Privacy/plonk-status

Native PLONK capabilities, strict proof flag, params mirror size

### Response

`200` Success

---

## GET /Privacy/pool-state

Pool state for an asset (e.g. VFX or VBTC:uid)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `asset` | query | string | No | — |

### Response

`200` Success

---

## POST /Privacy/private-transfer

Shielded VFX → shielded VFX (Z→Z)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `paymentAmount` | double | No | — |
| `recipientZfxAddress` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/resync

Wipe cached notes/balances and rescan from FromHeight (fixes corrupted balances)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromHeight` | int64 | No | — |
| `toHeight` | int64 | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/scan

Scan blocks for notes decryptable with the wallet encryption key

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromHeight` | int64 | No | — |
| `toHeight` | int64 | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/shield

Transparent VFX → shielded (T→Z). Signs with the local account key.

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | No | — |
| `memo` | string | No | — |
| `recipientZfxAddress` | string | No | — |
| `shieldAmount` | double | No | — |
| `transparentFee` | double | No | — |

### Response

`200` Success

---

## POST /Privacy/unshield

Shielded VFX → transparent (Z→T)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `transparentAmount` | double | No | — |
| `transparentToAddress` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## GET /Privacy/vbtc/balance

Shielded vBTC balance for a specific contract UID

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `zfxAddress` | query | string | No | — |
| `vbtcContractUid` | query | string | No | — |
| `includeCommitments` | query | boolean | No | — |

### Response

`200` Success

---

## POST /Privacy/vbtc/consolidate

Combine the two smallest unspent vBTC notes into one (Z→Z to self)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `vbtcContractUid` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## GET /Privacy/vbtc/pool-state

Pool state for a vBTC contract (asset key VBTC:{uid})

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `vbtcContractUid` | query | string | No | — |

### Response

`200` Success

---

## POST /Privacy/vbtc/private-transfer

Shielded vBTC → shielded vBTC (Z→Z). Requires vBTC inputs plus a VFX fee note.

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

## POST /Privacy/vbtc/resync

Wipe cached vBTC notes for a contract and rescan from FromHeight

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromHeight` | int64 | No | — |
| `toHeight` | int64 | No | — |
| `vbtcContractUid` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/vbtc/scan

Scan blocks for vBTC notes for a specific contract UID

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromHeight` | int64 | No | — |
| `toHeight` | int64 | No | — |
| `vbtcContractUid` | string | No | — |
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/vbtc/shield

Transparent vBTC → shielded (T→Z). Signs with the local account key.

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

## POST /Privacy/vbtc/unshield

Shielded vBTC → transparent (Z→T). Requires vBTC inputs plus a VFX fee note.

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

## POST /Privacy/viewing-key/export

Export the 32-byte viewing key (Base64) for watch-only import

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `walletPassword` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success

---

## POST /Privacy/viewing-key/import

Import a view-only wallet from a viewing key + zfx address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `transparentSourceAddress` | string | No | — |
| `viewingKeyBase64` | string | No | — |
| `zfxAddress` | string | No | — |

### Response

`200` Success
