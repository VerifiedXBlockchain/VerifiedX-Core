using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Bitcoin.FROST;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// FROST FFI startup health check utility.
    /// Verifies that the native FROST library is present in the correct location
    /// and that ALL FFI functions work correctly by running a complete mini
    /// 2-of-3 DKG ceremony + signing round with test data.
    /// </summary>
    public static class FrostFFICheckUtility
    {
        #region Result Model

        public class FrostFFICheckResult
        {
            public bool LibraryFilePresent { get; set; }
            public string LibraryFilePath { get; set; } = "";
            public long LibraryFileSize { get; set; }
            public string LibraryVersion { get; set; } = "Unknown";
            public bool VersionCheckPassed { get; set; }
            public bool DKGRound1Passed { get; set; }
            public bool DKGRound2Passed { get; set; }
            public bool DKGRound3Passed { get; set; }
            public bool SignRound1Passed { get; set; }
            public bool SignRound2Passed { get; set; }
            public bool SignAggregatePassed { get; set; }
            public List<string> Errors { get; set; } = new List<string>();

            public bool AllChecksPassed =>
                LibraryFilePresent &&
                VersionCheckPassed &&
                DKGRound1Passed &&
                DKGRound2Passed &&
                DKGRound3Passed &&
                SignRound1Passed &&
                SignRound2Passed &&
                SignAggregatePassed;

            public string Summary
            {
                get
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("=== FROST FFI Health Check ===");
                    sb.AppendLine($"  Library File Present:   {(LibraryFilePresent ? "PASS" : "FAIL")}");
                    if (LibraryFilePresent)
                    {
                        sb.AppendLine($"    Path: {LibraryFilePath}");
                        sb.AppendLine($"    Size: {LibraryFileSize:N0} bytes");
                    }
                    sb.AppendLine($"  Version Check:          {(VersionCheckPassed ? $"PASS (v{LibraryVersion})" : "FAIL")}");
                    sb.AppendLine($"  DKG Round 1 (Generate): {(DKGRound1Passed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  DKG Round 2 (Shares):   {(DKGRound2Passed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  DKG Round 3 (Finalize): {(DKGRound3Passed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  Sign Round 1 (Nonces):  {(SignRound1Passed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  Sign Round 2 (Share):   {(SignRound2Passed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  Sign Aggregate:         {(SignAggregatePassed ? "PASS" : "FAIL")}");
                    sb.AppendLine($"  Overall:                {(AllChecksPassed ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED")}");
                    if (Errors.Count > 0)
                    {
                        sb.AppendLine("  Errors:");
                        foreach (var err in Errors)
                            sb.AppendLine($"    - {err}");
                    }
                    sb.AppendLine("=== End FROST FFI Health Check ===");
                    return sb.ToString();
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Runs the complete FROST FFI health check: file presence + functional verification
        /// of all 8 native functions via a mini 2-of-3 DKG ceremony and signing round.
        /// Safe to call from any thread. No side effects on application state.
        /// </summary>
        public static FrostFFICheckResult RunFullCheck()
        {
            var result = new FrostFFICheckResult();

            // Phase 1: File presence check
            CheckLibraryFilePresence(result);

            if (!result.LibraryFilePresent)
            {
                // If the file isn't even present, no point trying the functional checks
                result.Errors.Add("Native library file not found — skipping functional checks.");
                return result;
            }

            // Phase 2: Functional verification
            CheckVersion(result);
            RunFullDKGAndSigningTest(result);

            return result;
        }

        /// <summary>
        /// Returns a compact one-line status string suitable for debug output.
        /// </summary>
        public static string GetCompactStatus()
        {
            return $"FROST FFI Available: {Globals.FrostFFIAvailable} | Version: {Globals.FrostFFIVersion}";
        }

        #endregion

        #region Phase 1: File Presence

        private static void CheckLibraryFilePresence(FrostFFICheckResult result)
        {
            try
            {
                string expectedFileName;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    expectedFileName = "frost_ffi.dll";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    expectedFileName = "libfrost_ffi.so";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    expectedFileName = "libfrost_ffi.dylib";
                else
                {
                    result.Errors.Add($"Unsupported OS platform: {RuntimeInformation.OSDescription}");
                    return;
                }

                // Check in the application's base directory (where the executable runs)
                var baseDir = AppContext.BaseDirectory;
                var candidatePaths = new List<string>
                {
                    Path.Combine(baseDir, expectedFileName),
                    Path.Combine(Directory.GetCurrentDirectory(), expectedFileName),
                };

                // Also check the Frost platform subdirectories (source layout)
                var frostDir = Path.Combine(baseDir, "Frost");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    candidatePaths.Add(Path.Combine(frostDir, "win", expectedFileName));
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    candidatePaths.Add(Path.Combine(frostDir, "linux", expectedFileName));
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    candidatePaths.Add(Path.Combine(frostDir, "mac", expectedFileName));

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        var fileInfo = new FileInfo(path);
                        result.LibraryFilePresent = true;
                        result.LibraryFilePath = fileInfo.FullName;
                        result.LibraryFileSize = fileInfo.Length;
                        return;
                    }
                }

                result.LibraryFilePresent = false;
                result.Errors.Add($"FROST native library '{expectedFileName}' not found. Searched: {string.Join(", ", candidatePaths)}");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"File presence check error: {ex.Message}");
            }
        }

        #endregion

        #region Phase 2: Functional Verification

        private static void CheckVersion(FrostFFICheckResult result)
        {
            try
            {
                var version = FrostNative.GetVersion();
                if (!string.IsNullOrEmpty(version) && version != "Unknown" && version != "Error")
                {
                    result.VersionCheckPassed = true;
                    result.LibraryVersion = version;
                }
                else
                {
                    result.Errors.Add($"frost_get_version returned: '{version}'");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Version check error: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a complete mini 2-of-3 DKG ceremony followed by a 2-of-3 signing round.
        /// This exercises every FROST FFI function end-to-end with no side effects.
        /// 
        /// We simulate 3 participants locally:
        ///   - DKG Round 1: All 3 generate commitments
        ///   - DKG Round 2: All 3 generate shares for each other
        ///   - DKG Round 3: All 3 finalize and derive the group public key
        ///   - Sign Round 1: 2 participants generate nonce commitments
        ///   - Sign Round 2: 2 participants generate signature shares
        ///   - Aggregate:    Combine shares into final Schnorr signature
        /// </summary>
        private static void RunFullDKGAndSigningTest(FrostFFICheckResult result)
        {
            const ushort MAX_SIGNERS = 3;
            const ushort MIN_SIGNERS = 2;

            // === DKG ROUND 1 ===
            // Generate commitments and secret packages for all 3 participants
            var round1Commitments = new string[MAX_SIGNERS];
            var round1Secrets = new string[MAX_SIGNERS];

            try
            {
                for (ushort i = 0; i < MAX_SIGNERS; i++)
                {
                    ushort participantId = (ushort)(i + 1); // FROST uses 1-based IDs
                    var (commitment, secretPackage, errorCode) = FrostNative.DKGRound1Generate(
                        participantId, MAX_SIGNERS, MIN_SIGNERS);

                    if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(commitment) || string.IsNullOrEmpty(secretPackage))
                    {
                        result.Errors.Add($"DKG Round 1 failed for participant {participantId}: error code {errorCode}");
                        return;
                    }

                    round1Commitments[i] = commitment;
                    round1Secrets[i] = secretPackage;
                }

                result.DKGRound1Passed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"DKG Round 1 exception: {ex.Message}");
                return;
            }

            // === DKG ROUND 2 ===
            // Each participant generates shares for all others using the collected commitments.
            // FROST expects a BTreeMap<Identifier, round1::Package> excluding self.
            var round2SharesAll = new string[MAX_SIGNERS];
            var round2Secrets = new string[MAX_SIGNERS];

            try
            {
                // Build FROST Identifier map (same logic as FrostStartup.BuildAddressToIdentifierMap)
                // Identifier for participant i (1-based) = i as 32-byte big-endian scalar, hex-encoded
                var identifiers = new string[MAX_SIGNERS];
                for (int i = 0; i < MAX_SIGNERS; i++)
                {
                    identifiers[i] = BuildFrostIdentifier((ushort)(i + 1));
                }

                for (int participant = 0; participant < MAX_SIGNERS; participant++)
                {
                    // Build BTreeMap of OTHER participants' commitments
                    var commitmentMap = new JObject();
                    for (int other = 0; other < MAX_SIGNERS; other++)
                    {
                        if (other == participant) continue; // Skip self
                        var packageJson = JToken.Parse(round1Commitments[other]);
                        commitmentMap[identifiers[other]] = packageJson;
                    }

                    var commitmentsJson = commitmentMap.ToString(Newtonsoft.Json.Formatting.None);

                    var (sharesJson, round2Secret, errorCode) = FrostNative.DKGRound2GenerateShares(
                        round1Secrets[participant], commitmentsJson);

                    if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(sharesJson) || string.IsNullOrEmpty(round2Secret))
                    {
                        result.Errors.Add($"DKG Round 2 failed for participant {participant + 1}: error code {errorCode}");
                        return;
                    }

                    round2SharesAll[participant] = sharesJson;
                    round2Secrets[participant] = round2Secret;
                }

                result.DKGRound2Passed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"DKG Round 2 exception: {ex.Message}");
                return;
            }

            // === DKG ROUND 3 ===
            // Each participant finalizes using:
            //   - Their round2 secret package
            //   - All round1 packages (excluding self)
            //   - The round2 packages meant for them (from each other participant)
            var keyPackages = new string[MAX_SIGNERS];
            var pubkeyPackages = new string[MAX_SIGNERS];
            string groupPubkey = "";

            try
            {
                var identifiers = new string[MAX_SIGNERS];
                for (int i = 0; i < MAX_SIGNERS; i++)
                    identifiers[i] = BuildFrostIdentifier((ushort)(i + 1));

                for (int participant = 0; participant < MAX_SIGNERS; participant++)
                {
                    // Build round1 packages map (others only)
                    var round1Map = new JObject();
                    for (int other = 0; other < MAX_SIGNERS; other++)
                    {
                        if (other == participant) continue;
                        round1Map[identifiers[other]] = JToken.Parse(round1Commitments[other]);
                    }

                    // Build round2 packages map: from each OTHER participant, extract the share meant for this participant
                    var round2Map = new JObject();
                    for (int sender = 0; sender < MAX_SIGNERS; sender++)
                    {
                        if (sender == participant) continue;
                        // Parse sender's generated shares (BTreeMap<Identifier, round2::Package>)
                        var senderShares = JObject.Parse(round2SharesAll[sender]);
                        // Extract the share for this participant by their identifier
                        var shareForMe = senderShares[identifiers[participant]];
                        if (shareForMe != null)
                        {
                            round2Map[identifiers[sender]] = shareForMe;
                        }
                        else
                        {
                            result.Errors.Add($"DKG Round 3: No share found for participant {participant + 1} from sender {sender + 1}");
                            return;
                        }
                    }

                    var round1PackagesJson = round1Map.ToString(Newtonsoft.Json.Formatting.None);
                    var round2PackagesJson = round2Map.ToString(Newtonsoft.Json.Formatting.None);

                    var (gpk, keyPackage, pubkeyPackage, errorCode) = FrostNative.DKGRound3Finalize(
                        round2Secrets[participant], round1PackagesJson, round2PackagesJson);

                    if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(gpk) ||
                        string.IsNullOrEmpty(keyPackage) || string.IsNullOrEmpty(pubkeyPackage))
                    {
                        result.Errors.Add($"DKG Round 3 failed for participant {participant + 1}: error code {errorCode}");
                        return;
                    }

                    keyPackages[participant] = keyPackage;
                    pubkeyPackages[participant] = pubkeyPackage;

                    if (string.IsNullOrEmpty(groupPubkey))
                        groupPubkey = gpk;
                    else if (gpk != groupPubkey)
                    {
                        result.Errors.Add($"DKG Round 3: Group public key mismatch! Participant {participant + 1} got '{gpk}' vs expected '{groupPubkey}'");
                        return;
                    }
                }

                result.DKGRound3Passed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"DKG Round 3 exception: {ex.Message}");
                return;
            }

            // === SIGNING ROUND 1 ===
            // Use 2 of the 3 participants (participant 1 and 2) to sign a test message
            var testMessageHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA256 of empty string
            var signingParticipants = new int[] { 0, 1 }; // indices
            var nonceCommitments = new string[signingParticipants.Length];
            var nonceSecrets = new string[signingParticipants.Length];

            try
            {
                for (int i = 0; i < signingParticipants.Length; i++)
                {
                    int pIdx = signingParticipants[i];
                    var (nonceCommitment, nonceSecret, errorCode) = FrostNative.SignRound1Nonces(keyPackages[pIdx]);

                    if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(nonceCommitment) || string.IsNullOrEmpty(nonceSecret))
                    {
                        result.Errors.Add($"Sign Round 1 failed for participant {pIdx + 1}: error code {errorCode}");
                        return;
                    }

                    nonceCommitments[i] = nonceCommitment;
                    nonceSecrets[i] = nonceSecret;
                }

                result.SignRound1Passed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Sign Round 1 exception: {ex.Message}");
                return;
            }

            // === SIGNING ROUND 2 ===
            // Each signing participant generates a signature share
            var identifiersForSigning = signingParticipants.Select(p => BuildFrostIdentifier((ushort)(p + 1))).ToArray();
            var signatureShares = new string[signingParticipants.Length];

            try
            {
                // Build nonce commitments map: BTreeMap<Identifier, NonceCommitment>
                var nonceMap = new JObject();
                for (int i = 0; i < signingParticipants.Length; i++)
                {
                    nonceMap[identifiersForSigning[i]] = JToken.Parse(nonceCommitments[i]);
                }
                var nonceCommitmentsJson = nonceMap.ToString(Newtonsoft.Json.Formatting.None);

                for (int i = 0; i < signingParticipants.Length; i++)
                {
                    int pIdx = signingParticipants[i];
                    var (signatureShare, errorCode) = FrostNative.SignRound2Signature(
                        keyPackages[pIdx], nonceSecrets[i], nonceCommitmentsJson, testMessageHash);

                    if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(signatureShare))
                    {
                        result.Errors.Add($"Sign Round 2 failed for participant {pIdx + 1}: error code {errorCode}");
                        return;
                    }

                    signatureShares[i] = signatureShare;
                }

                result.SignRound2Passed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Sign Round 2 exception: {ex.Message}");
                return;
            }

            // === SIGNATURE AGGREGATION ===
            try
            {
                // Build signature shares map
                var sharesMap = new JObject();
                for (int i = 0; i < signingParticipants.Length; i++)
                {
                    sharesMap[identifiersForSigning[i]] = JToken.Parse(signatureShares[i]);
                }
                var signatureSharesJson = sharesMap.ToString(Newtonsoft.Json.Formatting.None);

                // Build nonce commitments map (same as in round 2)
                var nonceMap = new JObject();
                for (int i = 0; i < signingParticipants.Length; i++)
                {
                    nonceMap[identifiersForSigning[i]] = JToken.Parse(nonceCommitments[i]);
                }
                var nonceCommitmentsJson = nonceMap.ToString(Newtonsoft.Json.Formatting.None);

                // Use the first participant's pubkey package (all should be identical)
                var (schnorrSignature, errorCode) = FrostNative.SignAggregate(
                    signatureSharesJson, nonceCommitmentsJson, testMessageHash, pubkeyPackages[0]);

                if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(schnorrSignature))
                {
                    result.Errors.Add($"Sign Aggregate failed: error code {errorCode}");
                    return;
                }

                result.SignAggregatePassed = true;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Sign Aggregate exception: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Build the FROST Identifier for a 1-based participant index.
        /// FROST Identifier = 64-char hex of the index as a 32-byte big-endian scalar.
        /// This matches the logic in FrostStartup.BuildAddressToIdentifierMap.
        /// </summary>
        private static string BuildFrostIdentifier(ushort participantId)
        {
            // 32-byte big-endian scalar: last byte = participantId, rest = 0
            var bytes = new byte[32];
            bytes[31] = (byte)(participantId & 0xFF);
            if (participantId > 255)
                bytes[30] = (byte)((participantId >> 8) & 0xFF);

            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        #endregion
    }
}
