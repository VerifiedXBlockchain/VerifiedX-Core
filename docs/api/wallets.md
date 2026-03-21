# Wallets

> Base path: `/api/rest/Wallets`

## POST /Wallets/encrypt

Encrypt the wallet with a password

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Wallets/encryption-status

Check encryption state

### Response

`200` Success

---

## POST /Wallets/hd

Create an HD wallet

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `strength` | int32 | Yes | min: 12, max: 24 |

### Response

`200` Success

---

## GET /Wallets/info

Wallet status including sync, peers, version

### Response

`200` Success

---

## POST /Wallets/lock

Lock the wallet (clear encryption password from memory)

### Response

`200` Success

---

## GET /Wallets/status

Health check — no auth required

### Response

`200` Success

---

## POST /Wallets/unlock

Unlock an encrypted wallet

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `password` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Wallets/version

CLI version string

### Response

`200` Success
