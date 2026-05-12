namespace VerfiedXCore.Tests
{
    /// <summary>Native plonk_ffi uses process-global Merkle state; run privacy tests sequentially.</summary>
    [CollectionDefinition("PrivacySequential", DisableParallelization = true)]
    public class PrivacySequentialCollection
    {
    }
}
