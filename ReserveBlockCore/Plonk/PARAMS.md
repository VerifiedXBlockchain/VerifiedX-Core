# PLONK universal parameters (Phase 4 prep)

## Current behavior (Phase 1)

- The native library exposes `plonk_load_params(path)`; [`PLONKSetup.TryLoadParamsFile`](../Privacy/PLONKSetup.cs) mirrors the file into [`Globals.PLONKUniversalParams`](../GlobalsPrivacy.cs) after a successful FFI load.
- **`plonk_verify` is still a stub** until circuits and a real verifier are wired in the `plonk` workspace.

## Configuration

- **Environment variable** `VFX_PLONK_PARAMS_PATH`: optional absolute path to a params file. Call `PLONKSetup.TryLoadParamsFromEnvironment()` during startup if you want zero config-file wiring.
- **Versioning**: when you rotate SRS/params, bump a constant or config value in one place and document the expected filename in release notes so nodes stay compatible.

## Operational checklist (when circuits exist)

1. Run trusted setup (or import a known SRS) in the **`plonk`** repo; export the blob your verifier expects.
2. Ship the file out-of-band or embed distribution policy (e.g. download URL + hash).
3. Replace `PlonkNative.plonk_verify` / `PLONKSetup.IsProofVerificationImplemented` with real verification and add consensus rules for required params version.

See also [`Plonk/README.md`](README.md) for native library layout.
