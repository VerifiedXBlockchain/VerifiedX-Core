# libplonk_ffi.so for linux-arm64

Drop the `aarch64-unknown-linux-gnu` build of `plonk_ffi` here as `libplonk_ffi.so`.

- Built by the plonk repository's **Build Native Libraries (plonk_ffi)** workflow, job `build-linux-arm64`,
  artifact `libplonk_ffi-linux-arm64`.
- `VerifiedXCore.csproj` copies this file (and not `Plonk/linux/libplonk_ffi.so`, which is x86-64) when publishing
  with `-r linux-arm64`, under the same output name the loader probes for.
- Until the file is present an arm64 publish carries no `libplonk_ffi.so`: the node fails closed on anything that
  needs the library (stage-2 proofs refuse, legacy stub checks report the library missing).

Verify the architecture before committing: `readelf -h libplonk_ffi.so | grep Machine` must say `AArch64`.
