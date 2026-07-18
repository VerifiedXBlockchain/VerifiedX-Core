# VFX REST API

Clean, resource-oriented API for VFX wallet integration.

**Version:** 1.0

## Authentication

Include your API token in the `apitoken` request header. Most endpoints require a valid token when one is configured.

The `GET /api/rest/wallets/status` endpoint is accessible without authentication.

## Response Format

All endpoints return a standard JSON envelope:

```json
{
  "success": true,
  "data": { ... },
  "error": null,
  "meta": null
}
```

Error responses:

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "ERROR_CODE",
    "message": "Human-readable description"
  }
}
```

Paginated endpoints include `meta` with `page`, `pageSize`, `totalCount`, `totalPages`.

## Controllers

| Controller | Endpoints | Description |
|------------|-----------|-------------|
| [Accounts](accounts.md) | 10 | Address management, balances, NFTs |
| [Adnr](adnr.md) | 5 | Domain name registration and resolution |
| [Beacons](beacons.md) | 7 | Beacon node management |
| [Bitcoin](bitcoin.md) | 33 |  |
| [Blocks](blocks.md) | 4 | Block data and chain history |
| [Network](network.md) | 10 | Network status, peers, masternodes |
| [Privacy](privacy.md) | 22 |  |
| [ReserveAccounts](reserve-accounts.md) | 10 | Reserve (xRBX) account operations |
| [Shops](shops.md) | 63 | Decentralized shop protocol (DST) |
| [Signatures](signatures.md) | 2 | Create and verify signatures |
| [SmartContracts](smart-contracts.md) | 16 | NFT lifecycle, sales, ownership |
| [Tokens](tokens.md) | 10 | Fungible token operations and governance |
| [Transactions](transactions.md) | 10 | Send, query, and manage transactions |
| [Validators](validators.md) | 9 | Validator registration and management |
| [Vbtc](vbtc.md) | 56 |  |
| [Voting](voting.md) | 7 | Topic creation and vote casting |
| [Wallets](wallets.md) | 8 | Wallet lifecycle, encryption, HD wallets |

---

*Generated from [swagger.json](swagger.json) using `tools/generate-api-docs.sh`*
