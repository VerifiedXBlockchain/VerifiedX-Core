using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Phase 1 loads the FFI and optional params file only. Full PLONK trusted setup / universal parameters
    /// used by real circuits are integrated in Phase 4; <see cref="PlonkNative.plonk_verify"/> remains a stub until then.
    /// </summary>
    public static class PLONKSetup
    {
        private static int _verificationProbe = -1;

        /// <summary>Environment variable pointing at a universal-params file (optional until Phase 4).</summary>
        public const string ParamsPathEnvironmentVariable = "VFX_PLONK_PARAMS_PATH";

        /// <summary>
        /// Probes <see cref="PlonkNative.plonk_verify"/> once. If it returns <see cref="PlonkNative.ErrNotImplemented"/>, native circuits are not linked yet.
        /// </summary>
        public static void RefreshVerificationCapability()
        {
            try
            {
                var code = PlonkNative.plonk_verify(0, new byte[] { 0xAB }, 1, new byte[] { 0x01 }, 1);
                _verificationProbe = code == PlonkNative.ErrNotImplemented ? 0 : 1;
            }
            catch
            {
                _verificationProbe = 0;
            }
        }

        /// <summary>
        /// Loads params from <see cref="ParamsPathEnvironmentVariable"/> when set and the file exists.
        /// </summary>
        public static bool TryLoadParamsFromEnvironment()
        {
            var path = Environment.GetEnvironmentVariable(ParamsPathEnvironmentVariable);
            return !string.IsNullOrWhiteSpace(path) && TryLoadParamsFile(path);
        }

        /// <summary>
        /// If <paramref name="paramsPath"/> exists, loads bytes into <see cref="Globals.PLONKUniversalParams"/> (memory only).
        /// </summary>
        public static bool TryLoadParamsFile(string paramsPath)
        {
            if (string.IsNullOrWhiteSpace(paramsPath) || !File.Exists(paramsPath))
                return false;
            try
            {
                var code = PlonkNative.plonk_load_params(paramsPath);
                if (code != PlonkNative.Success)
                    return false;
                Globals.PLONKUniversalParams = File.ReadAllBytes(paramsPath);
                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "PLONKSetup.TryLoadParamsFile()");
                return false;
            }
        }

        /// <summary>
        /// Whether native PLONK verification is wired to circuits (non-stub <c>plonk_verify</c>). Call <see cref="RefreshVerificationCapability"/> at startup.
        /// </summary>
        public static bool IsProofVerificationImplemented => _verificationProbe == 1;
    }
}
