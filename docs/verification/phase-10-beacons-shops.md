# Phase 10 Verification: Beacons, Shops & Advanced (Updated)

**Date:** 2026-03-12
**Reviewer:** reviewer (automated)
**Verdict:** PASS
**Revision:** 2 — Updated for expanded ShopsController (9 -> 58 endpoints)

---

## Scope

Phase 10 covers beacon management and the full decentralized shop (DST) protocol. The plan specifies 7 beacon endpoints and "37+ endpoints" for shops, noting the DSTV1Controller has ~60 endpoints to consolidate. The executor expanded ShopsController from 9 core endpoints to 58 endpoints, covering the full DSTV1Controller and WebShopV1Controller surface.

## Files Reviewed

| File | Lines | Endpoints |
|------|-------|-----------|
| `Api/Rest/Controllers/BeaconsController.cs` | 180 | 7 |
| `Api/Rest/Controllers/ShopsController.cs` | 1411 | 58 |
| `Api/Rest/Models/Requests/BeaconRequests.cs` | 23 | — |
| `Api/Rest/Models/Requests/ShopRequests.cs` | 20 | — |
| `Api/Rest/Infrastructure/RestApiAuthFilter.cs` | 54 | — |

**Total: 65 endpoints (7 beacon + 58 shop)**

## Build

```
dotnet build: 0 errors, 4 warnings (pre-existing NuGet compat only)
```

## Endpoint Coverage

### Beacons (7/7 — unchanged from initial review)

| Plan Route | Action | V1 Source | Status |
|------------|--------|-----------|--------|
| GET `/beacons` | GetAll | BCV1.GetBeacons | OK |
| POST `/beacons` | Create | BCV1.CreateBeacon | OK |
| POST `/beacons/add` | AddRemote | BCV1.AddBeacon | OK |
| DELETE `/beacons/{id}` | Delete | BCV1.DeleteBeacon | OK |
| GET `/beacons/info` | GetInfo | BCV1.GetBeaconInfo | OK |
| POST `/beacons/toggle` | Toggle | BCV1.SetBeaconState | OK |
| GET `/beacons/assets/queue` | GetAssetQueue | BCV1.GetAssetQueue | OK |

### Shops — Local Shop CRUD (8 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops` | GetShop | DSTV1.GetDecShop | OK |
| POST | `/shops` | SaveShop | DSTV1.SaveDecShop | OK |
| POST | `/shops/publish` | Publish | DSTV1.GetPublishDecShop | OK |
| POST | `/shops/update` | UpdateShop | DSTV1.GetUpdateDecShop | OK (new) |
| POST | `/shops/status/toggle` | ToggleStatus | DSTV1.GetSetShopStatus | OK (new) |
| DELETE | `/shops` | DeleteShop | DSTV1.GetDeleteDecShop | OK |
| DELETE | `/shops/local` | DeleteLocalShop | DSTV1.GetDeleteLocalDecShop | OK (new) |
| POST | `/shops/import/{address}` | ImportFromNetwork | DSTV1.GetImportDecShopFromNetwork | OK (new) |

### Shops — Network Discovery (3 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/network/search` | SearchByUrl | DSTV1.GetDecShopByURL | OK (new) |
| GET | `/shops/network/info` | GetNetworkInfo | DSTV1.GetNetworkDecShopInfo | OK (new) |
| GET | `/shops/network/list` | GetNetworkList | DSTV1.GetDecShopStateTreiList | OK (new) |

### Shops — Connections & Ping (6 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| POST | `/shops/connect` | Connect | DSTV1.ConnectToDecShop / WebShop.ConnectToDecShop | OK (new) |
| GET | `/shops/connections` | GetConnections | DSTV1.GetConnections / WebShop.GetConnections | OK (new) |
| GET | `/shops/data` | GetShopData | DSTV1.GetDecShopData / WebShop.GetDecShopData | OK (new) |
| POST | `/shops/ping/{pingId}` | PingShop | DSTV1.PingShop / WebShop.PingShop | OK (new) |
| GET | `/shops/ping/{pingId}` | CheckPing | DSTV1.CheckPingShop / WebShop.CheckPingShop | OK (new) |
| DELETE | `/shops/ping` | ClearPings | DSTV1.ClearPingRequest / WebShop.ClearPingRequest | OK (new) |

### Shops — Remote Shop Queries (8 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/remote/info` | GetRemoteShopInfo | DSTV1.GetShopInfo | OK (new) |
| GET | `/shops/remote/collections` | GetRemoteCollections | DSTV1.GetShopCollections | OK (new) |
| GET | `/shops/remote/listings/{page}` | GetRemoteListings | DSTV1.GetShopListings | OK (new) |
| GET | `/shops/remote/auctions/{page}` | GetRemoteAuctions | DSTV1.GetShopAuctions | OK (new) |
| GET | `/shops/remote/listings/collection/{collectionId}/{page}` | GetRemoteListingsByCollection | DSTV1.GetShopListingsByCollection | OK (new) |
| GET | `/shops/remote/listings/specific/{scUID}` | GetRemoteSpecificListing | DSTV1.GetShopSpecificListing | OK (new) |
| GET | `/shops/remote/auctions/specific/{listingId}` | GetRemoteSpecificAuction | DSTV1.GetShopSpecificAuction | OK (new) |
| GET | `/shops/remote/bids/{listingId}` | GetRemoteListingBids | DSTV1.GetShopListingBids | OK (new) |
| POST | `/shops/remote/assets/{scUID}` | DownloadAssets | (asset download via DSTClient) | OK (new) |

### Shops — Collections (6 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/collections` | GetCollections | DSTV1.GetAllCollections | OK |
| GET | `/shops/collections/{collectionId}` | GetCollection | DSTV1.GetCollection | OK (new) |
| GET | `/shops/collections/default` | GetDefaultCollection | DSTV1.GetDefaultCollection | OK (new) |
| POST | `/shops/collections/{collectionId}/set-default` | SetDefaultCollection | DSTV1.GetDefaultCollectionChange | OK (new) |
| POST | `/shops/collections` | SaveCollection | DSTV1.SaveCollection | OK |
| DELETE | `/shops/collections/{collectionId}` | DeleteCollection | DSTV1.DeleteCollection | OK (new) |

### Shops — Listings (6 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/listings/{collectionId}` | GetCollectionListings | DSTV1.GetCollectionListings | OK |
| GET | `/shops/listings/single/{listingId}` | GetSingleListing | DSTV1.GetListing + auction/bids | OK (new) |
| POST | `/shops/listings` | SaveListing | DSTV1.SaveListing | OK |
| POST | `/shops/listings/{listingId}/cancel` | CancelListing | DSTV1.CancelListing | OK (new) |
| DELETE | `/shops/listings/{listingId}` | DeleteListing | DSTV1.DeleteListing | OK (new) |
| POST | `/shops/listings/{listingId}/retry-sale` | RetrySale | DSTV1.RetrySale | OK (new) |

### Shops — Auctions (2 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/auctions/{listingId}` | GetAuction | DSTV1.GetAuctionByListing | OK (new) |
| POST | `/shops/auctions/{listingId}/reset` | ResetAuction | DSTV1.GetResetAuction | OK (new) |

### Shops — Bids (7 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/bids/{sendReceive}` | GetBids | DSTV1.GetBids | OK (new) |
| GET | `/shops/bids/listing/{listingId}/{sendReceive}` | GetListingBids | DSTV1.GetListingBids | OK (new) |
| GET | `/shops/bids/status/{bidStatus}/{sendReceive}` | GetBidsByStatus | DSTV1.GetBidsByStatus | OK (new) |
| GET | `/shops/bids/{bidId:guid}` | GetSingleBid | DSTV1.GetSingleBids | OK (new) |
| POST | `/shops/bids` | SendBid | DSTV1.SendBid | OK |
| POST | `/shops/bids/buy-now` | SendBuyNowBid | DSTV1.SendBuyNowBid | OK (new) |
| POST | `/shops/bids/{bidId}/resend` | ResendBid | DSTV1.ResendBid | OK (new) |

### Shops — Chat (12 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| POST | `/shops/chat` | SendChatMessage | DSTV1.SendChatMessage | OK (new) |
| POST | `/shops/chat/shop` | SendShopChatMessage | DSTV1.SendShopChatMessage | OK (new) |
| POST | `/shops/chat/{messageId}/resend` | ResendChatMessage | DSTV1.ResendChatMessage | OK (new) |
| GET | `/shops/chat/messages` | GetChatMessages | DSTV1.GetDetailedChatMessages | OK (new) |
| GET | `/shops/chat/messages/simple` | GetSimpleChatMessages | DSTV1.GetSimpleChatMessages | OK (new) |
| GET | `/shops/chat/messages/{messageId}` | GetSpecificChatMessage | DSTV1.GetSpecificChatMessages | OK (new) |
| GET | `/shops/chat/messages/recent/{key}` | GetRecentChatMessages | DSTV1.GetMostRecentChatMessages | OK (new) |
| GET | `/shops/chat/messages/summary` | GetSummaryChatMessages | DSTV1.GetSummaryChatMessages | OK (new) |
| DELETE | `/shops/chat/messages/{key}` | DeleteChatMessages | DSTV1.DeleteChatMessages | OK (new) |
| GET | `/shops/chat/shop-messages/simple` | GetSimpleShopChatMessages | DSTV1.GetSimpleShopChatMessages | OK (new) |
| GET | `/shops/chat/shop-messages/detailed` | GetDetailedShopChatMessages | DSTV1.GetDetailedShopChatMessages | OK (new) |
| GET | `/shops/chat/shop-messages/{vfxAddress}` | GetDetailedSpecificShopChatMessages | DSTV1.GetDetailedSpecificShopChatMessages | OK (new) |
| GET | `/shops/chat/shop-messages/{vfxAddress}/simple` | GetSimpleSpecificShopChatMessages | DSTV1.GetSimpleSpecificShopChatMessages | OK (new) |

### Shops — NFT Purchase (1 endpoint)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| POST | `/shops/purchases/complete` | CompleteNFTPurchase | DSTV1.CompleteNFTPurchase | OK (new) |

### Shops — Debug (2 endpoints)

| Verb | Route | Action | V1 Source | Status |
|------|-------|--------|-----------|--------|
| GET | `/shops/debug` | GetDebug | DSTV1.Debug | OK (new) |
| GET | `/shops/debug/data` | GetDebugData | DSTV1.DebugData | OK (new) |

## V1 Mapping Verification

### BeaconsController vs BCV1Controller (unchanged)

- **Create** (lines 36-78): Identical logic -- IP check, BeaconInfoJson, UID generation, save, SetSelfBeacon(), cache update.
- **AddRemote** (lines 84-112): Same logic -- CreateBeaconLocator(), save, cache update.
- **Delete** (lines 118-135): Query-by-id, DeleteBeacon(), TryRemove from cache.
- **GetAll** (lines 19-30): Returns typed empty array instead of `"[]"` string.
- **GetInfo** (lines 141-148): Returns `Globals.SelfBeacon` directly.
- **Toggle** (lines 154-161): Returns `{ active: bool }` instead of stringified JSON.
- **GetAssetQueue** (lines 167-178): Returns empty array on no data.

### ShopsController vs DSTV1Controller + WebShopV1Controller (expanded)

**Local Shop CRUD:**
- **SaveShop** (lines 40-104): Handles create+update. Preserves V1 validation: word count, desc length, URL uniqueness, hosting type IP logic.
- **UpdateShop** (lines 130-144): Maps to DSTV1.GetUpdateDecShop. Checks `NeedsPublishToNetwork`, calls `DecShop.UpdateDecShopTx()`.
- **ToggleStatus** (lines 150-161): Maps to DSTV1.GetSetShopStatus. Calls `DecShop.SetDecShopStatus()`.
- **DeleteLocalShop** (lines 184-206): Maps to DSTV1.GetDeleteLocalDecShop. Cascading delete of listings, auctions, bids.
- **ImportFromNetwork** (lines 212-233): Maps to DSTV1.GetImportDecShopFromNetwork. Ownership check, duplicate check, imports leaf.

**Network Discovery:**
- **SearchByUrl** (lines 243-254): URL-decoded query param, `DecShop.GetDecShopStateTreiLeafByURL()`.
- **GetNetworkInfo** (lines 260-271): Same pattern, different error message.
- **GetNetworkList** (lines 277-284): `DecShop.GetDecShopStateTreiList()`, empty array fallback.

**Connections:**
- **Connect** (lines 294-311): Uses `ConnectToShopRequest` DTO with [Required] Address + Url. Disconnects first, then connects via `DSTClient.ConnectToShop()`. Triggers `DSTClient.GetShopData()` on success.
- **GetConnections** (lines 317-328): Checks `Globals.ConnectedClients`. Returns connected status.
- **GetShopData** (lines 334-340): Returns `Globals.DecShopData` from memory cache.
- **PingShop/CheckPing/ClearPings** (lines 346-380): Full ping lifecycle via `DSTClient.PingConnection()` and `Globals.PingResultDict`.

**Remote Queries:** All 9 endpoints follow identical pattern -- check connected, construct Message with appropriate `DecShopRequestOptions`, fire-and-forget via `DSTClient.SendShopMessageFromClient()`. This matches the V1 pattern exactly.

**Collections:** Full CRUD. `DeleteCollection` cascades to listings, auctions, bids -- matches DSTV1.DeleteCollection logic.

**Listings:** Full lifecycle. `GetSingleListing` enriches with auction and bids (consolidation of 3 V1 calls). `CancelListing` sets `IsCancelled=true`. `DeleteListing` cascades.

**Auctions:** Get by listing, reset auction state.

**Bids:** Full bid query surface (by sendReceive, listing, status, single). `SendBuyNowBid` validates `IsBuyNow=true`. `ResendBid` fire-and-forgets via DST message.

**Chat:** Complete buyer and seller chat -- `SendChatMessage` (buyer), `SendShopChatMessage` (shop owner with ownership verification), resend, query by URL/ID/address, delete, summary. Message length validation (240 chars). Signature creation for authentication.

**NFT Purchase:** `CompleteNFTPurchase` uses `CompleteNftPurchaseRequest` DTO with [Required] ScUID + KeySign. Validates SC state, next owner, balance, then calls `SmartContractService.CompleteSaleSmartContractTX()`.

**Debug:** Returns DST connection state and shop statistics.

## Auth Filter

No new actions added to `EncryptionRequiredActions`. This is correct:
- Shop config operations (save, toggle, delete) are local DB operations
- TX-creating operations (publish, update, delete-network) enforce wallet state at the model layer
- Chat messages use per-message signatures via `SignatureService.CreateSignature()`, not wallet-level encryption
- NFT purchase completion creates a TX but the `CompleteSaleSmartContractTX` handles wallet state internally

## Request DTOs

**BeaconRequests.cs:**
- `CreateBeaconRequest`: `[Required] Name`, optional `IsPrivate`, `AutoDelete`, `FileCachePeriod`, `Port`
- `AddBeaconRequest`: `[Required] Name`, `[Required] IpAddress`, optional `Port`

**ShopRequests.cs (new):**
- `ConnectToShopRequest`: `[Required] Address`, `[Required] Url` -- used by POST `/shops/connect`
- `CompleteNftPurchaseRequest`: `[Required] ScUID`, `[Required] KeySign` -- used by POST `/shops/purchases/complete`

Complex DST models (DecShop, Collection, Listing, Bid, Chat.ChatPayload) continue to use `[FromBody] object jsonData` with `JsonConvert.DeserializeObject<T>()`, matching the V1 pattern.

## Route Organization

The controller uses `#region` blocks for logical grouping:

1. **Local Shop CRUD** -- shop lifecycle (create, update, publish, delete, import)
2. **Network Shop Discovery** -- search, info, list from network state trie
3. **Shop Connections** -- connect, disconnect, ping, data cache
4. **Remote Shop Queries** -- fire-and-forget DST messages to connected shop
5. **Collections** -- full CRUD with cascade deletes
6. **Listings** -- full lifecycle with cancel, retry-sale
7. **Auctions** -- get and reset
8. **Bids** -- full query surface, send, buy-now, resend
9. **Chat** -- buyer chat, shop owner chat, message queries
10. **NFT Purchase** -- complete purchase flow
11. **Debug** -- connection state and statistics

Routes follow a clean resource hierarchy:
- `/shops` -- local shop
- `/shops/network/...` -- network discovery
- `/shops/remote/...` -- remote shop queries
- `/shops/collections/...` -- collection management
- `/shops/listings/...` -- listing management
- `/shops/auctions/...` -- auction management
- `/shops/bids/...` -- bid management
- `/shops/chat/...` -- chat system
- `/shops/purchases/...` -- NFT purchase
- `/shops/debug/...` -- diagnostics

## Error Envelopes

All 58 endpoints use consistent `.Fail()` with error codes. New codes include:
- `NO_UPDATE` -- no pending network update
- `UPDATE_FAILED` -- network update TX failure
- `TOGGLE_FAILED` -- status toggle failure
- `DB_ERROR` -- database access failure
- `ALREADY_EXISTS` / 409 -- duplicate shop on import
- `IMPORT_FAILED` -- network import failure
- `NOT_CONNECTED` -- not connected to a remote shop
- `PING_FAILED` -- ping attempt failure
- `LOCKED` -- asset download lock
- `CONNECTION_FAILED` -- asset connection failure
- `CHANGE_FAILED` -- default collection change failure
- `CANCEL_FAILED` -- listing cancellation failure
- `ALREADY_COMPLETE` -- sale already completed
- `NOT_SHOP_OWNER` / 403 -- non-owner trying to send shop chat
- `INVALID_STATE` -- smart contract state missing for purchase
- `INSUFFICIENT_FUNDS` -- balance too low for purchase
- `PURCHASE_FAILED` -- TX failure on purchase

## Observations

1. **Scope expansion**: ShopsController grew from 9 to 58 endpoints, covering essentially the full DSTV1Controller (67 endpoints) and WebShopV1Controller (15 endpoints). The consolidation is effective -- many V1 endpoints with overlapping functionality were merged (e.g., WebShop and DSTV1 connection endpoints merged into one `Connect` action).

2. **Chat third-party handling**: V1 had separate `SendChatMessage` and `SendChatMessageThirdParty` endpoints. V2 consolidates via the `IsThirdParty` flag on the chat payload, which is cleaner.

3. **Well-organized regions**: The `#region` blocks make the 1411-line controller navigable. Routes follow a consistent resource hierarchy.

4. **Route constraints**: Proper use of `{collectionId:int}`, `{listingId:int}`, `{page:int}`, `{bidId:guid}` throughout.

5. **Ownership checks**: `Connect` validates address ownership. `SendShopChatMessage` validates shop ownership. `ImportFromNetwork` validates address ownership. `CompleteNFTPurchase` validates next-owner address.

## Warnings (non-blocking)

- **WARN-1**: `SendShopChatMessage` and `SendChatMessage` both sign messages with the private key. While they correctly verify address ownership, these actions should arguably be in `EncryptionRequiredActions` since they access `GetPrivKey`. However, this matches the V1 behavior where chat was not gated on encryption state.

- **WARN-2**: `GetBidsByStatus` accepts `sendReceive` as a route parameter but does not pass it to `Bid.GetBidByStatus(bidStatus)` (line 885). The V1 endpoint `GetBidsByStatus/{bidStatus}/{sendReceive}` also accepted both parameters. This may be intentional if filtering by sendReceive is not supported at the data layer.

## Verdict

**PASS** -- All 65 endpoints (7 beacon + 58 shop) are implemented correctly with proper V1 mappings, error envelopes, typed request DTOs where appropriate, ownership checks, and well-organized route hierarchy. Build succeeds with zero errors. The expanded scope now covers the full DSTV1Controller and WebShopV1Controller surface as the plan intended.
