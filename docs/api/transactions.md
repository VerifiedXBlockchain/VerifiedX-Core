# Transactions

> Base path: `/api/rest/Transactions`

## GET /Transactions

List local transactions (filterable by status, paginated)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `status` | query | string | No | — |
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |

### Response

`200` Success

---

## POST /Transactions

Send a raw transaction

### Response

`200` Success

---

## POST /Transactions/fee

Estimate transaction fee

### Response

`200` Success

---

## POST /Transactions/hash

Calculate transaction hash

### Response

`200` Success

---

## GET /Transactions/mempool

Get mempool transactions

### Response

`200` Success

---

## GET /Transactions/search/{hash}

Search full chain for a transaction by hash

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `hash` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Transactions/send

Simple send (from, to, amount in body)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | Yes | — |
| `fromAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Transactions/verify

Verify a raw transaction

### Response

`200` Success

---

## GET /Transactions/{hash}

Get transaction by hash (local)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `hash` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Transactions/{hash}/replace

Replace transaction by fee

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `hash` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `newFee` | double | Yes | — |

### Response

`200` Success
