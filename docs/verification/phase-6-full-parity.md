# Phase 6 — Full v1 → v2 API Parity (2026-07-18)

Closes the 168-endpoint gap identified in
[drift-audit-2026-07.md](drift-audit-2026-07.md). Ruling 4 satisfied: **all 535
v1 endpoints are now classified** in `docs/api/v2-coverage-map.txt` — covered by
a v2 equivalent or explicitly classified out with a one-line reason.

## Result

| Status | Before | After |
|---|--:|--:|
| covered | 101 | **248** |
| gap (actionable) | 168 | **0** |
| omit (deliberate, reasoned) | 198 | 219 |
| oos (P2P/consensus/internal) | 68 | 68 |

v2 REST surface: 170 → **282 endpoints** across 17 controllers
(`tools/generate-api-docs.sh` regenerated `docs/api-v2/`). `tools/api_drift.py
check` exits 0; the gate is wired into `.github/workflows/dotnet.yml` as the
`api-drift` job per [drift-policy.md](../api/drift-policy.md).

Every commit in this phase was verified with `dotnet build` (0 errors) and
`SecretPreservationTests` + `StateSnapshotTests` (16/16 green).

## What was added (commit per group)

| Commit | Group | New v2 surface |
|---|---|---|
| 8cf8ad8a | vBTC | `VbtcController` (api/rest/vbtc): contracts, balances, validators (read), MPC ceremonies, contract creation (local + raw + raw-tx pairs), transfers, all withdrawal flows (local, raw, six raw-tx build/send pairs, FROST prepare/execute), shielded vBTC, Base bridge. 51 v1 endpoints covered, 15 omitted. |
| d67b3ab3 | Bitcoin | `BitcoinController` (api/rest/bitcoin): BTC accounts, UTXOs, transactions (send/fee/RBF/broadcast), BTC ADNR, tokenized BTC (arbiter model), tokenized balances. 32 covered, 1 omitted. |
| 75393370 | Privacy | `PrivacyController` (api/rest/privacy): PLONK status, shielded VFX + shielded vBTC full lifecycle, address/viewing-key management, scan/resync. All 21 covered. |
| 1310c00a | GUI Wallet | 29 endpoints covered by pre-existing v2 twins (wallet services verified as thin wrappers over the same core services); 7 new v2 endpoints: bridge preflight/retry/force-retry/base-address, withdrawals/cancel-tx, bitcoin base-balances + link-evm, privacy addresses, transactions `address` filter. 36 covered, 2 omitted. |
| 65c31e48 | Explorer | Mapped onto existing v2 blocks/transactions/accounts resources + new `network/stats`. 7 covered, 3 omitted. |

## Classified out — rationale summary

Reasons are inline in `v2-coverage-map.txt` (`omit ... -- reason`). Groups:

- **Node-operator / validator plumbing** (vBTC): S3C pool ops, FROST blacklist
  admin, exit-burn scanner controls, attestation signing (caster→validator
  protocol), validator heartbeat (no-op) and cancellation voting.
- **Deprecated / stub in v1**: `RelayPendingBridgeLocks` (no relay queue),
  `TransferVBTCMulti` (returns not-supported), wallet `submitMint` (manual
  caster mint removed).
- **Dev-only**: `ConfigureBridge` ("only use in development/demo" per its own
  docstring; production uses config/env).
- **Not API surface**: wallet + explorer HTML pages, explorer SSE stream,
  API banners; explorer universal search (UI aggregator over per-resource
  lookups that are individually covered).

Note: `genmap` regeneration rewrites map comments — re-add omit reasons by hand
if the map is ever regenerated from scratch.

## The ceremony-state boundary (core-refactor STOP item, resolved additively)

v1 keeps MPC ceremony state (`_ceremonies`) and pending raw-tx state
(`_pendingRawVbtcTxs`) as **private statics on the v1 controller class**. There
is no service-layer home for them, and exposing v1's stores to v2 would require
modifying frozen v1 code. Resolution:

- v2 keeps **its own** stores with identical semantics (including the S3C-aware
  ceremony execution path). A mint or raw-tx flow started on v2 must finish on
  v2, and vice versa. This is documented on the controller.
- `CeremonyCleanupService` only prunes the v1 store, so v2 prunes its own store
  opportunistically on every ceremony-touching request with the same TTLs
  (1h active / 1h terminal).
- The pending raw-tx store mirrors v1's (entries removed only on submit — same
  unbounded-growth caveat as v1; not worsened).

If Aaron later moves ceremony state into a shared service, both controllers can
point at it and this boundary disappears.

## Deliberate v2 divergences (all documented in commit messages)

1. **Safe-by-default key exposure**: `bitcoin/accounts*` omit private keys
   unless `omitKeys=false` is passed. v1 defaults to including them.
2. **Side-effecting v1 GETs became POSTs** (send, ADNR ops, reset, broadcast,
   transfer-ownership, shield/unshield path params → bodies).
3. **Envelope**: v1's ad-hoc `{Success, Message, ...}` JSON becomes the v2
   `ApiResponse` envelope; legacy service JSON strings are unwrapped via
   `RestBaseController.FromLegacyJson`. Null fields are dropped by the v2
   serializer settings (e.g. `DepositAddressBalance` for non-owners).
4. **`GetTokenizedList` is awaited** — v1 serializes the raw `Task` wrapper.
5. **Mirrored v1 quirks (bug-compatible, flagged for Aaron, not fixed)**:
   `GetValidatorList` ignores `activeOnly`; BTC `GetBitcoinTXList` computes and
   then discards its `includeTokens` filter. Fixing these changes v1-observable
   behavior, which is out of scope for this phase.

## Semantic spot-checks (route parity ≠ behavior parity)

Checked v2 reimplementations line-by-line against current v1 + the moved data
layer (StateData +647, TransactionData +324 since March):

- **tx-send**: v2 `SendRaw`/`Verify`/`EstimateFee`/`CalculateHash` are verbatim
  the current v1 `TXV1Controller` pipeline (same `Data` re-serialization,
  `ToAddressNormalize`, `TKNZ_WD_OWNER` skip-verify flag, rating,
  `AddToPool` → `SendTXMempool`). v2 `Send` and v1 `SendTransaction` both
  delegate to `WalletService.SendTXOut`; no divergence is possible where both
  layers call the same service. **One mechanical difference**: v1 inlines the
  wallet-encryption check ("type in wallet encryption password first"), v2
  enforces the same rule via `RestApiAuthFilter.EncryptionRequiredActions`
  (401 WALLET_LOCKED). Equivalent enforcement, different response shape.
- **balance**: v2 `accounts/{address}/balance` reads on-chain state
  (`StateData.GetSpecificAccountStateTrei`) — same call the current v1
  `V2Controller.GetBalances` uses for token accounts. **Known semantic
  difference**: v1 `GetBalances` reports the *local wallet's cached* balance
  (`account.Balance`, optimistically debited on send) while v2 reports the
  *state-trei* balance. These diverge while a send is in-flight. v2's value is
  chain truth; flagged as intended, not a bug.
- **reserve accounts**: v2 `ReserveAccountsController` matches current v1
  `RSV1Controller` check-for-check (publish: `AvailableBalance < 5`,
  `IsNetworkProtected`; send: xRBX→xRBX rejection + address validation;
  identical `ReserveAccount.*` service calls for create/publish/send/
  transfer-nft/callback/recover/restore/unlock). No drift found.
- **vBTC balances** (new surface, custody-critical): v2 mirrors the v1
  owner-vs-holder split (owner = live ElectrumX deposit balance + ledger sum,
  holder = ledger sum), the local balance-cache refresh, the ElectrumX
  fallback-to-cached path, and pending-withdrawal deduction.

## Security notes

- Every new v2 action that signs with (or derives from) local wallet key
  material was added to `EncryptionRequiredActions`:
  `CreateVbtcContract`, `CreateVbtcContractRaw`, `TransferVbtc`,
  `RequestVbtcWithdrawal`, `CompleteVbtcWithdrawal`, `CancelVbtcWithdrawalTx`,
  `ShieldVbtc`, `BridgeVbtcToBase`, `SendBtcTransaction`, `ReplaceBtcByFee`,
  `TransferBtcCoin`, `TransferBtcCoinMulti`, `WithdrawBtcCoin`,
  `TokenizeBitcoin`, `ImportBtcPrivateKey`, `ShieldVfx`,
  `CreateShieldedAddressFromAccount`, `GenerateShieldedAddress`.
  Actions already gated by name (`TransferOwnership`, `CreateAdnr`,
  `TransferAdnr`, `DeleteAdnr`, `ShieldVbtc` for the privacy twin) inherit.
- Post-review additions (Greptile on PR #23): `CreateAccount` (VFX + BTC account
  creation returns fresh private keys that cannot be password-protected while
  the wallet is locked), `ResetAccounts` (wipes BTC UTXOs/balances), and
  `ExportViewingKey` (exports stored shielded key material — stricter than the
  ungated v1 posture, consistent with v2's safe-by-default stance). The
  shielded spend endpoints also gained broadcast-exception rollback (notes are
  unmarked if the broadcast throws, not just when it reports failure) — the
  mark-without-rollback-on-throw pattern is inherited from v1 and flagged for
  the v1 track.
- Raw/pre-signed flows (client signs offline) and shielded-password flows are
  deliberately **not** gated, matching v1 posture.
- The pre-existing posture that a null `Globals.APIToken` leaves the surface
  unauthenticated is unchanged (same as v1; out of scope here).

## Evidence

- `python3 tools/api_drift.py check` → exit 0, `gap: 0`, `UNCLASSIFIED: 0`.
- `dotnet build ReserveBlockCore` → 0 errors after every commit.
- `dotnet test --filter SecretPreservation|StateSnapshot` → 16/16 after every
  commit.
- Docs: `tools/generate-api-docs.sh` regenerated the v2 reference docs (282
  endpoints, 17 controllers; new `vbtc.md`, `bitcoin.md`, `privacy.md`,
  updated index). Generated reference docs live in `docs/api-v2/`; the parity
  ledger (`v2-coverage-map.txt`) and `drift-policy.md` stay in `docs/api/`.
