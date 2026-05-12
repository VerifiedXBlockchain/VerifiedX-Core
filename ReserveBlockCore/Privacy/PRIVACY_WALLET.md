# Privacy wallet & API (VerifiedX-Core)

Quick reference for shielded VFX / vBTC flows exposed by the node. Identities are **VFX addresses** and **`zfx_` shielded addresses** — never numeric user IDs in these routes.

## VFX (`PrivacyV1Controller`, route prefix `privacyapi/PrivacyV1`)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `GetPlonkStatus` | Native PLONK caps, `EnforcePlonkProofsForZk`, params mirror size, whether `VFX_PLONK_PARAMS_PATH` is set |
| POST | `ShieldVFX` | T→Z (sign with transparent key) |
| POST | `UnshieldVFX` | Z→T |
| POST | `PrivateTransferVFX` | Z→Z |
| POST | `ConsolidateShieldedVFX` | Merge **two smallest** VFX notes into one (Z→Z to self); repeat to fold more dust |
| GET | `GetShieldedBalance?zfxAddress=&includeCommitments=` | Balances; optional sanitized commitment list (no note randomness) |
| GET | `GetShieldedPoolState?asset=` | Pool row for `VFX` or `VBTC:…` |
| POST | `GenerateShieldedAddress` | Derive `zfx_` from seed |
| POST | `ScanShielded` | Trial-decrypt notes in `[FromHeight, ToHeight]`; response includes `BlocksScanned`, `TransactionsScanned` |
| POST | `ExportViewingKey` / `ImportViewingKey` | View-only wallet |

## vBTC (`VBTCController`, prefix `vbtcapi/VBTC`)

| Method | Path (privacy region) | Purpose |
|--------|------------------------|---------|
| POST | `ShieldVBTC` | T→Z vBTC (+ co-shield warning if low shielded VFX) |
| POST | `UnshieldVBTC` | Z→T |
| POST | `PrivateTransferVBTC` | Z→Z |
| GET | `GetShieldedVBTCBalance` | Per-contract shielded balance |
| GET | `GetShieldedVBTCPoolState/{scUID}` | Pool state |

## Parameters & PLONK

- Universal params: env **`VFX_PLONK_PARAMS_PATH`**, file formats **`VXPLNK01`** / **`VXPLNK02`** — see [`../Plonk/PARAMS.md`](../Plonk/PARAMS.md).
- **`VXPLNK02`** required for native **`plonk_prove_v0`** (wallet/node proving).

## Automated tests (privacy only)

```bash
dotnet test VerfiedXCore.Tests/VerfiedXCore.Tests.csproj --filter "FullyQualifiedName~PrivacyLayerTests"
```

Full solution tests may fail in unrelated native suites (e.g. FROST); use the filter above for privacy regressions.

## Deferred / out of node scope

- Full **fee-leg** ledger for vBTC ZK (VFX nullifier + fee change commitment in `DB_Privacy`) — coordinated builder + `PrivateTxLedgerService` work.
- **Security audit** of production PLONK circuits, **testnet** rollout, **performance** benchmarks — process / ops, not this file.

See root **`privacy_progress.md`** for phase checklist vs implementation.
