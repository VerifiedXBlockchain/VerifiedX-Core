# Blocks

> Base path: `/api/rest/Blocks`

## GET /Blocks

List recent blocks (paginated)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |

### Response

`200` Success

---

## GET /Blocks/hash/{hash}

Get block by hash

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `hash` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Blocks/latest

Get the latest block

### Response

`200` Success

---

## GET /Blocks/{height}

Get block by height

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `height` | path | int64 | Yes | — |

### Response

`200` Success
