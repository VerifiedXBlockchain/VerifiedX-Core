# Signatures

> Base path: `/api/rest/Signatures`

## POST /Signatures

Create a signature (address + message in body)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `message` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Signatures/verify

Verify a signature (all params in body)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `message` | string | Yes | minLength: 1 |
| `signature` | string | Yes | minLength: 1 |

### Response

`200` Success
