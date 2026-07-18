# Shops

> Base path: `/api/rest/Shops`

## GET /Shops

Get local shop info

### Response

`200` Success

---

## POST /Shops

Create or update a local shop

### Response

`200` Success

---

## DELETE /Shops

Delete shop from network

### Response

`200` Success

---

## GET /Shops/auctions/{listingId}

Get auction by listing ID

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/auctions/{listingId}/reset

Reset auction ended state

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/bids

Send a bid

### Response

`200` Success

---

## POST /Shops/bids/buy-now

Send a buy-now bid

### Response

`200` Success

---

## GET /Shops/bids/listing/{listingId}/{sendReceive}

Get bids for a specific listing

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |
| `sendReceive` | path | BidSendReceive | Yes | — |

### Response

`200` Success

---

## GET /Shops/bids/status/{bidStatus}/{sendReceive}

Get bids by status

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `bidStatus` | path | BidStatus | Yes | — |
| `sendReceive` | path | BidSendReceive | Yes | — |

### Response

`200` Success

---

## GET /Shops/bids/{bidId}

Get a single bid

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `bidId` | path | string (uuid) | Yes | — |

### Response

`200` Success

---

## POST /Shops/bids/{bidId}/resend

Resend an existing bid

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `bidId` | path | string (uuid) | Yes | — |

### Response

`200` Success

---

## GET /Shops/bids/{sendReceive}

Get all bids

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `sendReceive` | path | BidSendReceive | Yes | — |

### Response

`200` Success

---

## POST /Shops/chat

Send a chat message to a connected shop

### Response

`200` Success

---

## GET /Shops/chat/messages

Get detailed chat messages for a shop URL

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `shopUrl` | query | string | No | — |

### Response

`200` Success

---

## GET /Shops/chat/messages/recent/{key}

Get most recent chat messages by key

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `key` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/chat/messages/simple

Get simplified chat messages for a shop URL

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `shopUrl` | query | string | No | — |

### Response

`200` Success

---

## GET /Shops/chat/messages/summary

Get chat message summary for shop owner

### Response

`200` Success

---

## DELETE /Shops/chat/messages/{key}

Delete chat messages by key

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `key` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/chat/messages/{messageId}

Get a specific chat message by ID

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `messageId` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Shops/chat/shop

Send a chat message from a shop owner

### Response

`200` Success

---

## GET /Shops/chat/shop-messages/detailed

Get detailed shop chat messages (for shop owner)

### Response

`200` Success

---

## GET /Shops/chat/shop-messages/simple

Get simple shop chat messages (for shop owner)

### Response

`200` Success

---

## GET /Shops/chat/shop-messages/{vfxAddress}

Get detailed shop chat messages for a specific address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `vfxAddress` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/chat/shop-messages/{vfxAddress}/simple

Get simple shop chat messages for a specific address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `vfxAddress` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Shops/chat/{messageId}/resend

Resend a chat message

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `messageId` | path | string | Yes | — |
| `shopUrl` | query | string | No | — |

### Response

`200` Success

---

## GET /Shops/collections

List all collections

### Response

`200` Success

---

## POST /Shops/collections

Create or update a collection

### Response

`200` Success

---

## GET /Shops/collections/default

Get the default collection

### Response

`200` Success

---

## GET /Shops/collections/{collectionId}

Get a single collection

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `collectionId` | path | int32 | Yes | — |

### Response

`200` Success

---

## DELETE /Shops/collections/{collectionId}

Delete a collection and all associated data

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `collectionId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/collections/{collectionId}/set-default

Change the default collection

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `collectionId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/connect

Connect to a remote shop

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `address` | string | Yes | minLength: 1 |
| `url` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## GET /Shops/connections

Get current shop connections

### Response

`200` Success

---

## GET /Shops/data

Get cached shop data from memory

### Response

`200` Success

---

## GET /Shops/debug

Get DST debug data

### Response

`200` Success

---

## GET /Shops/debug/data

Get shop statistics

### Response

`200` Success

---

## POST /Shops/import/{address}

Import a shop from the network by owner address

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `address` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Shops/listings

Create or update a listing

### Response

`200` Success

---

## GET /Shops/listings/single/{listingId}

Get a single listing with auction and bids

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## GET /Shops/listings/{collectionId}

Get listings by collection

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `collectionId` | path | int32 | Yes | — |

### Response

`200` Success

---

## DELETE /Shops/listings/{listingId}

Delete a listing and all associated data

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/listings/{listingId}/cancel

Cancel a listing

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/listings/{listingId}/retry-sale

Retry a failed sale

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## DELETE /Shops/local

Delete local shop and all associated data

### Response

`200` Success

---

## GET /Shops/network/info

Get network shop info by URL

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `url` | query | string | No | — |

### Response

`200` Success

---

## GET /Shops/network/list

List all shops on the network

### Response

`200` Success

---

## GET /Shops/network/search

Search for a shop by URL on the network

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `url` | query | string | No | — |

### Response

`200` Success

---

## DELETE /Shops/ping

Clear all ping requests

### Response

`200` Success

---

## GET /Shops/ping/{pingId}

Check ping result

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `pingId` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Shops/ping/{pingId}

Ping a connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `pingId` | path | string | Yes | — |

### Response

`200` Success

---

## POST /Shops/publish

Publish shop to network

### Response

`200` Success

---

## POST /Shops/purchases/complete

Complete an NFT purchase

### Request Body

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `keySign` | string | Yes | minLength: 1 |
| `scUID` | string | Yes | minLength: 1 |

### Response

`200` Success

---

## POST /Shops/remote/assets/{scUID}

Request NFT asset download from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/auctions/specific/{listingId}

Request a specific auction from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/auctions/{page}

Request auctions from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `page` | path | int32 | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/bids/{listingId}

Request bids for a listing from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `listingId` | path | int32 | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/collections

Request collections from connected shop

### Response

`200` Success

---

## GET /Shops/remote/info

Request shop info from connected shop

### Response

`200` Success

---

## GET /Shops/remote/listings/collection/{collectionId}/{page}

Request listings by collection from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `collectionId` | path | int32 | Yes | — |
| `page` | path | int32 | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/listings/specific/{scUID}

Request a specific listing from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `scUID` | path | string | Yes | — |

### Response

`200` Success

---

## GET /Shops/remote/listings/{page}

Request listings from connected shop

### Parameters

| Name | In | Type | Required | Description |
|------|----|------|----------|-------------|
| `page` | path | int32 | Yes | — |

### Response

`200` Success

---

## POST /Shops/status/toggle

Toggle shop online/offline status

### Response

`200` Success

---

## POST /Shops/update

Update shop on network

### Response

`200` Success
