namespace ReserveBlockCore.Privacy
{
    public enum PlonkVerifyResult
    {
        /// <summary>Native returned valid (1).</summary>
        Valid = 0,
        /// <summary>Native returned invalid proof (0).</summary>
        Invalid = 1,
        /// <summary><see cref="PlonkNative.ErrNotImplemented"/> — replace <c>plonk_ffi</c> when circuits ship.</summary>
        NotImplemented = 2,
        /// <summary>Parameter / FFI error (negative codes other than stub).</summary>
        NativeError = 3
    }
}
