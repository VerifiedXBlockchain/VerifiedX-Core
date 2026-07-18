# Accounts

> Base path: `/api/rest/Accounts`

## GET /Accounts

List all accounts with balances

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |

### Response

`200` Success

---

## POST /Accounts

Create a new address (returns the new private key material)

### Response

`200` Success

---

## POST /Accounts/import

Import a private key

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `privateKey` | string | Yes | minLength: 1 |
| `scan` | boolean | No | — |

### Response

`200` Success

---

## POST /Accounts/sync-balances

Sync all account balances from state

### Response

`200` Success

---

## GET /Accounts/{address}

Get account details by address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Accounts/{address}/balance

Get balance for an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Accounts/{address}/nfts

List NFTs owned by an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Accounts/{address}/nonce

Get address nonce

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Accounts/{address}/rescan

Rescan for transactions

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Accounts/{address}/validate

Validate an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success
