# FROST Native Library Deployment

## Overview

This directory contains platform-specific native libraries for FROST (Flexible Round-Optimized Schnorr Threshold Signatures) used by VerifiedX-Core for vBTC V2 MPC operations.

## Directory Structure

```
Frost/
├── win/
│   └── frost_ffi.dll          # Windows x64 native library
├── linux/
│   └── libfrost_ffi.so        # Linux x64 native library (future)
└── mac/
    └── libfrost_ffi.dylib     # macOS x64 native library (future)
```

## Build Configuration

The `ReserveBlockCore.csproj` is configured to:

1. **Development Builds (Debug/Release)**:
   - Copy platform-specific DLLs to the output directory (`bin/Debug/net6.0/` or `bin/Release/net6.0/`)
   - Uses `<Link>` to flatten the directory structure (DLL appears at root of output)

2. **Published Builds**:
   - Include DLLs in the publish output via `CopyToPublishDirectory`
   - DLLs are automatically included when running `dotnet publish`

## Usage

### Development

Simply build the project:
```bash
dotnet build
```

The appropriate DLL will be copied to the output directory and loaded at runtime via P/Invoke in `FrostNative.cs`.

### Release/Publish

For platform-specific releases:

**Windows:**
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

**Linux (when available):**
```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

**macOS (when available):**
```bash
dotnet publish -c Release -r osx-x64 --self-contained
```

## Adding New Platform Libraries

When Linux and macOS libraries become available:

1. **Build the Rust library** for the target platform:
   ```bash
   # Linux
   cd C:\Users\Aaron\Documents\GitHub\frost\frost-ffi
   cargo build --release --target x86_64-unknown-linux-gnu
   
   # macOS
   cargo build --release --target x86_64-apple-darwin
   ```

2. **Copy the compiled library** to the appropriate directory:
   - Linux: Copy `target/x86_64-unknown-linux-gnu/release/libfrost_ffi.so` to `Frost/linux/`
   - macOS: Copy `target/x86_64-apple-darwin/release/libfrost_ffi.dylib` to `Frost/mac/`

3. **The .csproj is already configured** to include these files when they exist (via `Condition="Exists(...)"`)

## Platform Detection

The .NET runtime automatically loads the correct native library based on:
- Library naming conventions (`.dll`, `.so`, `.dylib`)
- Current operating system
- No explicit platform detection needed in C# code

## DllImport Configuration

In `FrostNative.cs`:
```csharp
private const string DllName = "frost_ffi";
```

This works across platforms because:
- Windows: Looks for `frost_ffi.dll`
- Linux: Looks for `libfrost_ffi.so`
- macOS: Looks for `libfrost_ffi.dylib`

The `lib` prefix and file extension are automatically handled by the P/Invoke system.

## Source Location

The Rust source code for the FROST FFI wrapper is located at:
```
C:\Users\Aaron\Documents\GitHub\frost\frost-ffi\
```

See `frost-ffi/README.md` (if exists) for building instructions.

## Troubleshooting

### DLL Not Found Error

If you encounter a `DllNotFoundException`:

1. **Verify the DLL exists** in the output directory
2. **Check the DLL was copied** from `Frost/win/frost_ffi.dll`
3. **Rebuild the project** to force copy: `dotnet clean && dotnet build`

### Cross-Platform Issues

- Ensure you're using the correct runtime identifier (`-r` flag) when publishing
- Verify the native library exists for the target platform
- Check that the library was built for the correct architecture (x64 vs ARM)

## Version Information

- **FROST Implementation**: ZCash Foundation frost-secp256k1-tr
- **FFI Version**: 0.1.0 (placeholder mode)
- **Target Framework**: .NET 6.0
- **Supported Platforms**: Windows x64 (Linux/macOS coming soon)

---

**Last Updated**: January 7, 2026  
**Maintainer**: VerifiedX Team
