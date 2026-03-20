# PLONK / privacy native library (`plonk_ffi`)

Same deployment pattern as **`Frost/`**: platform-specific binaries live under `win/`, `linux/`, and `mac/`, and `ReserveBlockCore.csproj` copies them to the output directory with a flat `<Link>` name so `DllImport("plonk_ffi")` resolves.

## Source (Rust)

The FFI crate lives in the **`plonk`** workspace (sibling repo), not inside VerifiedX-Core:

- `plonk/plonk-ffi/` — build + copy paths in `README.md`
- `plonk/plonk-ffi/DEPLOYMENT.md` — step-by-step placement into this `Plonk/` folder
- [`PARAMS.md`](PARAMS.md) — universal parameters file + `VFX_PLONK_PARAMS_PATH` (Phase 4 prep)

## How binaries get here

In the **plonk** repo: Actions → **Build Native Libraries (plonk_ffi)** (manual run) builds **all three** platforms and uploads artifacts:

| Platform | Artifact | Copy to |
|----------|----------|---------|
| **Windows** | `plonk_ffi-windows` (`plonk_ffi.dll`) | `Plonk/win/plonk_ffi.dll` |
| **Linux** | `libplonk_ffi-linux` | `Plonk/linux/libplonk_ffi.so` |
| **macOS** | `libplonk_ffi-macos` | `Plonk/mac/libplonk_ffi.dylib` |

FROST’s workflow only produces macOS/Linux; **plonk** adds **`windows-latest`** so you can pull the DLL from CI as well. Local `cargo build` still works if you prefer.

## Layout

```
Plonk/
├── win/
│   └── plonk_ffi.dll
├── linux/
│   └── libplonk_ffi.so
└── mac/
    └── libplonk_ffi.dylib
```

## CI

[`.github/workflows/dotnet.yml`](../../.github/workflows/dotnet.yml) runs **build + test** on **Windows** and **Ubuntu** so `plonk_ffi.dll` and `libplonk_ffi.so` are both exercised (same pattern as optional Frost linux `.so`).

## Roadmap (beyond Phase 1 FFI)

The committed library exposes **Pedersen**, **Poseidon** (plonk-hashing–aligned), **Merkle**, **nullifiers**, and **`plonk_load_params` / `plonk_verify`**. **C# Phase 4** wires [`PlonkProofVerifier`](../Privacy/PlonkProofVerifier.cs) + [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs) into validation; the Rust **`plonk_verify` body** is still a stub until the sibling **`plonk`** repo ships circuits and refreshed binaries are dropped here.

1. **Parameters** — proving/verification keys generated in **`plonk`**, loaded via `plonk_load_params` (see [`PARAMS.md`](PARAMS.md)).
2. **Circuits** — implement Transfer / Shield / Unshield / Fee in **`plonk`**; align public-input encoding with [`PlonkPublicInputsV1`](../Privacy/PlonkPublicInputsV1.cs).
3. **`plonk_verify`** — implement in **`plonk`**; no C# signature change required. [`PLONKSetup.RefreshVerificationCapability`](../Privacy/PLONKSetup.cs) will detect non-stub behavior.
4. **Optional FFI** — `plonk_prove_*` / `plonk_batch_verify` from the plan can be added to [`PlonkNative.cs`](../Privacy/PlonkNative.cs) when exported.

See `Privacy_Layer_Implementation_Plan.md` for phased detail.
