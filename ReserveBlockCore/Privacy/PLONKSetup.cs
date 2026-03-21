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
        private static int _v1CircuitsProbe = -1;
        private static int _v1ProveProbe = -1;

        /// <summary>Environment variable pointing at a universal-params file (optional until Phase 4).</summary>
        public const string ParamsPathEnvironmentVariable = "VFX_PLONK_PARAMS_PATH";

        /// <summary>
        /// Refreshes <see cref="IsProofVerificationImplemented"/> and <see cref="IsProofProvingImplemented"/> from <see cref="PlonkNative.plonk_capabilities"/>.
        /// Bit <see cref="PlonkNative.CapVerifyV1"/> means non-stub PLONK verification (SRS + circuits) is available.
        /// </summary>
        public static void RefreshVerificationCapability()
        {
            try
            {
                var caps = PlonkNative.plonk_capabilities();
                _verificationProbe = (caps & PlonkNative.CapVerifyV1) != 0 ? 1 : 0;
                _proveProbe = (caps & PlonkNative.CapProveV1) != 0 ? 1 : 0;
                _v1CircuitsProbe = (caps & PlonkNative.CapV1Circuits) != 0 ? 1 : 0;
                _v1ProveProbe = (caps & PlonkNative.CapV1Prove) != 0 ? 1 : 0;
            }
            catch
            {
                _verificationProbe = 0;
                _proveProbe = 0;
                _v1CircuitsProbe = 0;
                _v1ProveProbe = 0;
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
        /// If <paramref name="paramsPath"/> exists, loads params via native FFI and records file size in <see cref="Globals.PLONKParamsFileSize"/>.
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
                // Store file size for diagnostics; native FFI holds the actual data.
                Globals.PLONKParamsFileSize = new FileInfo(paramsPath).Length;
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

        /// <summary>
        /// Whether v1 real circuit verification keys are loaded (<b>VXPLNK03</b>). When true, <see cref="PlonkNative.plonk_verify"/>
        /// dispatches to real shield/transfer/unshield/fee circuit verifiers instead of the v0 digest-binding stub.
        /// </summary>
        public static bool IsV1CircuitsLoaded => _v1CircuitsProbe == 1;

        /// <summary>
        /// Whether v1 real circuit prover keys are loaded (<b>VXPLNK03</b> with prover keys).
        /// When true, <see cref="PlonkProverV1"/> can generate real shield/transfer/unshield/fee proofs.
        /// </summary>
        public static bool IsV1ProvingAvailable => _v1ProveProbe == 1;
    }
}
