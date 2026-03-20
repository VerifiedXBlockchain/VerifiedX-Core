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
        private static int _proveProbe = -1;

        /// <summary>Environment variable pointing at a universal-params file (optional until Phase 4).</summary>
        public const string ParamsPathEnvironmentVariable = "VFX_PLONK_PARAMS_PATH";

        /// <summary>
        /// Reads <see cref="PlonkNative.plonk_capabilities"/>. Bit <see cref="PlonkNative.CapVerifyV1"/> means
        /// non-stub PLONK verification (SRS + circuits) is available — not merely public-input parsing.
        /// Older <c>plonk_ffi</c> builds without <c>plonk_capabilities</c> fall back to “not implemented.”
        /// </summary>
        public static void RefreshVerificationCapability()
        {
            try
            {
                var caps = PlonkNative.plonk_capabilities();
                _verificationProbe = (caps & PlonkNative.CapVerifyV1) != 0 ? 1 : 0;
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

        /// <summary>
        /// Whether v0 native proving (<see cref="PlonkNative.plonk_prove_v0"/>) is available — <b>VXPLNK02</b> params with prover key loaded.
        /// </summary>
        public static bool IsProofProvingImplemented => _proveProbe == 1;
    }
}
