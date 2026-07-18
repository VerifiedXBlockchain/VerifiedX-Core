# VFX Core-CLI: REST API Layer (`/api/rest/`)

## Context

The existing VFX wallet CLI has ~330 API endpoints across 16 controllers. While the core logic is solid and audited, the API layer has poor REST conventions: GET used for mutations, passwords in URLs, inconsistent response formats, no pagination, mixed concerns in controllers, and inconsistent naming. This makes integration painful for exchanges, DeFi protocols, and internal tools.

**Goal**: Create a new, clean API layer at `/api/rest/` alongside the existing API. No changes to existing controllers or core logic — only new controllers that call into the existing services and data layer.

**Key decisions**:
- Route prefix: `/api/rest/[controller]`
- Auth: New `RestApiAuthFilter` (API token via header only — no password-in-URL support)
- JSON casing: camelCase via Newtonsoft `CamelCasePropertyNamesContractResolver`
- Audience: Both external integrators and internal tools

---

## Architecture

### Response Envelope

Every endpoint returns this structure:

```json
{
  "success": true,
  "data": { ... },
  "error": null,
  "meta": {
    "page": 1,
    "pageSize": 25,
    "totalCount": 142,
    "totalPages": 6
  }
}
```

Error responses:
```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "WALLET_LOCKED",
    "message": "Wallet is encrypted and locked. Unlock it first."
  }
}
```

- `meta` only present on paginated list endpoints
- HTTP status codes used properly: 200, 201, 400, 401, 403, 404, 500

### Folder Structure

```
ReserveBlockCore/
  Controllers/           # existing (UNTOUCHED)
  Api/
    Rest/
      Controllers/
        WalletsController.cs
        AccountsController.cs
        TransactionsController.cs
        BlocksController.cs
        SmartContractsController.cs
        TokensController.cs
        ValidatorsController.cs
        NetworkController.cs
        BeaconsController.cs
        VotingController.cs
        ReserveAccountsController.cs
        ShopsController.cs
        AdnrController.cs
        SignaturesController.cs
      Models/
        ApiEnvelope.cs           # ApiResponse<T>, ApiError, PaginationMeta
        PaginationParams.cs      # pagination query model
        Requests/                # request DTOs (one file per controller)
        Responses/               # response DTOs (one file per controller)
      Infrastructure/
        RestBaseController.cs    # base class with response helpers
        RestApiAuthFilter.cs     # API token auth (replaces ActionFilterController for REST)
        RestExceptionFilter.cs   # global error handling → envelope
        RestJsonSettings.cs      # shared Newtonsoft camelCase settings
```

### Auth Filter: `RestApiAuthFilter`

A new action filter built specifically for the REST API. Does NOT reuse `ActionFilterController` — the existing one relies on route-data method name matching and password-in-URL patterns that we're deliberately leaving behind.

```csharp
public class RestApiAuthFilter : ActionFilterAttribute
{
    // Endpoints that work without API token (health checks only)
    private static readonly HashSet<string> TokenBypassActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetStatus"  // GET /api/rest/wallets/status
    };

    // Write operations that require wallet to be unlocked when encrypted
    private static readonly HashSet<string> EncryptionRequiredActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Send", "CreateAdnr", "TransferAdnr", "DeleteAdnr",
        "ImportKey", "CreateSignature", "CastVote", "CreateTopic",
        "Mint", "Transfer", "Burn", "Evolve", "Devolve"
    };

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var actionName = context.RouteData.Values["action"]?.ToString() ?? "";

        // 1. API token check (when token is configured)
        if (Globals.APIToken != null)
        {
            bool bypass = TokenBypassActions.Contains(actionName);
            var headerToken = context.HttpContext.Request.Headers["apitoken"].ToString();
            if (!bypass && headerToken != Globals.APIToken.ToUnsecureString())
            {
                context.Result = new ObjectResult(
                    ApiResponse<object>.Error("UNAUTHORIZED", "Invalid or missing API token."))
                { StatusCode = 403 };
                return;
            }
        }

        // 2. Wallet encryption check
        if (Globals.IsWalletEncrypted && Globals.EncryptPassword.Length == 0)
        {
            if (EncryptionRequiredActions.Contains(actionName))
            {
                context.Result = new ObjectResult(
                    ApiResponse<object>.Error("WALLET_LOCKED", "Wallet is encrypted and locked. Unlock it first."))
                { StatusCode = 401 };
                return;
            }
        }
    }
}
```

**Key differences from `ActionFilterController`:**
- No `{somePassword?}` route support — token-only auth
- Returns structured JSON error envelope instead of bare status codes
- Uses `HashSet` with explicit action names for the REST controllers
- Encryption check returns 401 with clear message

### Error Handling: `RestExceptionFilter`

An exception filter on the base controller that catches unhandled exceptions and maps them to the error envelope. Prevents raw 500s from leaking stack traces.

```csharp
public class RestExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var error = context.Exception switch
        {
            ArgumentException ex => (400, "BAD_REQUEST", ex.Message),
            KeyNotFoundException ex => (404, "NOT_FOUND", ex.Message),
            UnauthorizedAccessException ex => (401, "UNAUTHORIZED", ex.Message),
            InvalidOperationException ex => (409, "CONFLICT", ex.Message),
            _ => (500, "INTERNAL_ERROR", "An unexpected error occurred.")
        };

        context.Result = new ObjectResult(
            ApiResponse<object>.Error(error.Item2, error.Item3))
        { StatusCode = error.Item1 };

        context.ExceptionHandled = true;
    }
}
```

This means individual controller methods don't need try/catch boilerplate — they throw, the filter catches.

### Base Controller

```csharp
[RestApiAuthFilter]
[ServiceFilter(typeof(RestExceptionFilter))]
[ApiController]
[ApiExplorerSettings(GroupName = "rest")]
[Route("api/rest/[controller]")]
[Produces("application/json")]
public abstract class RestBaseController : ControllerBase
{
    private static readonly JsonSerializerSettings CamelCase = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    protected IActionResult Ok<T>(T data)
    {
        var envelope = ApiResponse<T>.Success(data);
        return Content(JsonConvert.SerializeObject(envelope, CamelCase), "application/json");
    }

    protected IActionResult OkPaged<T>(IEnumerable<T> items, int page, int pageSize, int totalCount)
    {
        var envelope = ApiResponse<IEnumerable<T>>.Paged(items, page, pageSize, totalCount);
        return Content(JsonConvert.SerializeObject(envelope, CamelCase), "application/json");
    }

    protected IActionResult Created<T>(T data)
    {
        var envelope = ApiResponse<T>.Success(data);
        return new ContentResult
        {
            Content = JsonConvert.SerializeObject(envelope, CamelCase),
            ContentType = "application/json",
            StatusCode = 201
        };
    }

    protected IActionResult Fail(string code, string message, int status = 400)
    {
        var envelope = ApiResponse<object>.Error(code, message);
        return new ContentResult
        {
            Content = JsonConvert.SerializeObject(envelope, CamelCase),
            ContentType = "application/json",
            StatusCode = status
        };
    }
}
```

### Request Validation

All request DTOs use `[Required]` and other data annotation attributes. ASP.NET's `[ApiController]` attribute automatically returns 400 for invalid models, but the default format isn't our envelope. We add a model validation filter:

```csharp
// In Startup.cs — configures ApiController to use our envelope for validation errors
services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => $"{e.Key}: {e.Value!.Errors.First().ErrorMessage}")
            .ToList();

        var response = ApiResponse<object>.Error(
            "VALIDATION_ERROR",
            string.Join("; ", errors));

        return new BadRequestObjectResult(response);
    };
});
```

Example request DTO:
```csharp
public class SendTransactionRequest
{
    [Required(ErrorMessage = "Sender address is required")]
    public string FromAddress { get; set; }

    [Required(ErrorMessage = "Recipient address is required")]
    public string ToAddress { get; set; }

    [Required]
    [Range(0.00000001, double.MaxValue, ErrorMessage = "Amount must be greater than 0")]
    public decimal Amount { get; set; }
}
```

### Swagger — Separate Doc

REST API gets its own Swagger document, completely separate from the existing v1 docs.

**Startup.cs changes** (additive only):
```csharp
// In ConfigureServices — add alongside existing SwaggerDoc("v1", ...)
c.SwaggerDoc("rest", new OpenApiInfo
{
    Title = "VFX REST API",
    Version = "1.0",
    Description = "Clean, resource-oriented API for VFX wallet integration."
});

// In Configure — add alongside existing SwaggerEndpoint
c.SwaggerEndpoint("/swagger/rest/swagger.json", "VFX REST API");
```

The `[ApiExplorerSettings(GroupName = "rest")]` on `RestBaseController` ensures REST endpoints only appear in the `rest` doc, not the `v1` doc.

### Middleware Bypass

**Startup.cs** — add REST API unlock path to the middleware bypass (line ~155):
```csharp
if (target.Contains("/api/v1/unlockwallet/") ||
    target.Contains("/api/rest/wallets/unlock"))
{
    return func.Invoke();
}
```

Also add REST status endpoint as always-accessible:
```csharp
if (target.Contains("/api/rest/wallets/status"))
{
    return func.Invoke();
}
```

---

## Route Design

All routes use proper HTTP verbs and resource-oriented naming.

### Wallets (`/api/rest/wallets`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/wallets/status` | V1.CheckStatus | Health check (no auth required) |
| GET | `/wallets/info` | V1.GetWalletInfo | Wallet status (sync, peers, version) |
| GET | `/wallets/version` | V1.GetCLIVersion | CLI version |
| POST | `/wallets/encrypt` | V1.GetEncryptWallet | Encrypt wallet |
| POST | `/wallets/unlock` | V1.UnlockWallet | Unlock encrypted wallet |
| POST | `/wallets/lock` | V1.LockWallet | Lock wallet |
| GET | `/wallets/encryption-status` | V1.GetCheckEncryptionStatus | Check encryption state |
| POST | `/wallets/exit` | V1.SendExit | Shut down wallet |
| POST | `/wallets/restart` | V1.SetRestartAndExit | Restart wallet |
| POST | `/wallets/hd` | V1.GetHDWallet | Create HD wallet |
| POST | `/wallets/hd/restore` | V1.GetRestoreHDWallet | Restore HD wallet from mnemonic |

### Accounts (`/api/rest/accounts`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/accounts` | V1.GetAllAddresses + V2.GetBalances | List all accounts with balances |
| POST | `/accounts` | V1.GetNewAddress | Create new address |
| GET | `/accounts/{address}` | V1.GetAddressInfo + V2.GetStateBalance | Get account details |
| GET | `/accounts/{address}/balance` | V2.GetStateBalance | Get balance only |
| POST | `/accounts/import` | V1.ImportPrivateKey | Import private key (in body) |
| GET | `/accounts/{address}/nonce` | TXV1.GetAddressNonce | Get address nonce |
| POST | `/accounts/{address}/rescan` | V1.RescanForTx | Rescan for transactions |
| POST | `/accounts/sync-balances` | V1.SyncBalances | Sync all balances |
| GET | `/accounts/{address}/nfts` | Wallet.GetNFTs | List NFTs for account |
| GET | `/accounts/{address}/validate` | V1.ValidateAddress | Check address validity |

### Transactions (`/api/rest/transactions`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| POST | `/transactions` | TXV1.SendRawTransaction | Send raw transaction |
| POST | `/transactions/send` | V1.SendTransaction (as POST) | Simple send (from, to, amount in body) |
| GET | `/transactions?status=pending&page=1` | TXV1.GetPendingLocalTX etc. | List local TXs (filterable, paginated) |
| GET | `/transactions/{hash}` | TXV1.GetLocalTxByHash | Get TX by hash |
| GET | `/transactions/search/{hash}` | TXV1.GetNetworkTXByHash | Search full chain for TX |
| POST | `/transactions/verify` | TXV1.VerifyRawTransaction | Verify raw TX |
| POST | `/transactions/fee` | TXV1.GetRawTxFee | Estimate TX fee |
| POST | `/transactions/hash` | TXV1.GetTxHash | Calculate TX hash |
| POST | `/transactions/{hash}/replace` | TXV1.ReplaceTransactionByFee | Replace-by-fee |
| GET | `/transactions/mempool` | V1.GetMempool | Get mempool |

### Blocks (`/api/rest/blocks`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/blocks?page=1` | Explorer.GetBlocks | List recent blocks (paginated) |
| GET | `/blocks/latest` | V1.GetLastBlock | Get latest block |
| GET | `/blocks/{height}` | V1.GetBlockByHeight | Get block by height |
| GET | `/blocks/hash/{hash}` | V1.GetBlockByHash | Get block by hash |

### Network (`/api/rest/network`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/network` | Integrations.Network | Network overview |
| GET | `/network/metrics` | V1.NetworkMetrics | Block timing metrics |
| GET | `/network/height` | Integrations.Height | Current block height |
| GET | `/network/peers` | V1.GetPeerInfo | Peer info |
| POST | `/network/peers` | V1.AddPeer | Add peer |
| GET | `/network/peers/banned` | V1.ListBannedPeers | List banned peers |
| POST | `/network/peers/{ip}/ban` | V1.BanPeer | Ban peer |
| DELETE | `/network/peers/{ip}/ban` | V1.UnbanPeer | Unban peer |
| GET | `/network/masternodes` | V1.GetMasternodes | List masternodes |

### Signatures (`/api/rest/signatures`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| POST | `/signatures` | V1.CreateSignature | Create signature (address + message in body) |
| POST | `/signatures/verify` | V1.ValidateSignature | Verify signature (all params in body) |

### ADNR (`/api/rest/adnr`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| POST | `/adnr` | TXV1.CreateAdnr | Create domain name |
| POST | `/adnr/transfer` | TXV1.TransferAdnr | Transfer ADNR |
| DELETE | `/adnr/{address}` | TXV1.DeleteAdnr | Delete ADNR |
| GET | `/adnr/resolve/{name}` | V2.ResolveAdnr | Resolve name to address |
| GET | `/adnr/reverse/{address}` | V2.ResolveAddressAdnr | Reverse lookup |

### Smart Contracts (`/api/rest/smart-contracts`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/smart-contracts?page=1&search=` | SCV1.GetAllSmartContracts | List smart contracts (paginated) |
| GET | `/smart-contracts/minted?page=1` | SCV1.GetMintedSmartContracts | List minted SCs |
| GET | `/smart-contracts/{scUID}` | SCV1.GetSingleSmartContract | Get SC details |
| GET | `/smart-contracts/{scUID}/state` | SCV1.GetSmartContractsState | Get SC state |
| GET | `/smart-contracts/{scUID}/data` | SCV1.GetSmartContractData | Get on-chain SC data |
| POST | `/smart-contracts` | SCV1.CreateSmartContract | Create smart contract |
| POST | `/smart-contracts/{scUID}/mint` | SCV1.MintSmartContract | Mint/publish SC |
| POST | `/smart-contracts/{scUID}/transfer` | SCV1.TransferNFT | Transfer NFT |
| POST | `/smart-contracts/{scUID}/burn` | SCV1.Burn | Burn NFT |
| POST | `/smart-contracts/{scUID}/evolve` | SCV1.Evolve | Evolve NFT |
| POST | `/smart-contracts/{scUID}/devolve` | SCV1.Devolve | Devolve NFT |
| POST | `/smart-contracts/{scUID}/sale` | SCV1.TransferSale | Start sale |
| POST | `/smart-contracts/{scUID}/sale/complete` | SCV1.CompleteTransferSale | Complete sale |
| DELETE | `/smart-contracts/{scUID}/sale` | SCV1.CancelSale | Cancel sale |
| GET | `/smart-contracts/{scUID}/ownership` | SCV1.ProveOwnership | Prove ownership |
| POST | `/smart-contracts/{scUID}/ownership/verify` | SCV1.VerifyOwnership | Verify ownership |

### Tokens (`/api/rest/tokens`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/tokens/{scUID}` | TKV2.GetTokens | Get token info |
| POST | `/tokens/{scUID}/transfer` | TKV2.TransferToken | Transfer tokens |
| POST | `/tokens/{scUID}/burn` | TKV2.BurnToken | Burn tokens |
| POST | `/tokens/{scUID}/mint` | TKV2.TokenMint | Mint tokens |
| POST | `/tokens/{scUID}/pause` | TKV2.PauseTokenContract | Toggle pause |
| POST | `/tokens/{scUID}/ban` | TKV2.BanAddress | Ban address |
| POST | `/tokens/{scUID}/transfer-ownership` | TKV2.ChangeTokenContractOwnership | Transfer ownership |
| GET | `/tokens/{scUID}/votes` | TKV2.GetVoteBySmartContractUID | Get token votes |
| POST | `/tokens/{scUID}/topics` | TKV2.CreateTokenTopic | Create vote topic |
| POST | `/tokens/{scUID}/topics/{topicUID}/vote` | TKV2.CastTokenTopicVote | Cast vote |

### Voting (`/api/rest/voting`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/voting/topics?status=active&page=1` | VOV1.GetActiveTopics/GetAllTopics | List topics |
| GET | `/voting/topics/{topicUID}` | VOV1.GetTopicDetails | Get topic details |
| POST | `/voting/topics` | VOV1.PostNewTopic | Create topic |
| POST | `/voting/topics/{topicUID}/vote` | VOV1.CastTopicVote | Cast vote |
| GET | `/voting/topics/{topicUID}/votes` | VOV1.GetTopicVotes | Get votes for topic |
| GET | `/voting/my/topics` | VOV1.GetMyTopics | My topics |
| GET | `/voting/my/votes` | VOV1.GetMyVotes | My votes |

### Validators (`/api/rest/validators`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/validators` | V1.GetValidatorAddresses | List validator-eligible accounts |
| GET | `/validators/status` | V1.IsValidating | Check if validating |
| POST | `/validators/start` | V1.TurnOnValidator | Start validating |
| POST | `/validators/stop` | V1.TurnOffValidator | Stop validating |
| GET | `/validators/{address}` | V1.GetValidatorInfo | Get validator info |
| POST | `/validators/register` | V1.StartValidating | Register validator |
| PUT | `/validators/name` | V1.ChangeValidatorName | Change validator name |
| POST | `/validators/reset` | V1.ResetValidator | Reset validator |
| GET | `/validators/pool` | V2.ValidatorPool | Network validator pool |

### Reserve Accounts (`/api/rest/reserve-accounts`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/reserve-accounts` | RSV1.GetAllReserveAccounts | List reserve accounts |
| POST | `/reserve-accounts` | RSV1.NewReserveAddress | Create reserve account |
| GET | `/reserve-accounts/{address}` | RSV1.GetReserveAccountInfo | Get account info |
| POST | `/reserve-accounts/{address}/publish` | RSV1.PublishReserveAccount | Publish account |
| POST | `/reserve-accounts/{address}/unlock` | RSV1.UnlockReserveAccount | Unlock account |
| POST | `/reserve-accounts/send` | RSV1.SendReserveTransaction | Send transaction |
| POST | `/reserve-accounts/transfer-nft` | RSV1.ReserveTransferNFT | Transfer NFT |
| POST | `/reserve-accounts/{address}/recover` | RSV1.RecoverReserveAccountTx | Recover account |
| POST | `/reserve-accounts/restore` | RSV1.RestoreReserveAddress | Restore from code |
| POST | `/reserve-accounts/{hash}/callback` | RSV1.CallBackReserveAccountTx | Callback TX |

### Beacons (`/api/rest/beacons`)

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/beacons` | BCV1.GetBeacons | List beacons |
| POST | `/beacons` | BCV1.CreateBeacon | Create beacon |
| POST | `/beacons/add` | BCV1.AddBeacon | Add remote beacon |
| DELETE | `/beacons/{id}` | BCV1.DeleteBeacon | Delete beacon |
| GET | `/beacons/info` | BCV1.GetBeaconInfo | Get beacon info |
| POST | `/beacons/toggle` | BCV1.SetBeaconState | Toggle beacon state |
| GET | `/beacons/assets/queue` | BCV1.GetAssetQueue | Get asset queue |

### Shops (`/api/rest/shops`) — Later Phase

| Verb | Route | Maps To | Description |
|------|-------|---------|-------------|
| GET | `/shops` | DSTV1.GetDecShop | Get local shop |
| POST | `/shops` | DSTV1.SaveDecShop | Create/update shop |
| POST | `/shops/publish` | DSTV1.GetPublishDecShop | Publish shop |
| DELETE | `/shops` | DSTV1.GetDeleteDecShop | Delete shop |
| GET | `/shops/collections` | DSTV1.GetAllCollections | List collections |
| POST | `/shops/collections` | DSTV1.SaveCollection | Create collection |
| GET | `/shops/listings/{collectionId}` | DSTV1.GetCollectionListings | List by collection |
| POST | `/shops/listings` | DSTV1.SaveListing | Create listing |
| POST | `/shops/bids` | DSTV1.SendBid | Send bid |
| ... | ... | ... | (many more chat/bid/auction endpoints) |

---

## Implementation Phases

### Phase 1: Infrastructure (Priority: Critical)
**The scaffolding everything else builds on.**

**Deliverables:**
- `Api/Rest/Infrastructure/RestBaseController.cs` — base controller with `Ok<T>`, `OkPaged<T>`, `Created<T>`, `Fail` helpers
- `Api/Rest/Infrastructure/RestApiAuthFilter.cs` — API token auth + wallet encryption checks
- `Api/Rest/Infrastructure/RestExceptionFilter.cs` — global exception → error envelope mapping
- `Api/Rest/Infrastructure/RestJsonSettings.cs` — shared Newtonsoft `JsonSerializerSettings` with `CamelCasePropertyNamesContractResolver`
- `Api/Rest/Models/ApiEnvelope.cs` — `ApiResponse<T>`, `ApiError`, `PaginationMeta`
- `Api/Rest/Models/PaginationParams.cs` — `?page=1&pageSize=25` query model with `[Range]` validation
- **Startup.cs** (minimal edits):
  - Add `rest` Swagger doc group
  - Add `rest` Swagger UI endpoint
  - Add middleware bypass for `/api/rest/wallets/status` and `/api/rest/wallets/unlock`
  - Configure `ApiBehaviorOptions` for validation error envelope format
  - Register `RestExceptionFilter` as a service

**Verification:**
- `dotnet build` compiles cleanly
- No existing behavior changed

### Phase 2: Wallet & Health (8 endpoints)
**Minimum viable "is this wallet alive and can I talk to it?"**

**Deliverables:**
- `Api/Rest/Controllers/WalletsController.cs`

**Endpoints:**
- `GET /wallets/status` — health check (no auth)
- `GET /wallets/info` — wallet info
- `GET /wallets/version` — CLI version
- `GET /wallets/encryption-status` — encryption state
- `POST /wallets/encrypt` — encrypt wallet
- `POST /wallets/unlock` — unlock wallet
- `POST /wallets/lock` — lock wallet
- `POST /wallets/hd` — create HD wallet

**Key files to reference:** `Controllers/V1Controller.cs` (GetWalletInfo, CheckStatus, GetCLIVersion, UnlockWallet, LockWallet, GetEncryptWallet, GetCheckEncryptionStatus), `Controllers/WalletController.cs` (HD operations)

**Verification:**
- `GET /api/rest/wallets/status` → `{"success": true, "data": "Online"}` (no API token)
- `GET /api/rest/wallets/status` with wrong token → still 200 (bypassed)
- `GET /api/rest/wallets/info` without token → 403 with error envelope
- `GET /api/rest/wallets/info` with valid token → 200 with camelCase data
- Swagger UI at `/swagger` shows REST API as separate document
- Existing v1 endpoints still work unchanged

### Phase 3: Accounts & Balances (10 endpoints)
**"What addresses does this wallet have, and what's in them?"**

**Deliverables:**
- `Api/Rest/Controllers/AccountsController.cs`
- `Api/Rest/Models/Requests/AccountRequests.cs` — `ImportKeyRequest` with `[Required]` private key field

**Endpoints:** Full accounts table from Route Design above.

**Key files to reference:** `Controllers/V1Controller.cs` (GetAllAddresses, GetNewAddress, GetAddressInfo, ImportPrivateKey, ValidateAddress, RescanForTx, SyncBalances), `Controllers/V2Controller.cs` (GetStateBalance, GetBalances), `Controllers/TXV1Controller.cs` (GetAddressNonce), `Data/AccountData.cs`, `Data/StateData.cs`

**Verification:**
- `GET /api/rest/accounts` → paginated list with camelCase balances
- `GET /api/rest/accounts/{addr}/balance` → single balance
- `POST /api/rest/accounts/import` with key in body → works
- `POST /api/rest/accounts/import` without key in body → 400 VALIDATION_ERROR

### Phase 4: Transactions (10 endpoints)
**"Send money and check on it."**

**Deliverables:**
- `Api/Rest/Controllers/TransactionsController.cs`
- `Api/Rest/Models/Requests/TransactionRequests.cs` — `SendTransactionRequest` (fromAddress, toAddress, amount all `[Required]`), `RawTransactionRequest`, etc.

**Endpoints:** Full transactions table from Route Design above.

**Key files to reference:** `Controllers/TXV1Controller.cs` (all TX operations), `Controllers/V1Controller.cs` (SendTransaction, GetMempool), `Data/TransactionData.cs`, `Services/TransactionValidatorService.cs`

**Verification:**
- `POST /api/rest/transactions/send` with valid body → 201 with TX hash
- `POST /api/rest/transactions/send` missing amount → 400 VALIDATION_ERROR
- `POST /api/rest/transactions/send` with wallet locked → 401 WALLET_LOCKED
- `GET /api/rest/transactions?status=pending&page=1` → paginated list
- `GET /api/rest/transactions/{hash}` → single TX detail

### Phase 5: Blocks & Network (13 endpoints)
**Chain data and network status.**

**Deliverables:**
- `Api/Rest/Controllers/BlocksController.cs`
- `Api/Rest/Controllers/NetworkController.cs`

**Endpoints:** Full blocks + network tables from Route Design above.

**Key files to reference:** `Controllers/V1Controller.cs` (GetLastBlock, GetBlockByHeight, GetBlockByHash, GetPeerInfo, AddPeer, BanPeer, UnbanPeer, GetMasternodes, NetworkMetrics), `Controllers/IntegrationsV1Controller.cs` (Network, Height), `Controllers/ExplorerController.cs` (GetBlocks), `Data/BlockData.cs`, `Data/BlockchainData.cs`

**Verification:**
- `GET /api/rest/blocks/latest` → latest block in camelCase envelope
- `GET /api/rest/blocks?page=1&pageSize=10` → paginated blocks
- `GET /api/rest/network/height` → current height

### Phase 6: Signatures & ADNR (7 endpoints)
**Identity and naming.**

**Deliverables:**
- `Api/Rest/Controllers/SignaturesController.cs`
- `Api/Rest/Controllers/AdnrController.cs`
- `Api/Rest/Models/Requests/SignatureRequests.cs` — `CreateSignatureRequest` (address, message `[Required]`), `VerifySignatureRequest`
- `Api/Rest/Models/Requests/AdnrRequests.cs`

**Endpoints:** Full signatures + ADNR tables from Route Design above.

**Key files to reference:** `Controllers/V1Controller.cs` (CreateSignature, ValidateSignature), `Controllers/TXV1Controller.cs` (CreateAdnr, TransferAdnr, DeleteAdnr), `Controllers/V2Controller.cs` (ResolveAdnr, ResolveAddressAdnr), `Services/SignatureService.cs`

### Phase 7: Smart Contracts & NFTs (16 endpoints)
**Full NFT lifecycle.**

**Deliverables:**
- `Api/Rest/Controllers/SmartContractsController.cs`
- `Api/Rest/Models/Requests/SmartContractRequests.cs` — DTOs for create, transfer, sale operations

**Endpoints:** Full smart-contracts table from Route Design above.

**Key files to reference:** `Controllers/SCV1Controller.cs`, `Services/SmartContractService.cs`, `Services/SmartContractWriterService.cs`, `Services/SmartContractReaderService.cs`, `Data/StateData.cs`, `Models/SmartContractMain.cs`

### Phase 8: Tokens & Voting (17 endpoints)
**Fungible tokens and governance.**

**Deliverables:**
- `Api/Rest/Controllers/TokensController.cs`
- `Api/Rest/Controllers/VotingController.cs`
- `Api/Rest/Models/Requests/TokenRequests.cs`
- `Api/Rest/Models/Requests/VotingRequests.cs`

**Endpoints:** Full tokens + voting tables from Route Design above.

**Key files to reference:** `Controllers/TKV2Controller.cs`, `Controllers/VOV1Controller.cs`, `Services/TokenContractService.cs`, `Models/Vote.cs`, `Models/TopicTrei.cs`

### Phase 9: Validators & Reserve Accounts (19 endpoints)
**Staking infrastructure and xRBX.**

**Deliverables:**
- `Api/Rest/Controllers/ValidatorsController.cs`
- `Api/Rest/Controllers/ReserveAccountsController.cs`
- `Api/Rest/Models/Requests/ValidatorRequests.cs`
- `Api/Rest/Models/Requests/ReserveAccountRequests.cs`

**Endpoints:** Full validators + reserve-accounts tables from Route Design above.

**Key files to reference:** `Controllers/ValidatorController.cs`, `Controllers/V1Controller.cs` (validator operations), `Controllers/RSV1Controller.cs`, `Controllers/V2Controller.cs` (ValidatorPool), `Services/ValidatorService.cs`, `Models/ReserveAccount.cs`

### Phase 10: Beacons, Shops & Advanced (37+ endpoints)
**DST protocol, beacons, and remaining endpoints.**

**Deliverables:**
- `Api/Rest/Controllers/BeaconsController.cs`
- `Api/Rest/Controllers/ShopsController.cs`

**Endpoints:** Full beacons + shops tables from Route Design above.

This is the largest phase. The shops controller alone covers ~60 v1 endpoints, many of which can be consolidated into cleaner REST resources. Plan the shop route structure separately before implementation.

**Key files to reference:** `Controllers/BCV1Controller.cs`, `Controllers/DSTV1Controller.cs`, `Controllers/WebShopV1Controller.cs`

---

## Files Modified Outside `Api/Rest/` (Minimal)

Only **Startup.cs** is modified, with these additive changes:

1. **Swagger doc** — add `c.SwaggerDoc("rest", ...)` alongside existing `v1` doc
2. **Swagger UI** — add `c.SwaggerEndpoint("/swagger/rest/swagger.json", "VFX REST API")`
3. **Middleware bypass** — add `/api/rest/wallets/status` and `/api/rest/wallets/unlock` to the path bypass
4. **Validation error format** — add `services.Configure<ApiBehaviorOptions>(...)` for envelope-formatted validation errors
5. **Exception filter registration** — add `services.AddScoped<RestExceptionFilter>()`

No existing controllers, models, services, or data layer files are modified.

---

## Verification (Per Phase)

After each phase:
1. **Build**: `dotnet build` — must compile with zero errors
2. **Swagger**: Navigate to Swagger UI — REST API doc appears as separate document with only REST endpoints
3. **Smoke test**: Start wallet, hit key endpoints via curl
4. **Auth test**: Verify requests without valid API token get 403 with error envelope (except health check)
5. **Validation test**: POST with missing required fields → 400 VALIDATION_ERROR with field details
6. **Error test**: Trigger an error condition → structured error envelope, not raw exception
7. **Regression**: Hit a few existing v1 endpoints to confirm they still work unchanged
8. **camelCase check**: Verify all response fields are camelCase, not PascalCase

---

## Notes

- **No existing controllers modified.** All new files under `Api/Rest/`, only Startup.cs touched.
- Existing C# models (PascalCase properties) serialized as camelCase via Newtonsoft `CamelCasePropertyNamesContractResolver` at the response level.
- `services.AddControllers()` in Startup.cs auto-discovers all controllers including new ones — no additional registration needed.
- For endpoints that call static service methods (most of them), new controllers call the same static methods the old controllers do.
- The REST auth filter is intentionally separate from `ActionFilterController` to avoid coupling to the legacy password-in-URL pattern.
