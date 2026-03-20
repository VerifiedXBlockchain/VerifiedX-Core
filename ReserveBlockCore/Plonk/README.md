# PLONK / privacy native library (`plonk_ffi`)

Same deployment pattern as **`Frost/`**: platform-specific binaries live under `win/`, `linux/`, and `mac/`, and `ReserveBlockCore.csproj` copies them to the output directory with a flat `<Link>` name so `DllImport("plonk_ffi")` resolves.

## Source (Rust)

The FFI crate lives in the **`plonk`** workspace (sibling repo), not inside VerifiedX-Core:

- `plonk/plonk-ffi/` — build + copy paths in `README.md`
- `plonk/plonk-ffi/DEPLOYMENT.md` — step-by-step placement into this `Plonk/` folder

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

The committed library exposes **Pedersen**, **Poseidon** (plonk-hashing–aligned), **Merkle**, **nullifiers**, and **`plonk_load_params` / `plonk_verify`** (verify still a stub in Rust). Next work is mostly **product + ZK**, not more FFI packaging:

1. **Parameters** — define how proving/verification keys are generated, stored, and loaded end-to-end (`plonk_load_params` and callers).
2. **Circuits** — implement shielded / transfer constraints in the `plonk` workspace; keep FFI hash/Pedersen semantics aligned with in-circuit gadgets.
3. **`plonk_verify`** — replace the stub with real verifier wiring in [`PlonkNative.cs`](../Privacy/PlonkNative.cs) and consensus/block validation when shielded transactions exist.
4. **Consensus integration** — connect shielded state (commitments, nullifiers, roots) to block and mempool rules.

See also any in-repo privacy design doc (e.g. `Privacy_Layer_Implementation_Plan.md`) for phased detail.
