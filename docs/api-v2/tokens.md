# Tokens

> Base path: `/api/rest/Tokens`

## GET /Tokens/{scUID}

Get token info by scUID, or all tokens if getAll query param is true

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |
| `getAll` | query | boolean | No | — |

### Response

`200` Success

---

## POST /Tokens/{scUID}/ban

Ban an address from the token contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `banAddress` | string | Yes | minLength: 1 |
| `fromAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Tokens/{scUID}/burn

Burn tokens

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | Yes | — |
| `fromAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Tokens/{scUID}/mint

Mint new tokens (infinite supply only)

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | Yes | — |
| `fromAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Tokens/{scUID}/pause

Toggle pause on token contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Tokens/{scUID}/topics

Create a vote topic for a token community

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | Yes | minLength: 1 |
| `minimumVoteRequirement` | int64 | Yes | — |
| `topicDescription` | string | Yes | minLength: 1 |
| `topicName` | string | Yes | minLength: 1 |
| `votingEndDays` | VotingDays | Yes | — |

### Response

`200` Success

---

## POST /Tokens/{scUID}/topics/{topicUID}/vote

Cast a vote on a token topic

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |
| `topicUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | Yes | minLength: 1 |
| `voteType` | VoteType | Yes | — |

### Response

`200` Success

---

## POST /Tokens/{scUID}/transfer

Transfer tokens

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `amount` | double | Yes | — |
| `fromAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Tokens/{scUID}/transfer-ownership

Transfer token contract ownership

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `fromAddress` | string | Yes | minLength: 1 |
| `toAddress` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Tokens/{scUID}/votes

Get votes for a token contract

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success
