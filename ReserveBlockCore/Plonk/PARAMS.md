# PLONK universal parameters (Phase 4 prep)

## Current behavior

- The native library exposes `plonk_load_params(path)`; [`PLONKSetup.TryLoadParamsFile`](../Privacy/PLONKSetup.cs) mirrors the file into [`Globals.PLONKUniversalParams`](../GlobalsPrivacy.cs) after a successful FFI load.
- **C# Phase 4**: [`PlonkProofVerifier`](../Privacy/PlonkProofVerifier.cs), [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs), and [`PrivateTransactionValidatorService`](../Privacy/PrivateTransactionValidatorService.cs) call `plonk_verify` when proofs are present. [`PLONKSetup.RefreshVerificationCapability`](../Privacy/PLONKSetup.cs) reads [`plonk_capabilities()`](../Privacy/PlonkNative.cs); [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) is true only when bit [`CapVerifyV1`](../Privacy/PlonkNative.cs) is set (full cryptographic verify). Until then, malformed public-input blobs can fail early; valid layouts still get `ErrNotImplemented` from the crypto step and are not enforced as invalid proofs.
- **Public inputs v1**: binary layout is built in [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs) (`VFXPI1` header + asset tag + Merkle root + scaled amounts + commitments / nullifiers). The **`plonk`** circuits must decode the same layout when replacing the stub.
- **Scaled amount width**: v1 writes amounts as **LE `u64`** after `×10^18` scaling (same bound as Pedersen path). That is safe for current fees and practical supplies; the long-term spec may allow ranges that need **`u128` / multi-word** public fields—bump the layout in C# and Rust together when circuits require it.
- **`plonk_verify` crypto**: the sibling **`plonk`** repo validates **VFXPI1** layout in native code, then returns `ErrNotImplemented` for the actual PLONK check until circuits + SRS are wired and **`PLONK_CAP_VERIFY_V1`** is enabled. Copy refreshed `plonk_ffi` binaries into [`Plonk/`](README.md) after Rust changes.

## Configuration

- **Environment variable** `VFX_PLONK_PARAMS_PATH`: optional absolute path to a params file. Call `PLONKSetup.TryLoadParamsFromEnvironment()` during startup if you want zero config-file wiring.
- **Versioning**: when you rotate SRS/params, bump a constant or config value in one place and document the expected filename in release notes so nodes stay compatible.

## Operational checklist (when circuits exist)

1. Run trusted setup (or import a known SRS) in the **`plonk`** repo; export the blob your verifier expects.
2. Ship the file out-of-band or embed distribution policy (e.g. download URL + hash).
3. Ship real `plonk_verify` + proving keys from **`plonk`**; set the native **`PLONK_CAP_VERIFY_V1`** bit so [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) becomes true; optionally set [`Globals.EnforcePlonkProofsForZk`](../GlobalsPrivacy.cs) for strict ZK mempool rules.

See also [`Plonk/README.md`](README.md) for native library layout.
