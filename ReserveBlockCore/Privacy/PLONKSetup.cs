using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Phase 1 loads the FFI and optional params file only. Full PLONK trusted setup / universal parameters
    /// used by real circuits are integrated in Phase 4; <see cref="PlonkNative.plonk_verify"/> remains a stub until then.
    /// </summary>
    public static class PLONKSetup
    {
        /// <summary>
        /// If <paramref name="paramsPath"/> exists, loads bytes into <see cref="Globals.PLonKUniversalParams"/> (memory only).
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
        /// Whether native PLONK verification is wired to circuits (Phase 4).
        /// </summary>
        public static bool IsProofVerificationImplemented => false;
    }
}
