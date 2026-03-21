# Adnr

> Base path: `/api/rest/Adnr`

## POST /Adnr

Create a domain name

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `name` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Adnr/resolve/{name}

Resolve a domain name to an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `name` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Adnr/reverse/{address}

Reverse lookup — address to domain name

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Adnr/transfer

Transfer ADNR to another address

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## DELETE /Adnr/{address}

Delete ADNR from an address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success
