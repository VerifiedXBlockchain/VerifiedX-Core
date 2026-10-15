# VerifiedXCore.Tests: guide

xUnit 2.4 with Moq and `Microsoft.AspNetCore.TestHost`, targeting net6.0. The project references `../VerifiedXCore` directly, so tests can reach `Globals`, the static `DbContext` handles and internal services.

## The suite runs serially

`AssemblyParallelization.cs` turns off parallelism across the whole assembly. Many classes repoint the same static LiteDB handles and `Globals` fields, and running them side by side caused "Database lock timeout" failures. Do not turn parallelism back on.

Put a class that touches shared static state in the matching collection:

| Collection | Use it when the class |
|---|---|
| `DbContextSequential` | opens, closes or repoints `DbContext` (most DB-backed tests) |
| `GlobalCasterState` | reads or changes caster membership or other caster globals |
| `PrivacySequential` | uses the shielded pool or PLONK state |

## Isolating the database

A DB-backed test points `Globals.CustomPath` at a fresh temp folder and puts it back afterwards. Copy the pattern in `BridgeStateWipeTests.cs`:

```csharp
[Collection("DbContextSequential")]
public class MyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _priorCustomPath;

    public MyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"my_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_tempRoot);
        _priorCustomPath = Globals.CustomPath;
        Globals.CustomPath = _tempRoot;
        DbContext.Initialize();
    }

    public void Dispose()
    {
        DbContext.CloseDB();
        Globals.CustomPath = _priorCustomPath;
        Directory.Delete(_tempRoot, recursive: true);
    }
}
```

A test that changes any other `Globals` field (an activation height, `IsTestNet`, caster state) saves the old value and restores it in `finally` or `Dispose`. A leaked global makes a later, unrelated test fail.

## Height-gated rules

Test both sides of the height: a block or transaction below the activation height keeps the old behaviour, and one at or above it gets the new rule. Set the height on `Globals` inside the test and restore it afterwards.

## Naming

- One file per behaviour, named for what it guards: `BridgeIntraBlockGuardTests.cs`, `CasterPromotionDeterminismTests.cs`.
- Prefixes such as `FundLoss08_`, `VX17_`, `BB4_`, `Wave3_` tie a file to an audit finding or remediation round. Keep an existing prefix when you extend that work.
- Name test methods for the rule they check, e.g. `BlockBelowActivationHeight_KeepsLegacyOrder`.

## Gates and known failures

- Ship gate, run before every PR: `dotnet test --filter "FullyQualifiedName~SecretPreservation|FullyQualifiedName~StateSnapshot"` (16 tests).
- On macOS some `FrostKeyBackupTests` fail because .NET 6 has no `AesGcm` on Apple platforms. CI on Ubuntu and Windows is the judge.
- Proof tests for findings in the private tracker stay in that tracker. Never copy them here; write Core's own regression test for the fixed behaviour instead.
