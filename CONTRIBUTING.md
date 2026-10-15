# Contributing to VerifiedX Core

All changes go through pull requests. This repository is public: anything in a commit, PR, issue or code comment is visible to everyone.

## Branches

- `testnet` is the integration branch. Branch from it and open your PR against it.
- `main` is mainnet. It only receives release PRs from `testnet` (for example "Testnet to mainnet 8.1.0"). Never open a feature or fix PR against `main`.
- Name branches `fix/<topic>` or `feature/<topic>`.

## Before you open a PR

1. `dotnet build` succeeds with no new warnings.
2. `dotnet test` passes. CI runs it on Ubuntu and Windows for every PR into `testnet` or `main`.
3. The ship-gate tests pass. They guard against a release that loses wallet secrets or chain state, so run them for every change, however small:
   ```bash
   dotnet test --filter "FullyQualifiedName~SecretPreservation|FullyQualifiedName~StateSnapshot"
   ```
4. A bug fix comes with a test that fails before the fix and passes after it, plus a test that the legitimate behaviour still works.

On macOS the `FrostKeyBackupTests` that use `AesGcm` fail, because .NET 6 has no `AesGcm` on Apple platforms. They pass in CI; that is the only expected local failure.

## Changes that need extra care

- **Consensus, block validation and state application.** A rule change must activate at a block height so that nodes on older and newer builds agree on past blocks. Ask @Mathis1337 to review it.
- **Anything that moves funds or keys** (vBTC, FROST, bridge, privacy, wallet). Name the invariant the change keeps in the PR description.

## Commits

- One logical change per commit.
- The subject states the behaviour the change establishes, not the bug it removes: "one Bitcoin transaction pays one withdrawal", not "fix double-pay".
- Fixes for findings in the private issue tracker end with a trailer, one line per issue:
  ```
  Fixes-Issue: VerifiedX-Issues#23
  ```
  Do not use GitHub closing keywords (`Fixes #23`). Fixes land on `testnet`, so they would close the issue too early.
- Never describe how to exploit the old code, and never paste tracker content (traces, scenarios, proof tests) into commits, PRs or comments.

## Reporting bugs and vulnerabilities

Never report a security vulnerability in a public issue. Contact a maintainer privately. For other bugs, open an issue with steps to reproduce, what you expected, what happened, and the node version and network (mainnet or testnet).

## License

Contributions are made under the [MIT License](LICENSE) that covers the project.
