# PLONK universal parameters (Phase 4 prep)

## Current behavior

- The native library exposes `plonk_load_params(path)`; [`PLONKSetup.TryLoadParamsFile`](../Privacy/PLONKSetup.cs) mirrors the file into [`Globals.PLONKUniversalParams`](../GlobalsPrivacy.cs) after a successful FFI load.
- **C# Phase 4**: [`PlonkProofVerifier`](../Privacy/PlonkProofVerifier.cs), [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs), and [`PrivateTransactionValidatorService`](../Privacy/PrivateTransactionValidatorService.cs) call `plonk_verify` when proofs are present. [`PLONKSetup.RefreshVerificationCapability`](../Privacy/PLONKSetup.cs) reads [`plonk_capabilities()`](../Privacy/PlonkNative.cs); [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) is true only when bit [`CapVerifyV1`](../Privacy/PlonkNative.cs) is set (full cryptographic verify). Until then, malformed public-input blobs can fail early; valid layouts still get `ErrNotImplemented` from the crypto step and are not enforced as invalid proofs.
- **Public inputs v1**: binary layout is built in [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs) (`VFXPI1` header + asset tag + Merkle root + scaled amounts + commitments / nullifiers). The **`plonk`** circuits must decode the same layout when replacing the stub.
- **Scaled amount width**: v1 writes amounts as **LE `u64`** after `×10^18` scaling (same bound as Pedersen path). That is safe for current fees and practical supplies; the long-term spec may allow ranges that need **`u128` / multi-word** public fields—bump the layout in C# and Rust together when circuits require it.
- **Params file formats**: **`VXPLNK01`** = SRS + verifier key + PI row (verify-only). **`VXPLNK02`** = same plus serialized **prover key** — required for **`plonk_prove_v0`** / [`CapProveV1`](../Privacy/PlonkNative.cs). Current **`vfx_plonk_setup`** output is **`VXPLNK02`**. Legacy **`VXPLNK01`** still loads for verification.
- **`plonk_verify` crypto**: load a params file from the sibling **`plonk`** repo (`cargo run -p verifiedx-circuits --bin vfx_plonk_setup`, see **`verifiedx-circuits/README.md`** there). That enables **v0** verification (SHA-256 digest binding to the full VFXPI1 blob — not Pedersen/Merkle/nullifier security yet). Without that file, layout validation runs, then **`ErrNotImplemented`** for the crypto step. Copy refreshed `plonk_ffi` binaries into [`Plonk/`](README.md) after Rust changes. Proving availability is reflected in [`PLONKSetup.IsProofProvingImplemented`](../Privacy/PLONKSetup.cs) when the native library reports prove capability.

## Configuration

- **Auto-download (default)**: on startup, [`PLONKParamsDownloader.EnsureParamsAvailableAsync`](../Privacy/PLONKParamsDownloader.cs) checks for a cached params file in `{DataPath}/PlonkParams/vfx_plonk_v1.params`. If missing, it downloads from [GitHub Releases](https://github.com/VerifiedXBlockchain/plonk/releases/download/v1/vfx_plonk_v1.params) (~253 MB, one-time), verifies the SHA-256 hash (`e4ea423e068eb949c5b1321908da1b263fd472700272e879058dc5f55166d85c`), and caches permanently. Progress is printed to console.
- **Environment variable** `VFX_PLONK_PARAMS_PATH`: optional absolute path override. When set and the file exists, the auto-download is skipped and this path is used directly. Useful for CI, air-gapped nodes, or custom param builds.
- **Versioning**: when you rotate SRS/params, bump the download URL, SHA-256 hash, and filename in [`PLONKParamsDownloader`](../Privacy/PLONKParamsDownloader.cs) and document the expected filename in release notes so nodes stay compatible.

## Operational checklist (when circuits exist)

1. Run trusted setup (or import a known SRS) in the **`plonk`** repo; export the blob your verifier expects.
2. Upload the params file to GitHub Releases (auto-download handles distribution).
3. Ship real `plonk_verify` + proving keys from **`plonk`**; set the native **`PLONK_CAP_VERIFY_V1`** bit so [`PLONKSetup.IsProofVerificationImplemented`](../Privacy/PLONKSetup.cs) becomes true; optionally set [`Globals.EnforcePlonkProofsForZk`](../GlobalsPrivacy.cs) for strict ZK mempool rules.

See also [`Plonk/README.md`](README.md) for native library layout.

**Runtime**: `GET privacyapi/PrivacyV1/GetPlonkStatus` returns whether verify/prove are implemented, native capability bits, and whether `VFX_PLONK_PARAMS_PATH` is set (boolean only).
