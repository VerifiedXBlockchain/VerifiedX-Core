namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Must match <c>plonk_ffi</c> / circuit pack (<c>plonk_verify</c> circuit-type byte) and the privacy plan FFI spec.
    /// </summary>
    public enum PlonkCircuitType : byte
    {
        Transfer = 0,
        Shield = 1,
        Unshield = 2,
        Fee = 3
    }
}
