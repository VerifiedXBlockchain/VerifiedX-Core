# SmartContracts

> Base path: `/api/rest/smart-contracts`

## GET /smart-contracts

List all smart contracts (paginated, searchable)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |
| `search` | query | string | No | — |

### Response

`200` Success

---

## POST /smart-contracts

Create a smart contract

### Response

`200` Success

---

## GET /smart-contracts/minted

List minted smart contracts with evolving features

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |
| `search` | query | string | No | — |

### Response

`200` Success

---

## GET /smart-contracts/{scUID}

Get smart contract details

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/burn

Burn an NFT

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /smart-contracts/{scUID}/data

Get on-chain smart contract data

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/devolve

Devolve an NFT

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

## POST /smart-contracts/{scUID}/evolve

Evolve an NFT

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

## POST /smart-contracts/{scUID}/mint

Mint/publish a smart contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /smart-contracts/{scUID}/ownership

Prove ownership of an NFT

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/ownership/verify

Verify an ownership script

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ownershipScript` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/sale

Start a sale for an NFT

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `backupURL` | string | No | — |
| `saleAmount` | double | Yes | — |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## DELETE /smart-contracts/{scUID}/sale

Cancel an NFT sale

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/sale/complete

Complete an NFT sale

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `keySign` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /smart-contracts/{scUID}/state

Get smart contract state

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /smart-contracts/{scUID}/transfer

Transfer an NFT

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
