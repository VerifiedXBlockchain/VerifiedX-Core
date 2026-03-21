# Validators

> Base path: `/api/rest/Validators`

## GET /Validators

List validator-eligible accounts

### Response

`200` Success

---

## PUT /Validators/name

Change validator name

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `uniqueName` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Validators/pool

Get network validator pool

### Response

`200` Success

---

## POST /Validators/register

Register as a validator

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `uniqueName` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Validators/reset

Reset validator

### Response

`200` Success

---

## POST /Validators/start

Start validating (turn on existing validator)

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Validators/status

Check if currently validating

### Response

`200` Success

---

## POST /Validators/stop

Stop validating

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Validators/{address}

Get validator info by address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success
