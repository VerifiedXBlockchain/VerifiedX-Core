# v1 -> v2 API Drift Audit — 2026-07

Audit of what changed in the v1 API surface between the `feature/api-v2` branch
point (`2e579e9c`, 2026-03-12) and current `origin/main`, and how the v2 REST
layer (`ReserveBlockCore/Api/Rest/`) covers it. Generated after merging
`origin/main` (293 commits) into `feature/api-v2`.

Reproduce the inventory numbers with `tools/api_drift.py` (see
[../api/drift-policy.md](../api/drift-policy.md)).

## Merge result

- **Merge**: `origin/main` -> `feature/api-v2`, merge commit. Git's `ort`
  strategy auto-resolved `Startup.cs` with **no conflict** — main's only change
  there was commenting out `app.UseElmah();`, which sits several lines from the
  branch's Swagger/REST registration. Both sides are present in the merged file
  (main's `//app.UseElmah();` and the branch's `RestExceptionFilter` DI, the
  `rest` Swagger doc, and the `/api/rest/wallets/{unlock,status}` startup-gate
  bypass). Main's 2026-07-16 startup work (improper-shutdown detection, snapshot
  restore) lives in `Program.cs` / `Services/StartupService.cs`, not `Startup.cs`,
  so it did not interact with the branch.
- **Build**: `dotnet build ReserveBlockCore` (SDK 6.0.428) — **0 errors**, 2029
  pre-existing warnings. The v2 controller types are present in the built DLL.
  This is the key merge-integrity result: despite 293 commits of drift, every
  call the v2 layer makes into the core service/data layer still compiles.
- **Tests**: `SecretPreservation` + `StateSnapshot` filters — **16 passed, 0
  failed**.

## Method and scope

- v1 inventory extracted from `ReserveBlockCore/Controllers/` and
  `ReserveBlockCore/Bitcoin/Controllers/`; v2 from
  `ReserveBlockCore/Api/Rest/Controllers/`.
- Historical diff = v1 endpoints at `2e579e9c` vs current, matched on
  `VERB + normalized-route`. A route rename shows as one removed + one added.
- **Architectural note that shapes the whole audit:** v2 is **not** a wrapper
  over v1. Every v2 controller calls the data/service layer directly
  (`AccountData`, `StateData`, `TransactionData`, `WalletService`, …) — a
  *parallel reimplementation* over the same backend, with its own REST routes.
  Consequences: (1) v1 route changes don't break v2's compile; (2) the real
  drift risk is **semantic** — a v1 controller and the shared service get new
  behavior while v2's parallel path is left untouched. Route-level coverage (this
  audit + the tool) catches new/removed endpoints, not semantic divergence. See
  "Semantic drift" below.

## v1 surface drift since 2026-03-12

- v1 endpoints: **419 -> 535** (**+117 new, -1 removed**).
- The single removal is a rename inside the new vBTC withdrawal flow:
  `POST vbtcapi/vbtc/completewithdrawalraw` -> `.../executecompletewithdrawalraw`.
- **Every controller the v2 layer mirrors has zero route churn.** All 117 new
  endpoints landed in five controllers; the nine controllers v2 tracks (V1, V2,
  TX, SC, TK, DST, WebShop, VO, RS) are byte-for-byte route-stable.

| Controller | 2026-03-12 | now | +new | -rem | v2 relationship |
|---|--:|--:|--:|--:|---|
| V1Controller | 81 | 81 | 0 | 0 | tracked (accounts/wallets/blocks/network/adnr/signatures) |
| V2Controller | 19 | 19 | 0 | 0 | tracked (balances/adnr) |
| TXV1Controller | 39 | 39 | 0 | 0 | tracked (transactions) |
| SCV1Controller | 41 | 41 | 0 | 0 | tracked (smart-contracts) |
| TKV2Controller | 15 | 15 | 0 | 0 | tracked (tokens) |
| DSTV1Controller | 65 | 65 | 0 | 0 | tracked (shops) |
| WebShopV1Controller | 15 | 15 | 0 | 0 | tracked (shops) |
| VOV1Controller | 11 | 11 | 0 | 0 | tracked (voting) |
| RSV1Controller | 13 | 13 | 0 | 0 | tracked (reserve-accounts) |
| ExplorerController | 9 | 10 | **+1** | 0 | gap |
| PrivacyV1Controller | 0 | 21 | **+21** | 0 | gap — new domain |
| WalletController | 11 | 38 | **+27** | 0 | gap — GUI wallet |
| VBTCController | 27 | 66 | **+40** | 1 | gap — vBTC/bridge |
| BTCV2Controller | 33 | 33 | 0 | 0 | gap — pre-existing |
| ADJV1Controller | 7 | 7 | 0 | 0 | out of scope (P2P) |
| BCV1Controller | 15 | 15 | 0 | 0 | out of scope (P2P) |
| ValidatorController | 15 | 43 | **+28** | 0 | out of scope (consensus P2P) |
| IntegrationsV1Controller | 3 | 3 | 0 | 0 | out of scope (internal) |

## v2 coverage classification (current)

From `tools/api_drift.py check` against `docs/api/v2-coverage-map.txt`. Every one
of the 535 v1 endpoints is classified (gate is green):

| Status | Count | Meaning |
|---|--:|---|
| `covered` | 101 | v1 endpoint has a confirmed v2 equivalent |
| `omit` | 198 | tracked controller, no v2 equivalent — v2 exposes a curated subset by design |
| `gap` | 168 | integrator-facing, v2 should cover but does not (**actionable**) |
| `oos` | 68 | node-to-node P2P / consensus / internal — never part of a REST surface |

v2 layer itself: **170 REST endpoints** across 14 controllers.

> `covered` is assigned by a name/route heuristic and is deliberately
> conservative; some `omit` endpoints may in fact have a v2 equivalent. The gate
> does not depend on this split — it depends only on every endpoint being
> classified. Refine `covered`/`omit` by hand as needed.

## Coverage gaps by domain (the `gap` bucket, 168 endpoints)

| Domain (controller) | Endpoints | New since branch | Severity | Notes |
|---|--:|--:|---|---|
| **vBTC / bridge** (VBTCController) | 66 | +40 | **High** | vBTC v2 + Base-chain bridge. No v2 controller exists for vBTC at all. |
| Bitcoin (BTCV2Controller) | 33 | 0 | Medium | Pre-existing gap — v2 never covered BTC. Route-stable. |
| **Privacy / shielded** (PrivacyV1Controller) | 21 | +21 | **High** | Entirely new domain (shielded VFX/vBTC, PLONK). Zero v2 representation. |
| GUI wallet (WalletController) | 38 | +27 | Medium | `[Route("wallet")]` browser-wallet API. New endpoints are privacy + bridge; overlaps the two domains above. |
| Explorer (ExplorerController) | 10 | +1 | Low | Read-only public data; v2's blocks/network/transactions cover most equivalents. |

### vBTC v2 bridge — special attention

The vBTC controller nearly tripled (27 -> 66) and is the single largest area of
new integrator-facing surface, entirely uncovered by v2. The 40 new endpoints
group as:

- **Bridge / Base chain (integrator-facing, High):** `getbridgeconfig`,
  `getbridgestatus`, `getbridgelocks/{scUID}`, `getbridgelocksbyowner/{owner}`,
  `getbridgelockstatus/{lockId}`, `getbasebalance/{evmAddress}`,
  `getcontracthealth/{scUID}`, `getmintattestation/{lockId}`,
  `configurebridge`, `bridgetobase`.
- **Raw-transaction builders / senders (integrator-facing, High):**
  `get(raw)createcontracttxdata` + `sendrawcreatecontracttx`,
  `getrawtransfervbtcdata` + `sendrawtransfervbtctx`,
  `getraw{request,complete,cancel}withdrawaltxdata` + matching
  `sendraw…withdrawaltx`, `prepare/execute completewithdrawalraw`. These build
  and submit signed on-chain transactions — the highest-value surface to get
  right if/when v2 grows to include vBTC.
- **MPC / FROST / operational (node-facing, lower REST priority):**
  `prepare/executempcceremonyraw`, `relaypendingbridgelocks`,
  `pollbaseexitburns`, `rescanexitburns/{fromBlock}`, `starts3cautobridge`,
  `gets3cstatus`, `gets3cautobridgestatus`, `getfrostblacklist`,
  `invalidatefrostcontract/{scUID}`, `removefrostblacklist/{scUID}`.
- **Shielded vBTC (overlaps privacy):** `shieldvbtc`, `unshieldvbtc`,
  `privatetransfervbtc`, `getshieldedvbtcbalance`,
  `getshieldedvbtcpoolstate/{scUID}`.

Corresponding new wallet-facing bridge endpoints also appeared on
`WalletController` (`wallet/api/vbtc/bridge/{status,retry,forceretry,submitmint,tobase}`,
`wallet/api/vbtc/withdraw/cancel`, `wallet/api/btc/link-evm`,
`wallet/api/btc/base-balances`) — same domain, same gap.

## Security-relevant observations

- **v2 auth model** (`RestApiAuthFilter`): header token (`apitoken` == `Globals.APIToken`)
  on every action except `GetStatus`; plus a wallet-lock gate returning 401 for a
  hardcoded allowlist of mutating actions (`EncryptionRequiredActions`: Send,
  Mint, Transfer, Burn, CreateAdnr, CastVote, …). Two things to know:
  (1) if `Globals.APIToken` is null the whole v2 surface is unauthenticated —
  same posture as v1, not a merge regression; (2) `EncryptionRequiredActions` is
  a **by-name allowlist maintained by hand** — a new mutating v2 endpoint that
  isn't added to it will not enforce the wallet-lock. Worth a checklist item when
  v2 grows.
- **Startup gate bypass** (`Startup.cs`): the branch added
  `/api/rest/wallets/unlock` and `/api/rest/wallets/status` to the pre-`APIUnlockTime`
  bypass list, mirroring the existing `/api/v1/unlockwallet/` exemption. Consistent
  with v1; not a downgrade. Uses substring `Contains` matching like the existing
  v1 checks.
- No v1 endpoint that v2 covers changed its route, params-in-route, or was
  removed (except the one vBTC rename, which is uncovered). So there is **no
  stale-route risk** in the covered set today.

## Semantic drift (not caught by route-level coverage)

Because v2 reimplements over the shared service/data layer, and main heavily
changed that layer since March (`Transaction.cs`, `StateData.cs` +647,
`TransactionData.cs` +324, `WalletService`, `BlockValidatorService`,
`TransactionValidatorService`, new `StateTrei`/snapshot models), some `covered`
v2 endpoints may now compute or validate differently than their v1 counterparts
even though routes are identical. Build success guarantees signature
compatibility, **not** behavioral parity. Highest-value spots to spot-check if v2
is put in front of integrators:

- Transaction build/submit: v2 `TransactionsController` (`Send`, `SendRaw`,
  `ReplaceByFee`) vs v1 `TXV1Controller` — the tx model and validator changed.
- Balances / account state: v2 `AccountsController.GetBalance` reads
  `StateData.GetSpecificAccountStateTrei` (StateData changed substantially).
- Reserve accounts and smart-contract mint/evolve flows.

This is out of scope for a route-level audit and is called out as a known
limitation, not a finding.

## Bottom line

The merge is clean and builds. The v2-tracked v1 surface did not drift at the
route level at all — v2's route coverage of its intended surface is current. All
new integrator-facing surface since March is concentrated in domains v2 never
implemented: **vBTC/Base bridge (+40) and the privacy/shielded layer (+21)**,
plus GUI-wallet variants of both (+27). Whether v2 should grow to cover vBTC and
privacy is a scope decision for Tyler; until then those 168 endpoints are a
standing, tracked backlog. The one thing to watch going forward is **semantic**
drift on the covered set, which route tooling cannot see.
