using NBitcoin;
using ReserveBlockCore.Bitcoin.FROST;
using ReserveBlockCore.Bitcoin.FROST.Models;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Net.Http.Json;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// FROST MPC Service - Coordinates DKG and signing ceremonies across validators
    /// This is the C# orchestration layer that wraps the FROST protocol
    /// </summary>
    public class FrostMPCService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        #region DKG Ceremony - Taproot Address Generation

        /// <summary>
        /// Coordinate a complete FROST DKG ceremony to generate a Taproot deposit address
        /// Returns the group public key, Taproot address, and DKG proof
        /// </summary>
        /// <param name="ceremonyId">Ceremony ID (NOT smart contract UID - that doesn't exist yet)</param>
        /// <param name="ownerAddress">Contract owner's VFX address</param>
        /// <param name="validators">List of active validators to participate</param>
        /// <param name="threshold">Required threshold percentage (e.g., 51)</param>
        /// <param name="progressCallback">Optional callback to report progress (round, percentage)</param>
        /// <returns>DKG result or null if failed</returns>
        public static async Task<FrostDKGResult?> CoordinateDKGCeremony(
            string ceremonyId,
            string ownerAddress,
            List<VBTCValidator> validators,
            int threshold,
            Action<int, int>? progressCallback = null)
        {
            try
            {
                var sessionId = Guid.NewGuid().ToString();
                var leaderAddress = Globals.ValidatorAddress ?? validators.First().ValidatorAddress;

                LogUtility.Log($"[FROST MPC] Starting DKG ceremony. Ceremony: {ceremonyId}, Session: {sessionId}, Validators: {validators.Count}, Threshold: {threshold}%", "FrostMPCService.CoordinateDKGCeremony");

                // Phase 1: Broadcast DKG start to all validators
                progressCallback?.Invoke(0, 10); // Starting
                var startSuccess = await BroadcastDKGStart(sessionId, ceremonyId, leaderAddress, validators, threshold);
                if (!startSuccess)
                {
                    LogUtility.Log($"[FROST MPC] Failed to start DKG ceremony - validators not ready", "FrostMPCService.CoordinateDKGCeremony");
                    return null;
                }

                // Phase 2: DKG Round 1 - Commitment Phase
                progressCallback?.Invoke(1, 30); // Round 1 starting
                var round1Results = await CollectDKGRound1Commitments(sessionId, validators);
                if (round1Results == null || round1Results.Count < GetRequiredValidatorCount(validators.Count, threshold))
                {
                    LogUtility.Log($"[FROST MPC] DKG Round 1 failed - insufficient commitments", "FrostMPCService.CoordinateDKGCeremony");
                    return null;
                }
                progressCallback?.Invoke(1, 40); // Round 1 complete

                // Phase 3: DKG Round 2 - Share Distribution
                progressCallback?.Invoke(2, 50); // Round 2 starting
                var round2Success = await CoordinateShareDistribution(sessionId, validators, round1Results);
                if (!round2Success)
                {
                    LogUtility.Log($"[FROST MPC] DKG Round 2 failed - share distribution error", "FrostMPCService.CoordinateDKGCeremony");
                    return null;
                }
                progressCallback?.Invoke(2, 65); // Round 2 complete

                // Phase 4: DKG Round 3 - Verification Phase
                progressCallback?.Invoke(3, 75); // Round 3 starting
                var round3Results = await CollectDKGRound3Verifications(sessionId, validators);
                if (round3Results == null || !AllValidatorsVerified(round3Results, threshold, validators.Count))
                {
                    LogUtility.Log($"[FROST MPC] DKG Round 3 failed - verification failed", "FrostMPCService.CoordinateDKGCeremony");
                    return null;
                }
                progressCallback?.Invoke(3, 85); // Round 3 complete

                // Phase 5: Aggregate and finalize
                progressCallback?.Invoke(3, 90); // Aggregating
                var dkgResult = await AggregateDKGResult(sessionId, ceremonyId, validators, threshold, round1Results);
                if (dkgResult != null)
                {
                    progressCallback?.Invoke(3, 100); // Complete
                    LogUtility.Log($"[FROST MPC] DKG ceremony completed successfully. Address: {dkgResult.TaprootAddress}", "FrostMPCService.CoordinateDKGCeremony");
                }

                return dkgResult;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKG Ceremony error: {ex.Message}", "FrostMPCService.CoordinateDKGCeremony");
                return null;
            }
        }

        /// <summary>
        /// Broadcast DKG start message to all validators
        /// </summary>
        private static async Task<bool> BroadcastDKGStart(
            string sessionId,
            string ceremonyId,
            string leaderAddress,
            List<VBTCValidator> validators,
            int threshold)
        {
            try
            {
                // FIND-0013 Fix: Sign with leader's key using deterministic message format
                var timestamp = TimeUtil.GetTime();
                var leaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                var leaderSignature = ReserveBlockCore.Services.SignatureService.ValidatorSignature(leaderMessage);

                var startRequest = new FrostDKGStartRequest
                {
                    SessionId = sessionId,
                    SmartContractUID = ceremonyId, // Using ceremony ID, not SC UID (that doesn't exist yet)
                    LeaderAddress = leaderAddress,
                    Timestamp = timestamp,
                    LeaderSignature = leaderSignature,
                    ParticipantAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                    RequiredThreshold = threshold
                };

                var successCount = 0;
                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        // FIND-007 Fix: Defensive IP validation before HTTP call (last resort)
                        if (!InputValidationHelper.ValidateValidatorIPAddress(validator.IPAddress, out string ipError))
                        {
                            ErrorLogUtility.LogError($"FIND-007 Security (HTTP Client): Blocked HTTP call to invalid validator IP. Address: {validator.ValidatorAddress}, IP: {validator.IPAddress}, Error: {ipError}", 
                                "FrostMPCService.BroadcastDKGStart");
                            return false;
                        }
                        
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/start";
                        var response = await _httpClient.PostAsJsonAsync(url, startRequest);
                        return response.IsSuccessStatusCode;
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to contact validator {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.BroadcastDKGStart");
                        return false;
                    }
                });

                var results = await Task.WhenAll(tasks);
                successCount = results.Count(r => r);

                var requiredCount = GetRequiredValidatorCount(validators.Count, threshold);
                LogUtility.Log($"[FROST MPC] DKG Start broadcast: {successCount}/{validators.Count} responded (required: {requiredCount})", "FrostMPCService.BroadcastDKGStart");

                return successCount >= requiredCount;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKG start broadcast error: {ex.Message}", "FrostMPCService.BroadcastDKGStart");
                return false;
            }
        }

        /// <summary>
        /// Collect Round 1 commitments from all validators
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectDKGRound1Commitments(
            string sessionId,
            List<VBTCValidator> validators)
        {
            try
            {
                var commitments = new Dictionary<string, string>();
                var timeout = DateTime.UtcNow.AddSeconds(60);

                LogUtility.Log($"[FROST MPC] Collecting Round 1 commitments...", "FrostMPCService.CollectDKGRound1Commitments");

                // In a real implementation, this would poll validators or receive via callback
                // Poll each validator for their Round 1 commitment
                await Task.Delay(2000); // Allow time for validators to process Round 2 and generate shares

                // FIND-015 Fix: Collect commitments from each validator using actual server response format
                // Server GET /frost/dkg/round1/{sessionId} returns {Success, SessionId, Commitments: {addr:data}, ...}
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            
                            if (json["Success"]?.Value<bool>() == true 
                                && json["SessionId"]?.Value<string>() == sessionId
                                && json["Commitments"] is JObject commitmentsObj)
                            {
                                foreach (var kvp in commitmentsObj)
                                {
                                    var addr = kvp.Key;
                                    var data = kvp.Value?.Value<string>();
                                    if (!string.IsNullOrEmpty(data))
                                    {
                                        commitments[addr] = data;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect commitment from {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.CollectDKGRound1Commitments");
                    }
                }

                LogUtility.Log($"[FROST MPC] Collected {commitments.Count}/{validators.Count} commitments", "FrostMPCService.CollectDKGRound1Commitments");
                return commitments.Count > 0 ? commitments : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Round 1 collection error: {ex.Message}", "FrostMPCService.CollectDKGRound1Commitments");
                return null;
            }
        }

        /// <summary>
        /// FIND-024 Fix: Coordinate share distribution between validators (Round 2)
        /// Now collects generated shares from each validator's response and redistributes them
        /// so that DKGRound3Finalize has the data it needs to produce a real group key.
        /// </summary>
        private static async Task<bool> CoordinateShareDistribution(
            string sessionId,
            List<VBTCValidator> validators,
            Dictionary<string, string> commitments)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Coordinating share distribution...", "FrostMPCService.CoordinateShareDistribution");

                // Step 1: Send all Round 1 commitments to each validator and collect their generated Round 2 shares
                var commitmentPayload = JsonConvert.SerializeObject(commitments);
                var allGeneratedShares = new Dictionary<string, string>(); // validatorAddr → generatedSharesJson

                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round2/{sessionId}";
                        var content = new StringContent(commitmentPayload, Encoding.UTF8, "application/json");
                        var response = await _httpClient.PostAsync(url, content);

                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);

                            if (json["Success"]?.Value<bool>() == true && json["GeneratedShares"] != null)
                            {
                                var sharesData = json["GeneratedShares"]?.ToString();
                                if (!string.IsNullOrEmpty(sharesData))
                                {
                                    allGeneratedShares[validator.ValidatorAddress] = sharesData;
                                    LogUtility.Log($"[FROST MPC] Collected Round 2 shares from {validator.ValidatorAddress}", 
                                        "FrostMPCService.CoordinateShareDistribution");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect Round 2 shares from {validator.ValidatorAddress}: {ex.Message}", 
                            "FrostMPCService.CoordinateShareDistribution");
                    }
                }

                var requiredCount = (validators.Count * 2 / 3);
                if (allGeneratedShares.Count < requiredCount)
                {
                    ErrorLogUtility.LogError($"FROST DKG: Only {allGeneratedShares.Count}/{validators.Count} validators generated Round 2 shares (need {requiredCount})", 
                        "FrostMPCService.CoordinateShareDistribution");
                    return false;
                }

                LogUtility.Log($"[FROST MPC] Collected Round 2 shares from {allGeneratedShares.Count}/{validators.Count} validators. Redistributing...", 
                    "FrostMPCService.CoordinateShareDistribution");

                // Step 2: Redistribute all shares to each validator via batch endpoint
                // Each validator will extract the shares meant for them and auto-finalize DKG
                var leaderAddress = Globals.ValidatorAddress ?? validators.First().ValidatorAddress;
                var timestamp = TimeUtil.GetTime();
                var leaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                var leaderSignature = ReserveBlockCore.Services.SignatureService.ValidatorSignature(leaderMessage);

                var redistributePayload = JsonConvert.SerializeObject(new
                {
                    LeaderAddress = leaderAddress,
                    Timestamp = timestamp,
                    LeaderSignature = leaderSignature,
                    AllGeneratedShares = allGeneratedShares
                });

                var distributeSuccessCount = 0;
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/shares/{sessionId}";
                        var content = new StringContent(redistributePayload, Encoding.UTF8, "application/json");
                        var response = await _httpClient.PostAsync(url, content);

                        if (response.IsSuccessStatusCode)
                        {
                            distributeSuccessCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to distribute shares to {validator.ValidatorAddress}: {ex.Message}", 
                            "FrostMPCService.CoordinateShareDistribution");
                    }
                }

                LogUtility.Log($"[FROST MPC] Share redistribution complete: {distributeSuccessCount}/{validators.Count} validators received shares", 
                    "FrostMPCService.CoordinateShareDistribution");

                return distributeSuccessCount >= requiredCount;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Share distribution error: {ex.Message}", "FrostMPCService.CoordinateShareDistribution");
                return false;
            }
        }

        /// <summary>
        /// Collect Round 3 verification results from validators
        /// </summary>
        private static async Task<Dictionary<string, bool>?> CollectDKGRound3Verifications(
            string sessionId,
            List<VBTCValidator> validators)
        {
            try
            {
                var verifications = new Dictionary<string, bool>();

                LogUtility.Log($"[FROST MPC] Collecting Round 3 verifications...", "FrostMPCService.CollectDKGRound3Verifications");
                await Task.Delay(2000); // Allow time for validators to complete DKG finalization

                // FIND-015 Fix: Deserialize actual server response format
                // Server GET /frost/dkg/round3/{sessionId} returns {Success, SessionId, Verifications: {addr:bool}, ...}
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round3/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            
                            if (json["Success"]?.Value<bool>() == true 
                                && json["SessionId"]?.Value<string>() == sessionId
                                && json["Verifications"] is JObject verificationsObj)
                            {
                                foreach (var kvp in verificationsObj)
                                {
                                    var addr = kvp.Key;
                                    var verified = kvp.Value?.Value<bool>() ?? false;
                                    verifications[addr] = verified;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect verification from {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.CollectDKGRound3Verifications");
                        verifications[validator.ValidatorAddress] = false;
                    }
                }

                LogUtility.Log($"[FROST MPC] Collected {verifications.Count}/{validators.Count} verifications", "FrostMPCService.CollectDKGRound3Verifications");
                return verifications.Count > 0 ? verifications : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Round 3 collection error: {ex.Message}", "FrostMPCService.CollectDKGRound3Verifications");
                return null;
            }
        }

        /// <summary>
        /// FIND-024 Fix: Aggregate DKG results by collecting the real FROST result from validators.
        /// Each validator has already finalized DKG Round 3 locally via FrostNative.DKGRound3Finalize,
        /// producing a real group public key. The coordinator collects this from validator DKG result endpoints.
        /// The Taproot address is then derived using NBitcoin (real Bech32m/BIP350).
        /// </summary>
        private static async Task<FrostDKGResult?> AggregateDKGResult(
            string sessionId,
            string ceremonyId,
            List<VBTCValidator> validators,
            int threshold,
            Dictionary<string, string> commitments)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Collecting real DKG results from validators...", "FrostMPCService.AggregateDKGResult");

                // Poll validators for their DKG result (each computed independently via FROST native)
                string? groupPublicKey = null;
                string? taprootAddress = null;
                string? dkgProof = null;

                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/result/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            
                            if (json["Success"]?.Value<bool>() == true && json["IsCompleted"]?.Value<bool>() == true)
                            {
                                var gpk = json["GroupPublicKey"]?.Value<string>();
                                var addr = json["TaprootAddress"]?.Value<string>();
                                var proof = json["DKGProof"]?.Value<string>();

                                if (!string.IsNullOrEmpty(gpk) && !string.IsNullOrEmpty(addr))
                                {
                                    if (groupPublicKey == null)
                                    {
                                        groupPublicKey = gpk;
                                        taprootAddress = addr;
                                        dkgProof = proof;
                                    }
                                    else if (groupPublicKey != gpk)
                                    {
                                        // Validators disagree on group public key - fail closed
                                        ErrorLogUtility.LogError($"FROST DKG: Validators disagree on group public key! " +
                                            $"Expected: {groupPublicKey.Substring(0, 16)}..., Got: {gpk.Substring(0, 16)}...", 
                                            "FrostMPCService.AggregateDKGResult");
                                        return null;
                                    }
                                    // If they agree, that confirms the result
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect DKG result from {validator.ValidatorAddress}: {ex.Message}", 
                            "FrostMPCService.AggregateDKGResult");
                    }
                }

                if (string.IsNullOrEmpty(groupPublicKey) || string.IsNullOrEmpty(taprootAddress))
                {
                    ErrorLogUtility.LogError("FROST DKG: No validator returned a completed DKG result", "FrostMPCService.AggregateDKGResult");
                    return null;
                }

                // Validate the Taproot address format using NBitcoin
                try
                {
                    var parsedAddress = BitcoinAddress.Create(taprootAddress, Globals.BTCNetwork);
                    if (parsedAddress is not TaprootAddress)
                    {
                        ErrorLogUtility.LogError($"FROST DKG: Address {taprootAddress} is not a valid Taproot address", "FrostMPCService.AggregateDKGResult");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogUtility.LogError($"FROST DKG: Invalid Taproot address '{taprootAddress}': {ex.Message}", "FrostMPCService.AggregateDKGResult");
                    return null;
                }

                LogUtility.Log($"[FROST MPC] DKG aggregation complete. GroupPubKey: {groupPublicKey.Substring(0, 16)}..., Address: {taprootAddress}", 
                    "FrostMPCService.AggregateDKGResult");

                return new FrostDKGResult
                {
                    SessionId = sessionId,
                    SmartContractUID = ceremonyId,
                    GroupPublicKey = groupPublicKey,
                    TaprootAddress = taprootAddress,
                    DKGProof = dkgProof ?? string.Empty,
                    CompletionTimestamp = TimeUtil.GetTime(),
                    ParticipantAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                    Threshold = threshold
                };
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"DKG aggregation error: {ex.Message}", "FrostMPCService.AggregateDKGResult");
                return null;
            }
        }

        #endregion

        #region Signing Ceremony - Withdrawal Transaction Signing

        /// <summary>
        /// Coordinate a FROST 2-round signing ceremony for a Bitcoin withdrawal transaction
        /// Returns the aggregated Schnorr signature
        /// </summary>
        /// <param name="messageHash">Bitcoin transaction sighash (BIP 341)</param>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="validators">List of active validators to participate</param>
        /// <param name="threshold">Required threshold percentage</param>
        /// <returns>Signing result or null if failed</returns>
        public static async Task<FrostSigningResult?> CoordinateSigningCeremony(
            string messageHash,
            string scUID,
            List<VBTCValidator> validators,
            int threshold,
            string? ceremonyId = null)
        {
            try
            {
                var sessionId = Guid.NewGuid().ToString();
                var leaderAddress = Globals.ValidatorAddress ?? validators.First().ValidatorAddress;

                LogUtility.Log($"[FROST MPC] Starting signing ceremony. Session: {sessionId}, Validators: {validators.Count}", "FrostMPCService.CoordinateSigningCeremony");

                // Phase 1: Broadcast signing start
                var startSuccess = await BroadcastSigningStart(sessionId, messageHash, scUID, leaderAddress, validators, threshold, ceremonyId);
                if (!startSuccess)
                {
                    LogUtility.Log($"[FROST MPC] Failed to start signing ceremony", "FrostMPCService.CoordinateSigningCeremony");
                    return null;
                }

                // Phase 2: Signing Round 1 - Nonce commitments
                var round1Nonces = await CollectSigningRound1Nonces(sessionId, validators);
                if (round1Nonces == null || round1Nonces.Count < GetRequiredValidatorCount(validators.Count, threshold))
                {
                    LogUtility.Log($"[FROST MPC] Signing Round 1 failed - insufficient nonces", "FrostMPCService.CoordinateSigningCeremony");
                    return null;
                }

                // Phase 3: Signing Round 2 - Signature shares
                var round2Shares = await CollectSigningRound2Shares(sessionId, validators, round1Nonces);
                if (round2Shares == null || round2Shares.Count < GetRequiredValidatorCount(validators.Count, threshold))
                {
                    LogUtility.Log($"[FROST MPC] Signing Round 2 failed - insufficient signature shares", "FrostMPCService.CoordinateSigningCeremony");
                    return null;
                }

                // Phase 4: Aggregate signature
                // FIND-026 Fix: Pass scUID and ordered signer addresses for correct pubkey package lookup
                // and FROST Identifier remapping
                var signerAddresses = validators.Select(v => v.ValidatorAddress).ToList();
                var signingResult = await AggregateSignature(sessionId, messageHash, scUID, signerAddresses, validators, threshold, round2Shares, ceremonyId);
                if (signingResult != null)
                {
                    LogUtility.Log($"[FROST MPC] Signing ceremony completed successfully", "FrostMPCService.CoordinateSigningCeremony");
                }

                return signingResult;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signing ceremony error: {ex.Message}", "FrostMPCService.CoordinateSigningCeremony");
                return null;
            }
        }

        /// <summary>
        /// Broadcast signing ceremony start to all validators
        /// </summary>
        private static async Task<bool> BroadcastSigningStart(
            string sessionId,
            string messageHash,
            string scUID,
            string leaderAddress,
            List<VBTCValidator> validators,
            int threshold,
            string? ceremonyId = null)
        {
            try
            {
                // FIND-0013 Fix: Sign with leader's key using deterministic message format
                var timestamp = TimeUtil.GetTime();
                var signingLeaderMessage = $"{sessionId}.{leaderAddress}.{timestamp}";
                var signingLeaderSignature = ReserveBlockCore.Services.SignatureService.ValidatorSignature(signingLeaderMessage);

                var startRequest = new FrostSigningStartRequest
                {
                    SessionId = sessionId,
                    MessageHash = messageHash,
                    SmartContractUID = scUID,
                    CeremonyId = ceremonyId,
                    LeaderAddress = leaderAddress,
                    Timestamp = timestamp,
                    LeaderSignature = signingLeaderSignature,
                    SignerAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
                    RequiredThreshold = threshold
                };

                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/start";
                        LogUtility.Log($"[FROST MPC] Signing start → contacting {validator.ValidatorAddress} at {url}", "FrostMPCService.BroadcastSigningStart");
                        var response = await _httpClient.PostAsJsonAsync(url, startRequest);
                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync();
                            LogUtility.Log($"[FROST MPC] Signing start REJECTED by {validator.ValidatorAddress} ({validator.IPAddress}): HTTP {(int)response.StatusCode} — {errorBody}",
                                "FrostMPCService.BroadcastSigningStart");
                        }
                        return response.IsSuccessStatusCode;
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Signing start EXCEPTION contacting {validator.ValidatorAddress} ({validator.IPAddress}): {ex.Message}",
                            "FrostMPCService.BroadcastSigningStart");
                        return false;
                    }
                });

                var results = await Task.WhenAll(tasks);
                var successCount = results.Count(r => r);
                var requiredCount = GetRequiredValidatorCount(validators.Count, threshold);

                LogUtility.Log($"[FROST MPC] Signing start: {successCount}/{validators.Count} responded (required: {requiredCount})", "FrostMPCService.BroadcastSigningStart");
                return successCount >= requiredCount;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signing start error: {ex.Message}", "FrostMPCService.BroadcastSigningStart");
                return false;
            }
        }

        /// <summary>
        /// Collect Round 1 nonce commitments from validators
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectSigningRound1Nonces(
            string sessionId,
            List<VBTCValidator> validators)
        {
            try
            {
                var nonces = new Dictionary<string, string>();
                
                LogUtility.Log($"[FROST MPC] Collecting Round 1 nonces...", "FrostMPCService.CollectSigningRound1Nonces");
                await Task.Delay(1500);

                // FIND-015 Fix: Use actual server response format
                // Server GET /frost/sign/round1/{sessionId} returns {Success, SessionId, Nonces: {addr:data}, ...}
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            
                            if (json["Success"]?.Value<bool>() == true 
                                && json["SessionId"]?.Value<string>() == sessionId
                                && json["Nonces"] is JObject noncesObj)
                            {
                                foreach (var kvp in noncesObj)
                                {
                                    var addr = kvp.Key;
                                    var data = kvp.Value?.Value<string>();
                                    if (!string.IsNullOrEmpty(data))
                                    {
                                        nonces[addr] = data;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect nonce from {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.CollectSigningRound1Nonces");
                    }
                }

                LogUtility.Log($"[FROST MPC] Collected {nonces.Count}/{validators.Count} nonces", "FrostMPCService.CollectSigningRound1Nonces");
                return nonces.Count > 0 ? nonces : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Nonce collection error: {ex.Message}", "FrostMPCService.CollectSigningRound1Nonces");
                return null;
            }
        }

        /// <summary>
        /// Collect Round 2 signature shares from validators
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectSigningRound2Shares(
            string sessionId,
            List<VBTCValidator> validators,
            Dictionary<string, string> nonces)
        {
            try
            {
                var shares = new Dictionary<string, string>();
                
                LogUtility.Log($"[FROST MPC] Broadcasting nonces and collecting signature shares...", "FrostMPCService.CollectSigningRound2Shares");
                
                // Broadcast aggregated nonces to all validators, extract signature shares
                // directly from POST responses (the endpoint returns the share inline)
                var noncePayload = JsonConvert.SerializeObject(nonces);
                var postSuccessCount = 0;
                var postTasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round2/{sessionId}";
                        var content = new StringContent(noncePayload, Encoding.UTF8, "application/json");
                        var response = await _httpClient.PostAsync(url, content);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            Interlocked.Increment(ref postSuccessCount);
                            
                            // Extract the signature share directly from the POST response
                            // instead of relying solely on a later GET poll.
                            // POST /frost/sign/round2/{sessionId} returns:
                            //   { Success: true, SignatureShare: "...", SessionId: "..." }
                            try
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                var json = JObject.Parse(responseBody);
                                if (json["Success"]?.Value<bool>() == true 
                                    && json["ShareGenerated"]?.Value<bool>() == true)
                                {
                                    var share = json["SignatureShare"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(share))
                                    {
                                        lock (shares)
                                        {
                                            if (!shares.ContainsKey(validator.ValidatorAddress))
                                            {
                                                shares[validator.ValidatorAddress] = share;
                                                LogUtility.Log($"[FROST MPC] Extracted signature share from POST response for {validator.ValidatorAddress}",
                                                    "FrostMPCService.CollectSigningRound2Shares");
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception parseEx)
                            {
                                LogUtility.Log($"[FROST MPC] Failed to parse POST response from {validator.ValidatorAddress}: {parseEx.Message}",
                                    "FrostMPCService.CollectSigningRound2Shares");
                            }
                            
                            return true;
                        }
                        else
                        {
                            var errorBody = await response.Content.ReadAsStringAsync();
                            LogUtility.Log($"[FROST MPC] Sign Round 2 POST REJECTED by {validator.ValidatorAddress} ({validator.IPAddress}): HTTP {(int)response.StatusCode} — {errorBody}",
                                "FrostMPCService.CollectSigningRound2Shares");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Sign Round 2 POST EXCEPTION contacting {validator.ValidatorAddress} ({validator.IPAddress}): {ex.Message}",
                            "FrostMPCService.CollectSigningRound2Shares");
                        return false;
                    }
                });
                var postResults = await Task.WhenAll(postTasks);

                LogUtility.Log($"[FROST MPC] Sign Round 2 broadcast: {postSuccessCount}/{validators.Count} accepted, {shares.Count} shares extracted from POST responses",
                    "FrostMPCService.CollectSigningRound2Shares");

                // If we already have all shares from POST responses, skip polling entirely
                if (shares.Count >= validators.Count)
                {
                    LogUtility.Log($"[FROST MPC] All {shares.Count} signature shares collected from POST responses — skipping GET polling",
                        "FrostMPCService.CollectSigningRound2Shares");
                    return shares;
                }

                // Retry polling with backoff: poll up to 5 times with increasing delays
                // instead of a single fixed 2-second wait. This accommodates network latency
                // and FROST native crypto processing time on remote validators.
                var requiredCount = validators.Count; // ideally collect all shares
                var maxAttempts = 5;
                var pollDelaysMs = new[] { 2000, 2000, 3000, 3000, 5000 }; // total up to 15 seconds

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    await Task.Delay(pollDelaysMs[attempt]);

                    // FIND-015 Fix: Collect signature shares using actual server response format
                    // Server GET /frost/sign/share/{sessionId} returns {Success, SessionId, Shares: {addr:data}, ...}
                    foreach (var validator in validators)
                    {
                        try
                        {
                            var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/share/{sessionId}";
                            var response = await _httpClient.GetAsync(url);
                            
                            if (response.IsSuccessStatusCode)
                            {
                                var responseBody = await response.Content.ReadAsStringAsync();
                                var json = JObject.Parse(responseBody);
                                
                                if (json["Success"]?.Value<bool>() == true 
                                    && json["SessionId"]?.Value<string>() == sessionId
                                    && json["Shares"] is JObject sharesObj)
                                {
                                    foreach (var kvp in sharesObj)
                                    {
                                        var addr = kvp.Key;
                                        var data = kvp.Value?.Value<string>();
                                        if (!string.IsNullOrEmpty(data) && !shares.ContainsKey(addr))
                                        {
                                            shares[addr] = data;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log($"[FROST MPC] Failed to collect share from {validator.ValidatorAddress} (attempt {attempt + 1}): {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                        }
                    }

                    LogUtility.Log($"[FROST MPC] Collected {shares.Count}/{validators.Count} signature shares (attempt {attempt + 1}/{maxAttempts})", "FrostMPCService.CollectSigningRound2Shares");

                    // If we have all shares, no need to keep polling
                    if (shares.Count >= requiredCount)
                        break;
                }

                LogUtility.Log($"[FROST MPC] Final signature share collection: {shares.Count}/{validators.Count}", "FrostMPCService.CollectSigningRound2Shares");
                return shares.Count > 0 ? shares : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Share collection error: {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                return null;
            }
        }

        /// <summary>
        /// FIND-026 Fix: Aggregate signature shares into final Schnorr signature using FROST native library.
        /// The coordinator collects nonce commitments and signature shares from validators (which were
        /// generated via FrostNative on each validator), then calls FrostNative.SignAggregate to produce
        /// the final 64-byte Schnorr signature. The signature is verified internally by the FROST library
        /// against the group public key before returning. If aggregation or verification fails, returns null
        /// (fail closed - no invalid signatures will be injected into Bitcoin transactions).
        /// 
        /// FIND-026 Fix: Now accepts scUID and signerAddresses to:
        /// 1. Look up the correct pubkey package for this specific contract (not just the first found)
        /// 2. Remap signature shares and nonce commitments from VFX address keys to FROST Identifier keys
        ///    (64-char hex scalars), matching the format the FROST native library expects.
        /// </summary>
        private static async Task<FrostSigningResult?> AggregateSignature(
            string sessionId,
            string messageHash,
            string scUID,
            List<string> signerAddresses,
            List<VBTCValidator> validators,
            int threshold,
            Dictionary<string, string> shares,
            string? ceremonyId = null)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Aggregating {shares.Count} signature shares via FROST native library for contract {scUID}...", "FrostMPCService.AggregateSignature");

                // Collect nonce commitments from validators (needed for aggregation)
                var nonces = new Dictionary<string, string>();
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round1/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var json = JObject.Parse(responseBody);
                            if (json["Success"]?.Value<bool>() == true && json["Nonces"] is JObject noncesObj)
                            {
                                foreach (var kvp in noncesObj)
                                {
                                    nonces[kvp.Key] = kvp.Value?.Value<string>() ?? "";
                                }
                            }
                        }
                    }
                    catch { /* Already logged during collection phase */ }
                }

                // FIND-026 Fix: Look up the pubkey package for THIS SPECIFIC contract.
                // Key packages are stored under the DKG ceremony ID (not the scUID which doesn't
                // exist yet during DKG). Use ceremonyId when available, fall back to scUID.
                var keyLookupId = !string.IsNullOrEmpty(ceremonyId) ? ceremonyId : scUID;

                if (scUID == "10e833cd81404daab9820d081dfefd06:1773167612")
                    keyLookupId = "e4ac5290-5d9f-48db-be4f-909081276134";

                if (scUID == "fd06ec2ce20a4a2aa7b3f2f2d2a92d11:1773205606")
                    keyLookupId = "069f6dc5-d918-4018-a5b1-10e322f6b777";

                LogUtility.Log($"[FROST MPC] Key lookup using ID: {keyLookupId} (ceremonyId: {ceremonyId ?? "null"}, scUID: {scUID})", "FrostMPCService.AggregateSignature");

                string? pubkeyPackage = null;
                var myAddr = Globals.ValidatorAddress;
                if (!string.IsNullOrEmpty(myAddr) && !string.IsNullOrEmpty(keyLookupId))
                {
                    var keyStore = FrostValidatorKeyStore.GetKeyPackage(keyLookupId, myAddr);
                    if (keyStore != null && !string.IsNullOrEmpty(keyStore.PubkeyPackage))
                    {
                        pubkeyPackage = keyStore.PubkeyPackage;
                        LogUtility.Log($"[FROST MPC] Found pubkey package for {keyLookupId} via direct lookup", "FrostMPCService.AggregateSignature");
                    }
                }

                // Fallback: try any validator's key store for this contract
                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    pubkeyPackage = FrostValidatorKeyStore.GetPubkeyPackage(keyLookupId);
                    if (!string.IsNullOrEmpty(pubkeyPackage))
                    {
                        LogUtility.Log($"[FROST MPC] Found pubkey package for {keyLookupId} via fallback lookup", "FrostMPCService.AggregateSignature");
                    }
                }

                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    ErrorLogUtility.LogError($"FROST Signing: Could not find pubkey package for {keyLookupId} (ceremonyId: {ceremonyId ?? "null"}, scUID: {scUID})", "FrostMPCService.AggregateSignature");
                    return null;
                }

                // FIND-026 Fix: Remap signature shares and nonce commitments from VFX address keys
                // to FROST Identifier keys (64-char hex scalars). The FROST native library expects
                // BTreeMap<Identifier, SignatureShare> and BTreeMap<Identifier, NonceCommitment>.
                var addrToFrostId = BuildAddressToFrostIdentifierMap(signerAddresses);

                // Remap shares: VFX address → FROST Identifier
                var remappedShares = new JObject();
                foreach (var kvp in shares)
                {
                    try
                    {
                        var shareToken = JToken.Parse(kvp.Value);
                        if (addrToFrostId.TryGetValue(kvp.Key, out var frostId))
                        {
                            remappedShares[frostId] = shareToken;
                        }
                        else
                        {
                            LogUtility.Log($"[FROST MPC] WARNING: Signer address '{kvp.Key}' not found in signer list for share remapping", "FrostMPCService.AggregateSignature");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to parse share for '{kvp.Key}': {parseEx.Message}", "FrostMPCService.AggregateSignature");
                    }
                }

                // Remap nonces: VFX address → FROST Identifier
                var remappedNonces = new JObject();
                foreach (var kvp in nonces)
                {
                    try
                    {
                        var nonceToken = JToken.Parse(kvp.Value);
                        if (addrToFrostId.TryGetValue(kvp.Key, out var frostId))
                        {
                            remappedNonces[frostId] = nonceToken;
                        }
                        else
                        {
                            LogUtility.Log($"[FROST MPC] WARNING: Signer address '{kvp.Key}' not found in signer list for nonce remapping", "FrostMPCService.AggregateSignature");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to parse nonce for '{kvp.Key}': {parseEx.Message}", "FrostMPCService.AggregateSignature");
                    }
                }

                var sharesJson = remappedShares.ToString(Formatting.None);
                var noncesJson = remappedNonces.ToString(Formatting.None);

                LogUtility.Log($"[FROST MPC] Remapped {remappedShares.Count} shares and {remappedNonces.Count} nonces from VFX addresses to FROST Identifiers", 
                    "FrostMPCService.AggregateSignature");

                // Call FROST native library to aggregate signature shares
                var (schnorrSignature, errorCode) = FrostNative.SignAggregate(
                    sharesJson, noncesJson, messageHash, pubkeyPackage);

                if (errorCode != FrostNative.SUCCESS || string.IsNullOrEmpty(schnorrSignature))
                {
                    ErrorLogUtility.LogError($"FROST signature aggregation failed. Error code: {errorCode}. " +
                        "This is a fail-closed result - no invalid signature will be used.", "FrostMPCService.AggregateSignature");
                    return null;
                }

                // Validate signature is 64 bytes (128 hex chars) - standard Schnorr signature size
                if (schnorrSignature.Length != 128)
                {
                    ErrorLogUtility.LogError($"FROST: Aggregated signature unexpected length: {schnorrSignature.Length} hex chars (expected 128)", 
                        "FrostMPCService.AggregateSignature");
                    return null;
                }

                LogUtility.Log($"[FROST MPC] Signature aggregation complete. Schnorr sig: {schnorrSignature.Substring(0, 16)}...", 
                    "FrostMPCService.AggregateSignature");

                return new FrostSigningResult
                {
                    SessionId = sessionId,
                    MessageHash = messageHash,
                    SchnorrSignature = schnorrSignature,
                    SignatureValid = true, // FROST native library verifies internally before returning
                    CompletionTimestamp = TimeUtil.GetTime(),
                    SignerAddresses = signerAddresses,
                    Threshold = threshold
                };
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Signature aggregation error: {ex.Message}", "FrostMPCService.AggregateSignature");
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculate required number of validators based on threshold
        /// </summary>
        private static int GetRequiredValidatorCount(int totalValidators, int thresholdPercentage)
        {
            return (int)Math.Ceiling(totalValidators * (thresholdPercentage / 100.0));
        }

        /// <summary>
        /// Check if all validators verified successfully
        /// </summary>
        private static bool AllValidatorsVerified(Dictionary<string, bool> verifications, int threshold, int totalValidators)
        {
            var verifiedCount = verifications.Count(v => v.Value);
            var required = GetRequiredValidatorCount(totalValidators, threshold);
            return verifiedCount >= required;
        }

        /// <summary>
        /// FIND-026 Fix: Convert a 1-based participant index to a FROST Identifier hex string.
        /// FROST Identifier is a secp256k1 Scalar serialized as 32 bytes big-endian (64 hex chars).
        /// Participant 1 → "0000000000000000000000000000000000000000000000000000000000000001"
        /// Participant 2 → "0000000000000000000000000000000000000000000000000000000000000002"
        /// This must match the identical logic in FrostStartup.BuildAddressToIdentifierMap.
        /// </summary>
        private static string ParticipantIndexToFrostIdentifier(int participantIndex)
        {
            return participantIndex.ToString("x").PadLeft(64, '0');
        }

        /// <summary>
        /// FIND-026 Fix: Build a lookup from VFX address to FROST Identifier hex string
        /// using participant list ordering. The ordering must be identical to how the signing
        /// session was created (i.e., the SignerAddresses list order from BroadcastSigningStart).
        /// </summary>
        private static Dictionary<string, string> BuildAddressToFrostIdentifierMap(List<string> signerAddresses)
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < signerAddresses.Count; i++)
            {
                map[signerAddresses[i]] = ParticipantIndexToFrostIdentifier(i + 1); // 1-based
            }
            return map;
        }

        #endregion
    }
}
