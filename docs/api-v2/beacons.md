# Beacons

> Base path: `/api/rest/Beacons`

## GET /Beacons

List all beacons

### Response

`200` Success

---

## POST /Beacons

Create a local beacon

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `autoDelete` | boolean | No | — |
| `fileCachePeriod` | int32 | No | — |
| `isPrivate` | boolean | No | — |
| `name` | string | Yes | minLength: 1 |
| `port` | int32 | No | — |

### Response

`200` Success

---

## POST /Beacons/add

Add a remote beacon

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `ipAddress` | string | Yes | minLength: 1 |
| `name` | string | Yes | minLength: 1 |
| `port` | int32 | No | — |

### Response

`200` Success

---

## GET /Beacons/assets/queue

Get asset queue

### Response

`200` Success

---

## GET /Beacons/info

Get local beacon info

### Response

`200` Success

---

## POST /Beacons/toggle

Toggle beacon active state

### Response

`200` Success

---

## DELETE /Beacons/{id}

Delete a beacon by ID

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `id` | path | int32 | Yes | — |

### Response

`200` Success
