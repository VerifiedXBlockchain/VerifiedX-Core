# VerifiedX Core: guide for AI agents and developers

VerifiedX Core is the C# .NET 6 node for the VerifiedX (VFX) Layer 1: block production and validation, P2P, the CLI wallet, the REST API, vBTC (FROST MPC over Bitcoin), the Base bridge, and the shielded privacy pool. Bugs here can split the chain or lose funds. Read this file before changing code. [CONTRIBUTING.md](CONTRIBUTING.md) has the PR process.

This repository is public. Never put private issue-tracker content, exploit details, server addresses or credentials in code, comments, commits or PRs.

## Loading this file

Claude Code loads `AGENTS.md` automatically, but only when the repo has no `CLAUDE.md` or `CLAUDE.local.md`. Both are gitignored, so they are personal. If you keep one, make its first line:

```
@AGENTS.md
```

Put personal notes below that line. Folder guides such as [VerifiedXCore.Tests/AGENTS.md](VerifiedXCore.Tests/AGENTS.md) load when you work in that folder.

## Commands

```bash
dotnet build                                   # whole solution; must have 0 errors
dotnet test                                    # full suite; runs serially, ~9 min, ~1,930 tests
dotnet test --filter "FullyQualifiedName~SecretPreservation|FullyQualifiedName~StateSnapshot"   # ship gate: 16 tests, run before every PR
dotnet test --filter "FullyQualifiedName~<ClassName>"                                           # one class
dotnet run --project VerifiedXCore -- testnet enableapi                                          # local testnet node, API on 17292
```

On macOS exactly 5 `FrostKeyBackupTests` (the `AesGcm_*` and `BlobFormat_*` ones) fail, because .NET 6 has no `AesGcm` on Apple platforms. They pass in CI (Ubuntu and Windows). `Frost/mac/libfrost_ffi.dylib` is arm64 only, so FROST tests need an arm64 `dotnet`, not one running under Rosetta.

Command-line args are parsed in `VerifiedXCore/Program.cs` (case-insensitive: `testnet`, `headless`, `enableapi`, `apitoken=`, `rebuildstate`, `revertblock=N`, `warden`, and others). Release builds: `.github/workflows/build-dotnet.yml` (manual dispatch) and [RBXBuildInstructions.md](RBXBuildInstructions.md).

## Branches

- `testnet-staging` is where work merges first. Branch from it (or from `testnet`) and open PRs against it, merged with a merge commit (never squash or rebase-merge).
- `testnet` takes `testnet-staging` by PR once a batch is reviewed and CI passes, every few days. No PRs from other branches into `testnet`.
- `main` is mainnet. It only takes release PRs from `testnet`.
- `VerifiedXCore/Program.cs` sets `Globals.IsTestNet = true;` under `//Forced Testnet` on `testnet`; the line is commented out on `main`. That one line is how the two branches differ. Never change it in a fix or feature PR.

## Hard rules

1. **Rule changes activate at a block height.** Any change to what makes a block or transaction valid, or to how state is applied, must be gated so that blocks before the height still validate the old way. See "Height-gated rules" below.
2. **Consensus, validation and state application changes need @Mathis1337's review.** That covers the folders marked "consensus" in the routing table.
3. **Never change serialized shapes casually.** `Models/` classes, transaction `Data` payloads and anything hashed on-chain (including generated Trillium code from `SmartContractSourceGenerator/`) must stay byte-compatible with history.
4. **Every fix comes with a test** that fails before the fix and passes after it, plus a test that the legitimate behaviour still works.
5. **Never write to real node data.** Tests point `Globals.CustomPath` at a temp folder (see the tests guide). Never touch `~/rbx`, `~/rbxtest` or a running node's databases.
6. **Commit trailers for tracked findings.** End the message with `Fixes-Issue: VerifiedX-Issues#<n>`, never `Fixes #<n>`, and describe the behaviour the fix establishes, not how to exploit the old code.

## Height-gated rules

Every activation height is a `public static long XxxHeight` (or `const`) in `VerifiedXCore/Globals.cs`, with a `///` comment explaining the rule. To add one:

1. Declare the mainnet height in `Globals.cs`. It must be above the tip by the time every validator runs the new build. Related fixes can share one height (`FundLossAuditHeight`).
2. Set the testnet value in `Globals.ApplyNetworkHeights()` (`GlobalsNetworkHeights.cs`): `Globals.X = Globals.IsTestNet ? <testnet> : Globals.X;`. `Program.cs` and the Warden both call it, so it is the only place.
3. Gate on the height of the block or transaction being processed (`tx.Height >= Globals.XHeight`), never the local tip. Example: `Data/StateData.cs`, `BridgeIntraBlockGuardHeight`.

`999_999_999_999` means "not active yet".

## Where things live

Paths are under `VerifiedXCore/`. "consensus" means a bug can fork the chain; "funds" means it can lose or mint value.

| Area | Folder / files | Risk |
|---|---|---|
| Block validation | `Services/BlockValidatorService.cs` (`ValidateBlock`), `Utilities/BlockVersionUtility.cs` | consensus |
| Transaction validation | `Services/TransactionValidatorService.cs` (`VerifyTX`), `Services/LedgerIntegrityRules.cs` | consensus |
| State application | `Data/StateData.cs` (`UpdateTreis`), `Data/DbContext.cs` (static LiteDB handles) | consensus |
| Transaction types | `Models/Transaction.cs` (`TransactionType` enum, 0–42) | consensus |
| Activation heights, global state | `Globals.cs`, `GlobalsPrivacy.cs`, per-network overrides in `GlobalsNetworkHeights.cs` | consensus |
| Caster and validator loops | `Nodes/` (`BlockcasterNode.cs`, `ValidatorNode.cs`) | consensus |
| P2P (SignalR hubs) | `P2P/`, hub routes in `StartupP2P*.cs` | consensus, DoS |
| vBTC, FROST, Base bridge, Bitcoin wallet | `Bitcoin/` (`Services/VBTCService.cs`, `Services/BridgeTxRules.cs`, `FROST/`) | funds, consensus |
| Shielded pool, PLONK proofs | `Privacy/` (`PrivateTransactionValidatorService.cs`, `PrivateTxSupplyRules.cs`, `ShieldedPoolService.cs`) | funds, consensus |
| Native libraries | `Frost/`, `Plonk/` (prebuilt FFI binaries; Rust sources are in other repos, see their READMEs) | crypto |
| Keys and signatures | `EllipticCurve/`, `BIP32/`, `BIP39/`, `SecretSharing/` | crypto |
| Smart contracts | `Trillium/` (runtime), `SmartContractSourceGenerator/` (generated code is hashed on-chain) | consensus |
| vBTC v1 arbiter | `Arbiter/` | funds |
| Auctions, decentralized shops | `Engines/`, `DST/` | funds |
| REST API | `Controllers/` and `Bitcoin/Controllers/` (`<Area>V<n>Controller`; V1 and V2 both live), auth in `Controllers/ActionFilterController.cs` and `Utilities/ApiRequestGuard.cs` | funds |
| Beacons (asset file relay) | `Beacon/` | medium |
| Browser wallet services | `BrowserWalletServices/` | medium |
| Config, ports, data paths | `Config/Config.cs`, `Utilities/GetPathUtility.cs` | low |
| CLI menu | `Commands/` | low |
| Tests | `VerifiedXCore.Tests/` (has its own guide) | |

The repo-root folders `ReserveBlockCore/` and `VerfiedXCore.Tests/` (misspelled) hold only old build output. Ignore them.

Testnet uses ports 13338 (P2P) and 17292 (API), addresses that start with `x`, and Bitcoin TestNet4. Mainnet uses 3338 and 7292. Runtime data lives in `~/rbx` or `~/rbxtest` on macOS, and under `LocalApplicationData/RBX` or `RBXTest` elsewhere.

## Platform context (optional)

The private repo `VerifiedXBlockchain/VerifiedX-PlatformContext` documents how Core fits with the wallet, explorer, SDKs and other components. To use it, clone it and set an environment variable in your shell profile:

```bash
export VFX_PLATFORM_CONTEXT=/path/to/vfx-platform-context
```

Agents: when a task involves another component (the GUI wallet, the explorer API, the SDKs, the bridge contract), read `$VFX_PLATFORM_CONTEXT/CLAUDE.md` for its routing table, then the relevant files, such as `core/integration.md`, `core/vbtc-v2.md` and `core/vbtcb.md`. If the variable is unset, continue without it. Those docs are background: where they disagree with the code in this repo, the code wins.

## Writing code here

- Match the code around you; there is no formatter beyond `.editorconfig`.
- Log through `LogUtility` / `ErrorLogUtility`, as the surrounding code does.
- Keep comments to the rule and why it holds, not the history of the bug.
