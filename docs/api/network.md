# Network

> Base path: `/api/rest/Network`

## GET /Network

Network overview

### Response

`200` Success

---

## GET /Network/height

Current block height

### Response

`200` Success

---

## GET /Network/masternodes

List masternodes (validator pool)

### Response

`200` Success

---

## GET /Network/metrics

Block timing metrics

### Response

`200` Success

---

## GET /Network/peers

Connected peer info

### Response

`200` Success

---

## POST /Network/peers

Add a peer by IP

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ipAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Network/peers/banned

List banned peers

### Response

`200` Success

---

## POST /Network/peers/{ip}/ban

Ban a peer by IP

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ip` | path | string | Yes | — |

### Response

`200` Success

---

## DELETE /Network/peers/{ip}/ban

Unban a peer by IP

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `ip` | path | string | Yes | — |

### Response

`200` Success
