# Privacy wallet & API (VerifiedX-Core)

Quick reference for shielded VFX / vBTC flows exposed by the node. Identities are **VFX addresses** and **`zfx_` shielded addresses** — never numeric user IDs in these routes.

## VFX (`PrivacyV1Controller`, route prefix `privacyapi/PrivacyV1`)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `GetPlonkStatus` | Native PLONK caps, params mirror size, and (stage 2) `VfxPi2VerifyAvailable`, `V1ProvingAvailable`, `CircuitPoseidonAvailable`, `ProofRulesHeight`, `ProofRulesActive` |
| POST | `ShieldVFX` | T→Z (sign with transparent key) |
| POST | `UnshieldVFX` | Z→T |
| POST | `PrivateTransferVFX` | Z→Z |
| POST | `ConsolidateShieldedVFX` | Merge **two smallest** VFX notes into one (Z→Z to self); repeat to fold more dust |
| GET | `GetShieldedBalance?zfxAddress=&includeCommitments=` | Balances; optional sanitized commitment list (no note randomness) |
| GET | `GetShieldedPoolState?asset=` | Pool row for `VFX` or `VBTC:…` |
| POST | `GenerateShieldedAddress` | Derive `zfx_` from seed |
| POST | `ScanShielded` | Trial-decrypt notes in `[FromHeight, ToHeight]`; response includes `BlocksScanned`, `TransactionsScanned` |
| POST | `ExportViewingKey` / `ImportViewingKey` | View-only wallet |

### Raw flows (keys held by the caller: web wallet and integrators)

The node does what only a node can do (reads the pool, finds the caller's notes, builds the public inputs, runs the
prover) and never needs a local account, password or shielded wallet row. Built transactions wait in memory keyed by
`Hash` for 30 minutes, as the vBTC raw routes do. All are POST with a JSON body; responses are `{ Success, Result }` or
`{ Success: false, Message }`.

| Path | Body | Purpose |
|------|------|---------|
| `GetRawShieldTxData` | `FromAddress, RecipientZfxAddress, ShieldAmount, TransparentFee?, Memo?` | Unsigned, **proven** T→Z. Result: `Hash, Transaction, Fee, RequiresSignature: true, ExpiresUtc`. Sign `Hash` with the FromAddress key. |
| `GetShieldedNotesRaw` | `ZfxAddress, ViewingKey, FromHeight?, ToHeight?, IncludeSpent?` | Stateless scan by viewing key: each note's `Commitment, NoteHash, Amount, RandomnessB64, TreePosition, BlockHeight, TxHash, Spent, PendingSpend, Spendable, Reason`, plus `UnspentBalance, SpendableBalance, ProofRulesActive`. Reads only the blocks the pool index names. |
| `GetRawUnshieldTxData` | `ZfxAddress, ViewingKey, TransparentToAddress, TransparentAmount, InputCommitments?` | Complete, proven Z→T (ZK-authorised, no signature). Node selects 1–2 notes unless `InputCommitments` names them. Result adds `SpentCommitments, ChangeAmount, MerkleRoot`. |
| `GetRawPrivateTransferTxData` | `ZfxAddress, ViewingKey, RecipientZfxAddress, PaymentAmount, InputCommitments?` | Complete, proven Z→Z; pay your own `zfx_` to consolidate. |
| `VerifyRawPrivateTx` | `Hash, Signature?` | Dry run of the node's full verifier, nothing broadcast. |
| `SendRawPrivateTx` | `Hash, Signature?` | Shield: `Hash + Signature`. Unshield / transfer: `Hash` alone. Verifies, claims the build once, admits to the mempool, broadcasts. |

Flow: shield = `GetRawShieldTxData` → sign `Hash` → `SendRawPrivateTx { Hash, Signature }`. Spend = `GetShieldedNotesRaw`
(optional) → `GetRawUnshieldTxData` / `GetRawPrivateTransferTxData` → `SendRawPrivateTx { Hash }`.

Notes for integrators:

- `ViewingKey` is the 32-byte viewing key as Base64 or 64 hex chars. Derivation (`ShieldedHdDerivation`): spending scalar
  from `m/44'/{coin}'/0'/1'/{index}'` (or `DeriveFromPrivateKey` for a single account) → viewing key =
  SHA-256("VFX/shielded/viewing/v1" ‖ spend) → encryption secret from the viewing key → `zfx_` encodes its public key.
  The node checks the viewing key belongs to the `zfx_` address before it decrypts or spends anything.
- **The viewing key is spend authority** in this scheme (nullifier = Poseidon(vk, note, pos)). Send it only to a node
  you trust, over TLS; a view-only share of it is a spend share.
- A spend binds the pool's current Merkle root. If another commitment lands first the submit fails with
  "merkle_root does not match the current shielded pool Merkle root": rebuild and resubmit.
- Proving runs on the node: ~0.2 s for a shield, ~6 s for an unshield or transfer; the calls are synchronous.
- The routes work while the node's wallet is locked (nothing on the node is unlocked or signed).

## vBTC (`VBTCController`, prefix `vbtcapi/VBTC`)

| Method | Path (privacy region) | Purpose |
|--------|------------------------|---------|
| POST | `ShieldVBTC` | T→Z vBTC (+ co-shield warning if low shielded VFX) |
| POST | `UnshieldVBTC` | Z→T |
| POST | `PrivateTransferVBTC` | Z→Z |
| GET | `GetShieldedVBTCBalance` | Per-contract shielded balance |
| GET | `GetShieldedVBTCPoolState/{scUID}` | Pool state |

## Stage 3: owner-bound notes (fund-loss re-audit, Oct 2026)

The stage-2 circuits let anyone who knew a note's amount and randomness (its sender always does) spend it with any key,
once per key, and an unshield could be copied with another recipient. From the proof-rules epoch the v2 circuits apply:

- **A note names its owner**: `note_hash = Poseidon(amount, randomness, owner_pk)` with
  `owner_pk = Poseidon("VFX_OWNR", nullifier_key)`; a spend proves knowledge of the nullifier key, so only the owner can
  spend and the nullifier is unique per note. The nullifier key is the viewing key reduced into the field
  (`ShieldedKeyMaterial.NullifierKey32`), so the viewing key remains spend authority as before.
- **The unshield recipient is a public input** (`recipient_tag` = SHA-256 of the address, reduced): a redirected copy
  fails its proof.
- **v2 `zfx_` addresses** carry the owner key next to the encryption key. The same keys have a v1 and a v2 string; both
  decode, rows created under v1 are found by either, and `GetShieldedAddresses` reports `zfxAddress` (v2, what to be
  paid at) plus `legacyZfxAddress`. In the epoch a v1 recipient address is refused ("carries no owner key").
- **Params**: `VXPLNK05` (`vfx_plonk_v3.params`): the v2 circuits compiled over the **Zcash Sapling Powers of Tau**
  universal parameters (`phase1radix2m16`, imported by `vfx_plonk_setup --zcash`; generator and pairing checks on
  import). `VXPLNK03` (ownerless circuits) and `VXPLNK04` (the seeded setup, whose tau is public and whose proofs are
  therefore forgeable) are refused by the library. Public-input blobs are `VFXPI1` version 3; capability bit 64.
- Tests: `PrivacyStage2_RoundTripTests.AStrangerWhoKnowsTheNote_CannotSpendIt_WithAnotherKey` and the redirect check in
  `UnshieldOneNote_...` reproduce both re-audit attacks against the new circuits; the plonk repository's
  `v1_vfxpi2.rs` does the same natively.

## Parameters & PLONK

- Universal params: env **`VFX_PLONK_PARAMS_PATH`**, file formats **`VXPLNK01`** / **`VXPLNK02`** — see [`../Plonk/PARAMS.md`](../Plonk/PARAMS.md).
- **`VXPLNK02`** required for native **`plonk_prove_v0`** (legacy stub proving).
- **`VXPLNK05`** (`vfx_plonk_v3.params`; v2 owner-bound circuits over the Zcash Powers of Tau, with prover keys) required for real proving and for the `PrivacyStage2_RoundTripTests` / `PrivacyStage2_RawApiTests` proof tests (`VFX_PLONK_PARAMS`). `VXPLNK03` and the seeded `VXPLNK04` are refused.

## Automated tests (privacy only)

```bash
dotnet test VerifiedXCore.Tests/VerifiedXCore.Tests.csproj --filter "FullyQualifiedName~PrivacyLayerTests|FullyQualifiedName~PrivacyStage2"
```

Full solution tests may fail in unrelated native suites (e.g. FROST); use the filter above for privacy regressions.

## Deferred / out of node scope

- Full **fee-leg** ledger for vBTC ZK (VFX nullifier + fee change commitment in `DB_Privacy`) — coordinated builder + `PrivateTxLedgerService` work.
- **Security audit** of production PLONK circuits, **testnet** rollout, **performance** benchmarks — process / ops, not this file.

See root **`privacy_progress.md`** for phase checklist vs implementation.
