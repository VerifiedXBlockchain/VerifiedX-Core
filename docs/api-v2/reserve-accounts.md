# ReserveAccounts

> Base path: `/api/rest/reserve-accounts`

## GET /reserve-accounts

List all reserve accounts

### Response

`200` Success

---

## POST /reserve-accounts

Create a new reserve account

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |
| `storeRecoveryAccount` | boolean | No | — |

### Response

`200` Success

---

## POST /reserve-accounts/restore

Restore a reserve account from restore code

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `onlyRestoreRecovery` | boolean | No | — |
| `password` | string | Yes | minLength: 1 |
| `rescanForTx` | boolean | No | — |
| `restoreCode` | string | Yes | minLength: 1 |
| `storeRecoveryAccount` | boolean | No | — |

### Response

`200` Success

---

## POST /reserve-accounts/send

Send a reserve transaction

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | Yes | — |
| `decryptPassword` | string | Yes | minLength: 1 |
| `fromAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |
| `unlockDelayHours` | int32 | No | — |

### Response

`200` Success

---

## POST /reserve-accounts/transfer-nft

Transfer an NFT from a reserve account

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `backupURL` | string | No | — |
| `decryptPassword` | string | Yes | minLength: 1 |
| `fromAddress` | string | Yes | minLength: 1 |
| `smartContractUID` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |
| `unlockDelayHours` | int32 | No | — |

### Response

`200` Success

---

## GET /reserve-accounts/{address}

Get reserve account info

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## POST /reserve-accounts/{address}/publish

Publish a reserve account to the network

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /reserve-accounts/{address}/recover

Recover a reserve account

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |
| `recoveryPhrase` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /reserve-accounts/{address}/unlock

Unlock a reserve account

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |
| `unlockTime` | int32 | No | min: 0, max: 2147483647 |

### Response

`200` Success

---

## POST /reserve-accounts/{hash}/callback

Callback a reserve account transaction

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `hash` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |

### Response

`200` Success
