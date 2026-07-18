# Voting

> Base path: `/api/rest/Voting`

## GET /Voting/my/topics

Get topics created by the current validator

### Response

`200` Success

---

## GET /Voting/my/votes

Get votes cast by the current validator

### Response

`200` Success

---

## GET /Voting/topics

List topics with optional status filter and pagination

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `Page` | query | int32 | No | — |
| `PageSize` | query | int32 | No | — |
| `status` | query | string | No | — |

### Response

`200` Success

---

## POST /Voting/topics

Create a new vote topic

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `topicDescription` | string | Yes | minLength: 1 |
| `topicName` | string | Yes | minLength: 1 |
| `voteTopicCategory` | VoteTopicCategories | Yes | — |
| `votingEndDays` | VotingDays | Yes | — |

### Response

`200` Success

---

## GET /Voting/topics/{topicUID}

Get topic details

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `topicUID` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Voting/topics/{topicUID}/vote

Cast a vote on a topic

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `topicUID` | path | string | Yes | — |

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `voteType` | VoteType | Yes | — |

### Response

`200` Success

---

## GET /Voting/topics/{topicUID}/votes

Get votes for a specific topic

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `topicUID` | path | string | Yes | — |

### Response

`200` Success
