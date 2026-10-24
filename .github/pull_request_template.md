## What this changes

<!-- The behaviour this PR establishes, and why. Public repo: no exploit details or tracker content. -->

## Risk

- [ ] Consensus, block validation or state application (activation height: ____ ; needs @Mathis1337's review)
- [ ] Moves funds or keys (vBTC, FROST, bridge, privacy, wallet): invariant kept: ____
- [ ] None of the above

## Testing

- [ ] `dotnet test` passes
- [ ] Ship-gate tests pass (`--filter "FullyQualifiedName~SecretPreservation|FullyQualifiedName~StateSnapshot"`)
- [ ] New tests cover the fix and the legitimate behaviour it keeps
- [ ] Tried on a running testnet node (say how):

## Tracker

<!-- Fixes-Issue trailers are in the commits. List the issues here too, e.g. VerifiedX-Issues#23. Never "Fixes #N". -->
