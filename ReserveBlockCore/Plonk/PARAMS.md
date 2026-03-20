# PLONK universal parameters (Phase 4 prep)

## Current behavior

- The native library exposes `plonk_load_params(path)`; [`PLONKSetup.TryLoadParamsFile`](../Privacy/PLONKSetup.cs) mirrors the file into [`Globals.PLONKUniversalParams`](../GlobalsPrivacy.cs) after a successful FFI load.
- **C# Phase 4**: [`PlonkProofVerifier`](../Privacy/PlonkProofVerifier.cs), [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs), and [`PrivateTransactionValidatorService`](../Privacy/PrivateTransactionValidatorService.cs) call `plonk_verify` when proofs are present. [`PLONKSetup.RefreshVerificationCapability`](../Privacy/PLONKSetup.cs) runs at startup; while the **`plonk`** workspace still returns `ErrNotImplemented`, [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) stays false and proofs are not rejected for failing verification.
- **Public inputs v1**: binary layout is built in [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs) (`VFXPI1` header + asset tag + Merkle root + scaled amounts + commitments / nullifiers). The **`plonk`** circuits must decode the same layout when replacing the stub.
- **`plonk_verify` stub**: until circuits ship in the sibling **`plonk`** repo and new `plonk_ffi` binaries are copied into [`Plonk/`](README.md), verification returns `ErrNotImplemented`.

## Configuration

- **Environment variable** `VFX_PLONK_PARAMS_PATH`: optional absolute path to a params file. Call `PLONKSetup.TryLoadParamsFromEnvironment()` during startup if you want zero config-file wiring.
- **Versioning**: when you rotate SRS/params, bump a constant or config value in one place and document the expected filename in release notes so nodes stay compatible.

## Operational checklist (when circuits exist)

1. Run trusted setup (or import a known SRS) in the **`plonk`** repo; export the blob your verifier expects.
2. Ship the file out-of-band or embed distribution policy (e.g. download URL + hash).
3. Ship real `plonk_verify` + proving keys from **`plonk`**; rebuild natives so [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) becomes true; optionally set [`Globals.EnforcePlonkProofsForZk`](../GlobalsPrivacy.cs) for strict ZK mempool rules.

See also [`Plonk/README.md`](README.md) for native library layout.
