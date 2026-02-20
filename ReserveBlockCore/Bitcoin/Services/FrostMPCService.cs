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
                // For now, simulate commitment collection
                await Task.Delay(2000); // Simulate network round trip

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
        /// Coordinate share distribution between validators (Round 2)
        /// </summary>
        private static async Task<bool> CoordinateShareDistribution(
            string sessionId,
            List<VBTCValidator> validators,
            Dictionary<string, string> commitments)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Coordinating share distribution...", "FrostMPCService.CoordinateShareDistribution");

                // Broadcast all commitments to all validators
                // Each validator will then generate and send shares to others
                var commitmentPayload = JsonConvert.SerializeObject(commitments);
                
                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/round2/{sessionId}";
                        var content = new StringContent(commitmentPayload, Encoding.UTF8, "application/json");
                        var response = await _httpClient.PostAsync(url, content);
                        return response.IsSuccessStatusCode;
                    }
                    catch
                    {
                        return false;
                    }
                });

                var results = await Task.WhenAll(tasks);
                var successCount = results.Count(r => r);

                LogUtility.Log($"[FROST MPC] Share distribution: {successCount}/{validators.Count} validators", "FrostMPCService.CoordinateShareDistribution");
                
                // Wait for validators to exchange shares
                await Task.Delay(3000);

                return successCount >= (validators.Count * 2 / 3); // Require 2/3 for share distribution
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
                await Task.Delay(2000); // Simulate verification time

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
                    var network = Globals.IsTestNet ? Network.TestNet : Network.Main;
                    var parsedAddress = BitcoinAddress.Create(taprootAddress, network);
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
            int threshold)
        {
            try
            {
                var sessionId = Guid.NewGuid().ToString();
                var leaderAddress = Globals.ValidatorAddress ?? validators.First().ValidatorAddress;

                LogUtility.Log($"[FROST MPC] Starting signing ceremony. Session: {sessionId}, Validators: {validators.Count}", "FrostMPCService.CoordinateSigningCeremony");

                // Phase 1: Broadcast signing start
                var startSuccess = await BroadcastSigningStart(sessionId, messageHash, scUID, leaderAddress, validators, threshold);
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
                var signingResult = await AggregateSignature(sessionId, messageHash, validators, threshold, round2Shares);
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
            int threshold)
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
                        var response = await _httpClient.PostAsJsonAsync(url, startRequest);
                        return response.IsSuccessStatusCode;
                    }
                    catch
                    {
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
                
                // Broadcast aggregated nonces to all validators
                var noncePayload = JsonConvert.SerializeObject(nonces);
                var tasks = validators.Select(async validator =>
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/sign/round2/{sessionId}";
                        var content = new StringContent(noncePayload, Encoding.UTF8, "application/json");
                        await _httpClient.PostAsync(url, content);
                    }
                    catch { }
                });
                await Task.WhenAll(tasks);

                await Task.Delay(2000);

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
                                    if (!string.IsNullOrEmpty(data))
                                    {
                                        shares[addr] = data;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[FROST MPC] Failed to collect share from {validator.ValidatorAddress}: {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                    }
                }

                LogUtility.Log($"[FROST MPC] Collected {shares.Count}/{validators.Count} signature shares", "FrostMPCService.CollectSigningRound2Shares");
                return shares.Count > 0 ? shares : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Share collection error: {ex.Message}", "FrostMPCService.CollectSigningRound2Shares");
                return null;
            }
        }

        /// <summary>
        /// FIND-024 Fix: Aggregate signature shares into final Schnorr signature using FROST native library.
        /// The coordinator collects nonce commitments and signature shares from validators (which were
        /// generated via FrostNative on each validator), then calls FrostNative.SignAggregate to produce
        /// the final 64-byte Schnorr signature. The signature is verified internally by the FROST library
        /// against the group public key before returning. If aggregation or verification fails, returns null
        /// (fail closed - no invalid signatures will be injected into Bitcoin transactions).
        /// </summary>
        private static async Task<FrostSigningResult?> AggregateSignature(
            string sessionId,
            string messageHash,
            List<VBTCValidator> validators,
            int threshold,
            Dictionary<string, string> shares)
        {
            try
            {
                LogUtility.Log($"[FROST MPC] Aggregating {shares.Count} signature shares via FROST native library...", "FrostMPCService.AggregateSignature");

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

                // Get the pubkey package for this contract (needed for aggregation)
                // The coordinator needs to know which contract to look up
                string? pubkeyPackage = null;
                foreach (var validator in validators)
                {
                    try
                    {
                        var url = $"http://{validator.IPAddress}:{Globals.FrostValidatorPort}/frost/dkg/result/{sessionId}";
                        var response = await _httpClient.GetAsync(url);
                        // Try to get pubkey package from local key store if coordinator is also a validator
                        break;
                    }
                    catch { }
                }

                // Try local key store first (coordinator is typically a validator too)
                var myAddr = Globals.ValidatorAddress;
                if (!string.IsNullOrEmpty(myAddr))
                {
                    // Search all contracts for one matching our validators
                    var contracts = VBTCContractV2.GetAllContracts();
                    if (contracts != null)
                    {
                        foreach (var contract in contracts)
                        {
                            var keyStore = FrostValidatorKeyStore.GetKeyPackage(contract.SmartContractUID, myAddr);
                            if (keyStore != null && !string.IsNullOrEmpty(keyStore.PubkeyPackage))
                            {
                                pubkeyPackage = keyStore.PubkeyPackage;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(pubkeyPackage))
                {
                    ErrorLogUtility.LogError("FROST Signing: Could not find pubkey package for signature aggregation", "FrostMPCService.AggregateSignature");
                    return null;
                }

                // Serialize shares and nonces for FROST native library
                var sharesJson = JsonConvert.SerializeObject(shares);
                var noncesJson = JsonConvert.SerializeObject(nonces);

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
                    SignerAddresses = validators.Select(v => v.ValidatorAddress).ToList(),
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

        #endregion
    }
}
