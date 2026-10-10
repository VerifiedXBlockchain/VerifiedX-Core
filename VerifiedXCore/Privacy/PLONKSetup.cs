using VerifiedXCore.Utilities;

namespace VerifiedXCore.Privacy
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
        private static int _vfxPi2Probe = -1;
        private static int _v2CircuitsProbe = -1;

        /// <summary>Whether the loaded library carries the v2 owner-bound circuits (<see cref="PlonkNative.CapV2Circuits"/>, VXPLNK05). The proof-rules epoch requires it.</summary>
        public static bool IsV2CircuitsAvailable => _v2CircuitsProbe == 1;

        /// <summary>
        /// Whether the loaded library verifies v1 proofs against VFXPI1 version-2 public inputs (<see cref="PlonkNative.CapVfxPi2Verify"/>).
        /// Consensus from PrivateTxProofRulesHeight requires this; a node without it refuses private transactions (fail closed).
        /// </summary>
        public static bool IsVfxPi2VerifyAvailable => _vfxPi2Probe == 1;

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
                _vfxPi2Probe = (caps & PlonkNative.CapVfxPi2Verify) != 0 ? 1 : 0;
                _v2CircuitsProbe = (caps & PlonkNative.CapV2Circuits) != 0 ? 1 : 0;
            }
            catch
            {
                _verificationProbe = 0;
                _proveProbe = 0;
                _v1CircuitsProbe = 0;
                _v1ProveProbe = 0;
                _vfxPi2Probe = 0;
                _v2CircuitsProbe = 0;
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
        /// THE load point: every path that hands a params file to the native library comes through here, and here the file
        /// must be the published one (<see cref="PLONKParamsDownloader.ExpectedSha256"/>). Proofs made or verified under
        /// any other universal parameters agree with nothing the network uses. (Fourth review: the hash check had been
        /// added to a method nothing in production calls, so an operator-supplied file still loaded unpinned; enforcing
        /// it at the one place the library is called leaves no caller that can skip it.)
        /// Records the file size in <see cref="Globals.PLONKParamsFileSize"/>.
        /// </summary>
        public static bool TryLoadParamsFile(string paramsPath)
        {
            if (string.IsNullOrWhiteSpace(paramsPath) || !File.Exists(paramsPath))
                return false;
            try
            {
                if (!PLONKParamsDownloader.VerifyFileHash(paramsPath))
                {
                    var why = $"PLONK params at {paramsPath} do not match the pinned SHA-256 ({PLONKParamsDownloader.ExpectedSha256}); not loaded.";
                    ErrorLogUtility.LogError(why, "PLONKSetup.TryLoadParamsFile()");
                    Console.WriteLine($"PLONKSetup: {why}");
                    return false;
                }
                Console.WriteLine($"PLONKSetup: Loading params from {paramsPath}");
                var code = PlonkNative.plonk_load_params(paramsPath);
                if (code != PlonkNative.Success)
                    return false;

                Console.WriteLine("PLONKSetup: Params loaded successfully");
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
        /// Makes the params available (cached file, else download; both verified) and loads them, retrying until it
        /// succeeds: a node that cannot verify refuses every private transaction in the proof-rules epoch, and with it the
        /// blocks that carry one, so one failed download at startup must not leave it that way until the next restart.
        /// Returns true once loaded; false only when cancelled or out of attempts.
        /// </summary>
        public static async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken = default, int maxAttempts = int.MaxValue)
        {
            for (var attempt = 1; attempt <= maxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                var loaded = false;
                try
                {
                    var path = await PLONKParamsDownloader.EnsureParamsAvailableAsync().ConfigureAwait(false);
                    loaded = !string.IsNullOrEmpty(path) && TryLoadParamsFile(path!);
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"PLONK params load attempt {attempt} failed: {ex}", "PLONKSetup.EnsureLoadedAsync()");
                }
                RefreshVerificationCapability();
                if (loaded)
                {
                    LogUtility.Log($"PLONK params loaded (attempt {attempt}); v2 circuits available: {IsV2CircuitsAvailable}, proving: {IsV1ProvingAvailable}.", "PLONKSetup.EnsureLoadedAsync()");
                    return true;
                }
                if (attempt >= maxAttempts)
                    break;
                var wait = TimeSpan.FromMinutes(Math.Min(attempt, 10));
                ErrorLogUtility.LogError($"PLONK params are not loaded (attempt {attempt}); private transactions are refused until they are. Retrying in {wait.TotalMinutes:0} min.", "PLONKSetup.EnsureLoadedAsync()");
                try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
            return false;
        }

        /// <summary>
        /// Whether native PLONK verification is available (v0 <c>CapVerifyV1</c> OR v1 <c>CapV1Circuits</c>).
        /// Call <see cref="RefreshVerificationCapability"/> at startup.
        /// </summary>
        public static bool IsProofVerificationImplemented => _verificationProbe == 1 || _v1CircuitsProbe == 1;

        /// <summary>
        /// Whether native PLONK proving is available (v0 <c>CapProveV1</c> OR v1 <c>CapV1Prove</c>).
        /// </summary>
        public static bool IsProofProvingImplemented => _proveProbe == 1 || _v1ProveProbe == 1;

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
