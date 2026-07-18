using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.Privacy;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Privacy;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Text;
using BtcServices = ReserveBlockCore.Bitcoin.Services;
using VbtcBaseBridge = ReserveBlockCore.Bitcoin.Services.BaseBridgeService;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    /// <summary>
    /// vBTC (MPC-based tokenized Bitcoin) REST API.
    /// Parallel reimplementation of Bitcoin/Controllers/VBTCController.cs over the same
    /// service/data layer. Ceremony and pending-raw-tx state is v2-local: a ceremony
    /// initiated here must be consumed here (and vice versa for v1) — do not mix the
    /// v1 and v2 layers within a single mint or raw-tx flow.
    /// </summary>
    public class VbtcController : RestBaseController
    {
        #region v2-local ceremony / pending-tx state

        // Mirrors the v1 controller's in-memory stores. v1's CeremonyCleanupService only
        // prunes the v1 store, so this one is pruned opportunistically on every
        // ceremony-touching request with the same TTLs (1h active, 1h terminal).
        private static readonly ConcurrentDictionary<string, MPCCeremonyState> _ceremonies = new();
        private static readonly ConcurrentDictionary<string, Transaction> _pendingRawVbtcTxs = new();

        private const int MaxConcurrentCeremonies = 100;
        private const long ActiveCeremonyTtlSeconds = 3600;
        private const long TerminalCeremonyTtlSeconds = 3600;

        private static bool IsCeremonyTerminal(CeremonyStatus status) =>
            status == CeremonyStatus.Completed || status == CeremonyStatus.Failed || status == CeremonyStatus.TimedOut;

        private static void PruneStaleCeremonies()
        {
            var now = TimeUtil.GetTime();
            foreach (var kvp in _ceremonies)
            {
                var ceremony = kvp.Value;
                var age = now - ceremony.InitiatedTimestamp;

                if (IsCeremonyTerminal(ceremony.Status))
                {
                    if (age > TerminalCeremonyTtlSeconds)
                        _ceremonies.TryRemove(kvp.Key, out _);
                }
                else if (age > ActiveCeremonyTtlSeconds)
                {
                    ceremony.Status = CeremonyStatus.TimedOut;
                    ceremony.ErrorMessage = "Ceremony expired due to inactivity (1 hour TTL exceeded).";
                    ceremony.CompletedTimestamp = now;
                }
            }
        }

        private static async Task ExecuteCeremony(string ceremonyId)
        {
            try
            {
                if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                    return;

                ceremony.Status = CeremonyStatus.ValidatingValidators;
                ceremony.ProgressPercentage = 5;

                // S3C §7.1: route on the ceremony's pool, not global state.
                List<Bitcoin.Models.VBTCValidator> allValidators;
                try
                {
                    allValidators = ceremony.IsS3C
                        ? BtcServices.S3CService.GetValidatorsForCeremony()
                        : BtcServices.VBTCValidatorRegistry.GetPublicValidators();
                }
                catch (Exception s3cEx)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = s3cEx.Message;
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                if (allValidators == null || !allValidators.Any())
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = "No active validators available for vBTC V2 contract creation.";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                LogUtility.Log($"[MPC Ceremony] Found {allValidators.Count} registered validators. Probing FROST port reachability...",
                    "VbtcController.ExecuteCeremony");

                var activeValidators = await BtcServices.FrostMPCService.ProbeValidatorReachability(allValidators);

                if (activeValidators.Count < 3)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = $"Insufficient reachable validators for DKG ceremony. " +
                        $"Only {activeValidators.Count} of {allValidators.Count} registered validators are online on FROST port " +
                        $"(minimum 3 required).";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    LogUtility.Log($"[MPC Ceremony] {ceremony.ErrorMessage}", "VbtcController.ExecuteCeremony");
                    return;
                }

                LogUtility.Log($"[MPC Ceremony] Starting DKG with {activeValidators.Count} reachable validators " +
                    $"(out of {allValidators.Count} registered).", "VbtcController.ExecuteCeremony");

                ceremony.ProgressPercentage = 15;
                ceremony.Status = CeremonyStatus.Round1InProgress;

                var dkgResult = await BtcServices.FrostMPCService.CoordinateDKGCeremony(
                    ceremonyId,
                    ceremony.OwnerAddress,
                    activeValidators,
                    ceremony.RequiredThreshold,
                    (round, percentage) =>
                    {
                        ceremony.CurrentRound = round;
                        ceremony.ProgressPercentage = percentage;
                        if (round == 1 && ceremony.Status != CeremonyStatus.Round1InProgress)
                            ceremony.Status = CeremonyStatus.Round1InProgress;
                        else if (round == 2 && ceremony.Status != CeremonyStatus.Round2InProgress)
                            ceremony.Status = CeremonyStatus.Round2InProgress;
                        else if (round == 3 && ceremony.Status != CeremonyStatus.Round3InProgress)
                            ceremony.Status = CeremonyStatus.Round3InProgress;
                    }
                );

                if (dkgResult == null)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = "FROST DKG ceremony failed - unable to generate Taproot address";
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                    return;
                }

                ceremony.ValidatorSnapshot = dkgResult.ParticipantAddresses;
                LogUtility.Log($"[MPC Ceremony] Validator snapshot built from {dkgResult.ParticipantAddresses.Count} actual respondents " +
                    $"(out of {activeValidators.Count} candidates).", "VbtcController.ExecuteCeremony");

                ceremony.DepositAddress = dkgResult.TaprootAddress;
                ceremony.FrostGroupPublicKey = dkgResult.GroupPublicKey;
                ceremony.DKGProof = dkgResult.DKGProof;
                ceremony.ProofBlockHeight = Globals.LastBlock.Height;
                ceremony.Status = CeremonyStatus.Completed;
                ceremony.ProgressPercentage = 100;
                ceremony.CompletedTimestamp = TimeUtil.GetTime();
            }
            catch (Exception ex)
            {
                if (_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = ex.Message;
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                }
            }
        }

        #endregion

        #region Contracts (read)

        /// <summary>
        /// List vBTC contracts, optionally filtered by owner address
        /// </summary>
        [HttpGet("contracts")]
        public IActionResult GetContracts([FromQuery] string? owner)
        {
            var contracts = string.IsNullOrEmpty(owner)
                ? VBTCContractV2.GetAllContracts()
                : VBTCContractV2.GetContractsByOwner(owner);

            var contractList = contracts ?? new List<VBTCContractV2>();

            var enriched = contractList.Select(c =>
            {
                var scMain = SmartContractMain.SmartContractData.GetSmartContract(c.SmartContractUID);
                return new
                {
                    c.SmartContractUID,
                    c.OwnerAddress,
                    c.DepositAddress,
                    c.Balance,
                    c.ValidatorAddressesSnapshot,
                    c.FrostGroupPublicKey,
                    c.FrostPubkeyPackage,
                    c.RequiredThreshold,
                    c.DKGProof,
                    c.ProofBlockHeight,
                    c.LastValidatorActivityBlock,
                    c.TotalRegisteredValidators,
                    c.OriginalThreshold,
                    c.WithdrawalStatus,
                    c.ActiveWithdrawalBTCDestination,
                    c.ActiveWithdrawalAmount,
                    c.ActiveWithdrawalRequestHash,
                    c.WithdrawalRequestBlock,
                    c.ActiveWithdrawalFeeRate,
                    c.ActiveWithdrawalRequestTime,
                    c.WithdrawalHistory,
                    Name = scMain?.Name ?? "",
                    Description = scMain?.Description ?? "",
                };
            }).ToList();

            return Ok(new { Contracts = enriched });
        }

        /// <summary>
        /// Get vBTC contract details
        /// </summary>
        [HttpGet("contracts/{scUID}")]
        public IActionResult GetContract(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return Fail("NOT_FOUND", $"Contract not found: {scUID}", 404);

            return Ok(new { SmartContractUID = scUID, Contract = contract });
        }

        /// <summary>
        /// Get the MPC-generated BTC deposit address for a vBTC contract
        /// </summary>
        [HttpGet("contracts/{scUID}/deposit-address")]
        public IActionResult GetDepositAddress(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return Fail("NOT_FOUND", "vBTC V2 contract not found for the given scUID", 404);

            if (string.IsNullOrEmpty(contract.DepositAddress))
                return Fail("NO_DEPOSIT_ADDRESS", "Contract exists but deposit address has not been generated yet. DKG ceremony may not have completed.");

            return Ok(new
            {
                SmartContractUID = scUID,
                DepositAddress = contract.DepositAddress,
                FrostGroupPublicKey = contract.FrostGroupPublicKey ?? string.Empty,
                RequiredThreshold = contract.RequiredThreshold,
                DKGProof = contract.DKGProof ?? string.Empty
            });
        }

        /// <summary>
        /// Contract health: how many of the original DKG validators are still online,
        /// and whether FROST withdrawals can currently be processed
        /// </summary>
        [HttpGet("contracts/{scUID}/health")]
        public IActionResult GetContractHealth(string scUID)
        {
            // Resolve the ValidatorAddressesSnapshot: local DB first, then State Trei decompile.
            List<string>? originalValidators = null;
            int requiredThreshold = 67;
            long lastActivityBlock = 0;
            string? depositAddress = null;

            var vbtcContract = VBTCContractV2.GetContract(scUID);
            if (vbtcContract != null)
            {
                lastActivityBlock = vbtcContract.LastValidatorActivityBlock;
                depositAddress = vbtcContract.DepositAddress;
                requiredThreshold = vbtcContract.RequiredThreshold;
            }

            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState != null && !string.IsNullOrEmpty(scState.ContractData))
            {
                try
                {
                    var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                    if (scMainDecompile?.Features != null)
                    {
                        var tknzFeature = scMainDecompile.Features
                            .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                            .Select(x => x.FeatureFeatures)
                            .FirstOrDefault();

                        if (tknzFeature is TokenizationV2Feature tknz)
                        {
                            originalValidators = tknz.ValidatorAddressesSnapshot;
                            if (requiredThreshold <= 0) requiredThreshold = tknz.RequiredThreshold;
                            if (lastActivityBlock <= 0) lastActivityBlock = tknz.ProofBlockHeight;
                            if (string.IsNullOrEmpty(depositAddress)) depositAddress = tknz.DepositAddress;
                        }
                    }
                }
                catch (Exception decompileEx)
                {
                    ErrorLogUtility.LogError($"Failed to decompile contract {scUID} for health check: {decompileEx.Message}",
                        "VbtcController.GetContractHealth()");
                }
            }

            if (originalValidators == null || !originalValidators.Any())
                return Fail("NOT_FOUND",
                    "Could not resolve the original validator snapshot for this contract. " +
                    "The contract may not exist or may not have a TokenizationV2 feature.", 404);

            var activeValidators = BtcServices.VBTCValidatorRegistry.GetActiveValidators();
            var activeAddressSet = new HashSet<string>(
                activeValidators?.Select(v => v.ValidatorAddress) ?? Enumerable.Empty<string>());

            var validatorDetails = new List<object>();
            int onlineCount = 0;
            int offlineCount = 0;

            foreach (var addr in originalValidators)
            {
                bool isOnline = activeAddressSet.Contains(addr);
                long? lastHeartbeat = null;

                if (isOnline)
                {
                    onlineCount++;
                    var activeVal = activeValidators!.FirstOrDefault(v => v.ValidatorAddress == addr);
                    if (activeVal != null)
                        lastHeartbeat = activeVal.LastHeartbeatBlock;
                }
                else
                {
                    offlineCount++;
                }

                validatorDetails.Add(new
                {
                    Address = addr,
                    IsOnline = isOnline,
                    LastHeartbeatBlock = lastHeartbeat
                });
            }

            int totalOriginal = originalValidators.Count;
            decimal onlinePercentage = totalOriginal > 0
                ? Math.Round(((decimal)onlineCount / totalOriginal) * 100m, 2)
                : 0m;

            bool meetsThreshold = onlinePercentage >= requiredThreshold;
            long currentBlock = Globals.LastBlock.Height;

            string thresholdExplanation;
            int adjustedThreshold;
            int requiredValidatorCount;
            try
            {
                adjustedThreshold = BtcServices.VBTCThresholdCalculator.CalculateAdjustedThreshold(
                    totalOriginal, onlineCount, lastActivityBlock, currentBlock);
                requiredValidatorCount = BtcServices.VBTCThresholdCalculator.CalculateRequiredValidators(
                    adjustedThreshold, onlineCount);
                thresholdExplanation = BtcServices.VBTCThresholdCalculator.GetThresholdExplanation(
                    totalOriginal, onlineCount, lastActivityBlock, currentBlock);
            }
            catch
            {
                adjustedThreshold = requiredThreshold;
                requiredValidatorCount = (int)Math.Ceiling(onlineCount * (requiredThreshold / 100.0));
                thresholdExplanation = $"Original threshold: {requiredThreshold}%. Online: {onlineCount}/{totalOriginal} ({onlinePercentage}%).";
            }

            string healthStatus;
            if (onlinePercentage >= 80)
                healthStatus = "Excellent";
            else if (onlinePercentage >= 67)
                healthStatus = "Healthy";
            else if (onlinePercentage >= 50)
                healthStatus = "Degraded";
            else if (onlinePercentage > 0)
                healthStatus = "Critical";
            else
                healthStatus = "Offline";

            return Ok(new
            {
                SmartContractUID = scUID,
                DepositAddress = depositAddress,
                HealthStatus = healthStatus,
                TotalOriginalValidators = totalOriginal,
                OnlineValidators = onlineCount,
                OfflineValidators = offlineCount,
                OnlinePercentage = onlinePercentage,
                RequiredThreshold = requiredThreshold,
                AdjustedThreshold = adjustedThreshold,
                RequiredValidatorCount = requiredValidatorCount,
                CanProcessWithdrawals = onlineCount >= requiredValidatorCount,
                IsHealthy = meetsThreshold,
                CurrentBlockHeight = currentBlock,
                ScanWindow = BtcServices.VBTCValidatorRegistry.SCAN_WINDOW,
                ThresholdExplanation = thresholdExplanation,
                Validators = validatorDetails
            });
        }

        /// <summary>
        /// Withdrawal history for a contract
        /// </summary>
        [HttpGet("contracts/{scUID}/withdrawals")]
        public IActionResult GetWithdrawalHistory(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return Fail("NOT_FOUND", $"Contract not found: {scUID}", 404);

            return Ok(new
            {
                SmartContractUID = scUID,
                WithdrawalHistory = contract.WithdrawalHistory ?? new List<VBTCWithdrawalHistory>()
            });
        }

        /// <summary>
        /// Current withdrawal status for a contract
        /// </summary>
        [HttpGet("contracts/{scUID}/withdrawal-status")]
        public IActionResult GetWithdrawalStatus(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return Fail("NOT_FOUND", $"Contract not found: {scUID}", 404);

            var hasActiveWithdrawal = VBTCContractV2.HasActiveWithdrawal(scUID);

            return Ok(new
            {
                SmartContractUID = scUID,
                Status = contract.WithdrawalStatus.ToString(),
                HasActiveWithdrawal = hasActiveWithdrawal
            });
        }

        /// <summary>
        /// Ownership-transfer TX data for raw transaction building (web wallet flow).
        /// Locator comes from the beacon upload request endpoint.
        /// </summary>
        [HttpGet("contracts/{scUID}/ownership-transfer-data/{toAddress}/{**locator}")]
        public IActionResult GetOwnershipTransferData(string scUID, string toAddress, string locator)
        {
            toAddress = toAddress.ToAddressNormalize();

            var scStateTrei = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scStateTrei == null)
                return Fail("NOT_FOUND", "Smart contract state not found.", 404);

            var vbtcContract = VBTCContractV2.GetContract(scUID);
            if (vbtcContract == null)
                return Fail("NOT_FOUND", $"vBTC V2 contract not found: {scUID}", 404);

            var sc = SmartContractMain.GenerateSmartContractInMemory(scStateTrei.ContractData);
            if (sc == null)
                return Fail("DECOMPILE_FAILED", "Failed to generate smart contract from state data.");

            if (sc.Features == null)
                return Fail("NO_FEATURES", $"Contract has no features: {scUID}");

            var tknzFeature = sc.Features
                .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                .Select(x => x.FeatureFeatures)
                .FirstOrDefault();

            if (tknzFeature == null)
                return Fail("NO_FEATURES", $"Contract missing TokenizationV2 feature: {scUID}");

            decimal availableBalance = vbtcContract.Balance;
            if (scStateTrei.SCStateTreiTokenizationTXes != null)
            {
                var ownerTxes = scStateTrei.SCStateTreiTokenizationTXes
                    .Where(x => x.FromAddress == scStateTrei.OwnerAddress || x.ToAddress == scStateTrei.OwnerAddress)
                    .ToList();

                if (ownerTxes.Any())
                {
                    var ledgerDelta = ownerTxes.Sum(x => x.Amount);
                    availableBalance = vbtcContract.Balance + ledgerDelta;
                }
            }

            if (availableBalance <= 0)
                return Fail("ZERO_BALANCE", "Cannot transfer a token with zero balance.");

            var newSCInfo = new[]
            {
                new
                {
                    Function = "Transfer()",
                    ContractUID = sc.SmartContractUID,
                    ToAddress = toAddress,
                    Data = scStateTrei.ContractData,
                    Locators = locator,
                    MD5List = scStateTrei.MD5List
                }
            };

            return Ok(new { TxData = newSCInfo });
        }

        #endregion

        #region Balances

        /// <summary>
        /// All vBTC balances for an address across contracts
        /// </summary>
        [HttpGet("balances/{address}")]
        public async Task<IActionResult> GetAllBalances(string address)
        {
            var contractBalances = new List<object>();
            decimal totalBalance = 0.0M;

            var contracts = VBTCContractV2.GetContractsByOwner(address);
            if (contracts != null && contracts.Any())
            {
                foreach (var contract in contracts)
                {
                    var scState = SmartContractStateTrei.GetSmartContractState(contract.SmartContractUID);

                    decimal ledgerBalance = 0.0M;
                    int txCount = 0;

                    if (scState?.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                    {
                        var transactions = scState.SCStateTreiTokenizationTXes
                            .Where(x => x.FromAddress == address || x.ToAddress == address)
                            .ToList();

                        if (transactions.Any())
                        {
                            ledgerBalance = transactions.Sum(x => x.Amount);
                            txCount = transactions.Count;
                        }
                    }

                    bool isOwner = contract.OwnerAddress == address || scState?.OwnerAddress == address;
                    decimal depositBalance = 0.0M;

                    if (isOwner && !string.IsNullOrEmpty(contract.DepositAddress))
                    {
                        try
                        {
                            using var elxClient = await ReserveBlockCore.Bitcoin.Bitcoin.ElectrumXClient();
                            if (elxClient != null)
                            {
                                var balance = await elxClient.GetBalance(contract.DepositAddress, false);
                                depositBalance = balance.Confirmed / 100_000_000M;
                            }
                            else
                            {
                                depositBalance = contract.Balance;
                            }

                            if (contract.Balance != depositBalance)
                            {
                                contract.Balance = depositBalance;
                                VBTCContractV2.UpdateContract(contract);
                            }
                        }
                        catch
                        {
                            depositBalance = contract.Balance;
                        }
                    }

                    decimal contractBalance = isOwner ? depositBalance + ledgerBalance : ledgerBalance;

                    if (contractBalance > 0 || isOwner)
                    {
                        var pendingWithdrawals = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(address, contract.SmartContractUID);

                        contractBalances.Add(new
                        {
                            SmartContractUID = contract.SmartContractUID,
                            DepositAddress = contract.DepositAddress,
                            Balance = contractBalance,
                            DepositAddressBalance = isOwner ? depositBalance : (decimal?)null,
                            LedgerBalance = ledgerBalance,
                            AvailableBalance = contractBalance - pendingWithdrawals,
                            PendingWithdrawals = pendingWithdrawals,
                            TransactionCount = txCount,
                            IsOwner = isOwner,
                            WithdrawalStatus = contract.WithdrawalStatus.ToString()
                        });

                        totalBalance += contractBalance;
                    }
                }
            }

            return Ok(new
            {
                Address = address,
                TotalBalance = totalBalance,
                ContractCount = contractBalances.Count,
                Contracts = contractBalances
            });
        }

        /// <summary>
        /// vBTC balance for an address in a specific contract. Owner balance includes the
        /// live BTC deposit-address balance (ElectrumX) plus the tokenization ledger.
        /// </summary>
        [HttpGet("balances/{address}/{scUID}")]
        public async Task<IActionResult> GetBalance(string address, string scUID)
        {
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);
            if (scState == null)
                return Fail("NOT_FOUND", "Smart contract not found or no state available", 404);

            decimal ledgerBalance = 0.0M;
            bool isOwner = false;
            decimal depositBalance = 0.0M;

            // State entries use negative amounts for debits, so the net balance is simply the sum.
            if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
            {
                var transactions = scState.SCStateTreiTokenizationTXes
                    .Where(x => x.FromAddress == address || x.ToAddress == address)
                    .ToList();

                if (transactions.Any())
                    ledgerBalance = transactions.Sum(x => x.Amount);
            }

            var contract = VBTCContractV2.GetContract(scUID);
            if (contract != null && (contract.OwnerAddress == address || scState.OwnerAddress == address))
            {
                isOwner = true;

                if (!string.IsNullOrEmpty(contract.DepositAddress))
                {
                    try
                    {
                        using var client = await ReserveBlockCore.Bitcoin.Bitcoin.ElectrumXClient();
                        if (client != null)
                        {
                            var balance = await client.GetBalance(contract.DepositAddress, false);
                            depositBalance = balance.Confirmed / 100_000_000M;
                        }
                        else
                        {
                            depositBalance = contract.Balance;
                        }

                        if (contract.Balance != depositBalance)
                        {
                            contract.Balance = depositBalance;
                            VBTCContractV2.UpdateContract(contract);
                        }
                    }
                    catch (Exception elxEx)
                    {
                        depositBalance = contract.Balance;
                        ErrorLogUtility.LogError($"ElectrumX query failed, using cached balance: {elxEx.Message}", "VbtcController.GetBalance");
                    }
                }
            }

            decimal totalBalance = isOwner ? depositBalance + ledgerBalance : ledgerBalance;
            var pendingWithdrawals = VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(address, scUID);

            return Ok(new
            {
                Address = address,
                SmartContractUID = scUID,
                Balance = totalBalance,
                DepositAddressBalance = isOwner ? depositBalance : (decimal?)null,
                LedgerBalance = ledgerBalance,
                AvailableBalance = totalBalance - pendingWithdrawals,
                PendingWithdrawals = pendingWithdrawals,
                IsOwner = isOwner,
                TransactionCount = scState.SCStateTreiTokenizationTXes?.Count(x => x.FromAddress == address || x.ToAddress == address) ?? 0
            });
        }

        #endregion

        #region Validators (read)

        /// <summary>
        /// List registered vBTC validators
        /// </summary>
        [HttpGet("validators")]
        public IActionResult GetValidators([FromQuery] bool activeOnly = false)
        {
            // Parity note: v1 GetValidatorList ignores activeOnly and always returns the
            // active set; mirrored here so both layers report identically.
            var validators = activeOnly
                ? BtcServices.VBTCValidatorRegistry.GetActiveValidators()
                : BtcServices.VBTCValidatorRegistry.GetActiveValidators();

            return Ok(new { Validators = validators });
        }

        /// <summary>
        /// Validator status and details
        /// </summary>
        [HttpGet("validators/{validatorAddress}")]
        public IActionResult GetValidator(string validatorAddress)
        {
            var validator = BtcServices.VBTCValidatorRegistry.GetValidator(validatorAddress);
            if (validator == null)
                return Fail("NOT_FOUND", "Validator not found", 404);

            return Ok(new { Validator = validator });
        }

        #endregion

        #region Ceremonies

        /// <summary>
        /// Initiate an MPC (FROST DKG) ceremony to generate a deposit address.
        /// Runs in the background — poll GET ceremonies/{ceremonyId} for progress.
        /// </summary>
        [HttpPost("ceremonies")]
        public IActionResult InitiateCeremony([FromBody] InitiateCeremonyRequest request)
        {
            PruneStaleCeremonies();

            // S3C §2.1: if S3C= is configured but invalid, refuse to mint rather than silently
            // falling back to the public pool. forcePublic companions (§5) are exempt.
            if (!request.ForcePublic && Globals.S3CConfigInvalid)
                return Fail("S3C_CONFIG_INVALID", "S3C configuration is present but invalid; refusing to mint. Fix the S3C= config and restart.", 409);

            var existingActive = _ceremonies.Values.FirstOrDefault(c =>
                c.OwnerAddress == request.OwnerAddress && !IsCeremonyTerminal(c.Status));
            if (existingActive != null)
                return Fail("CEREMONY_ACTIVE",
                    $"You already have an active MPC ceremony in progress ({existingActive.CeremonyId}, " +
                    $"status {existingActive.Status}, {existingActive.ProgressPercentage}%). Complete or cancel it before starting a new one.", 409);

            var activeCeremonyCount = _ceremonies.Values.Count(c => !IsCeremonyTerminal(c.Status));
            if (activeCeremonyCount >= MaxConcurrentCeremonies)
                return Fail("CEREMONY_LIMIT", $"Maximum concurrent ceremony limit reached ({MaxConcurrentCeremonies}). Please try again later.", 429);

            var ceremonyId = Guid.NewGuid().ToString();
            var currentTime = TimeUtil.GetTime();

            var ceremony = new MPCCeremonyState
            {
                CeremonyId = ceremonyId,
                OwnerAddress = request.OwnerAddress,
                Status = CeremonyStatus.Initiated,
                InitiatedTimestamp = currentTime,
                RequiredThreshold = 51,
                ProgressPercentage = 0,
                CurrentRound = 0,
                // S3C §7.1: global config sets the default; companion creation forces public.
                IsS3C = request.ForcePublic ? false : Globals.UseS3C
            };

            if (!_ceremonies.TryAdd(ceremonyId, ceremony))
                return Fail("CEREMONY_CREATE_FAILED", "Failed to create ceremony", 500);

            _ = Task.Run(async () => await ExecuteCeremony(ceremonyId));

            return Created(new
            {
                Message = "MPC ceremony initiated successfully. Poll the ceremony status endpoint for progress.",
                CeremonyId = ceremonyId,
                Status = ceremony.Status.ToString(),
                InitiatedTimestamp = currentTime
            });
        }

        /// <summary>
        /// Status of an ongoing or completed MPC ceremony (v2-initiated ceremonies only)
        /// </summary>
        [HttpGet("ceremonies/{ceremonyId}")]
        public IActionResult GetCeremonyStatus(string ceremonyId)
        {
            PruneStaleCeremonies();

            if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                return Fail("NOT_FOUND", "Ceremony not found", 404);

            return Ok(new
            {
                CeremonyId = ceremony.CeremonyId,
                Status = ceremony.Status.ToString(),
                OwnerAddress = ceremony.OwnerAddress,
                ProgressPercentage = ceremony.ProgressPercentage,
                CurrentRound = ceremony.CurrentRound,
                InitiatedTimestamp = ceremony.InitiatedTimestamp,
                CompletedTimestamp = ceremony.CompletedTimestamp,
                ErrorMessage = ceremony.ErrorMessage,
                DepositAddress = ceremony.Status == CeremonyStatus.Completed ? ceremony.DepositAddress : null,
                FrostGroupPublicKey = ceremony.Status == CeremonyStatus.Completed ? ceremony.FrostGroupPublicKey : null,
                DKGProof = ceremony.Status == CeremonyStatus.Completed ? ceremony.DKGProof : null,
                ValidatorCount = ceremony.ValidatorSnapshot?.Count ?? 0,
                RequiredThreshold = ceremony.RequiredThreshold,
                ProofBlockHeight = ceremony.ProofBlockHeight
            });
        }

        /// <summary>
        /// Cancel an active MPC ceremony. Only the original owner can cancel.
        /// </summary>
        [HttpPost("ceremonies/{ceremonyId}/cancel")]
        public IActionResult CancelCeremony(string ceremonyId, [FromBody] CancelCeremonyRequest request)
        {
            if (!_ceremonies.TryGetValue(ceremonyId, out var ceremony))
                return Fail("NOT_FOUND", "Ceremony not found", 404);

            if (ceremony.OwnerAddress != request.OwnerAddress)
                return Fail("FORBIDDEN", "Only the ceremony owner can cancel it", 403);

            if (IsCeremonyTerminal(ceremony.Status))
            {
                _ceremonies.TryRemove(ceremonyId, out _);
                return Ok(new
                {
                    Message = $"Ceremony was already in terminal state ({ceremony.Status}). Removed from memory."
                });
            }

            ceremony.Status = CeremonyStatus.Failed;
            ceremony.ErrorMessage = "Ceremony cancelled by owner";
            ceremony.CompletedTimestamp = TimeUtil.GetTime();
            _ceremonies.TryRemove(ceremonyId, out _);

            return Ok(new
            {
                Message = "Ceremony cancelled and removed successfully",
                CeremonyId = ceremonyId,
                PreviousStatus = ceremony.Status.ToString()
            });
        }

        /// <summary>
        /// Prepare an MPC ceremony for web-wallet use: probes validators and returns the
        /// exact leader-auth messages the wallet must sign, then call execute-raw.
        /// </summary>
        [HttpPost("ceremonies/prepare-raw")]
        public async Task<IActionResult> PrepareCeremonyRaw([FromBody] PrepareCeremonyRawRequest request)
        {
            PruneStaleCeremonies();

            var existingActive = _ceremonies.Values.FirstOrDefault(c =>
                c.OwnerAddress == request.OwnerAddress && !IsCeremonyTerminal(c.Status));
            if (existingActive != null)
                return Fail("CEREMONY_ACTIVE", $"Active ceremony already in progress ({existingActive.CeremonyId}).", 409);

            // S3C §7.1: exclusion-only (public mints never use S3C validators)
            var allValidators = BtcServices.VBTCValidatorRegistry.GetPublicValidators();
            if (allValidators == null || !allValidators.Any())
                return Fail("NO_VALIDATORS", "No active validators available");

            var activeValidators = await BtcServices.FrostMPCService.ProbeValidatorReachability(allValidators);
            if (activeValidators.Count < 3)
                return Fail("INSUFFICIENT_VALIDATORS", $"Insufficient reachable validators ({activeValidators.Count}/{allValidators.Count})");

            var threshold = 51;
            var sessionId = Guid.NewGuid().ToString();
            var ceremonyId = Guid.NewGuid().ToString();
            var startTimestamp = TimeUtil.GetTime();
            var shareDistTimestamp = startTimestamp + 1;

            var startMessage = $"{sessionId}.{request.OwnerAddress}.{startTimestamp}";
            var shareDistMessage = $"{sessionId}.{request.OwnerAddress}.{shareDistTimestamp}";

            return Ok(new
            {
                Message = "Sign both LeaderAuthMessages with your private key (ECDSA secp256k1) and submit via the execute-raw endpoint.",
                CeremonyId = ceremonyId,
                SessionId = sessionId,
                OwnerAddress = request.OwnerAddress,
                ValidatorCount = activeValidators.Count,
                Threshold = threshold,
                StartMessage = startMessage,
                StartTimestamp = startTimestamp,
                ShareDistributionMessage = shareDistMessage,
                ShareDistributionTimestamp = shareDistTimestamp
            });
        }

        /// <summary>
        /// Execute an MPC ceremony using pre-signed leader authentication from prepare-raw
        /// </summary>
        [HttpPost("ceremonies/execute-raw")]
        public IActionResult ExecuteCeremonyRaw([FromBody] ExecuteCeremonyRawRequest request)
        {
            PruneStaleCeremonies();

            var startMessage = $"{request.SessionId}.{request.OwnerAddress}.{request.StartTimestamp}";
            if (!SignatureService.VerifySignature(request.OwnerAddress, startMessage, request.StartSignature))
                return Fail("INVALID_SIGNATURE", "Invalid start signature");

            var shareDistMessage = $"{request.SessionId}.{request.OwnerAddress}.{request.ShareDistributionTimestamp}";
            if (!SignatureService.VerifySignature(request.OwnerAddress, shareDistMessage, request.ShareDistributionSignature))
                return Fail("INVALID_SIGNATURE", "Invalid share distribution signature");

            var existingActive = _ceremonies.Values.FirstOrDefault(c =>
                c.OwnerAddress == request.OwnerAddress && !IsCeremonyTerminal(c.Status));
            if (existingActive != null)
                return Fail("CEREMONY_ACTIVE", $"Active ceremony already in progress ({existingActive.CeremonyId}).", 409);

            var activeCeremonyCount = _ceremonies.Values.Count(c => !IsCeremonyTerminal(c.Status));
            if (activeCeremonyCount >= MaxConcurrentCeremonies)
                return Fail("CEREMONY_LIMIT", "Maximum concurrent ceremony limit reached.", 429);

            var ceremony = new MPCCeremonyState
            {
                CeremonyId = request.CeremonyId,
                OwnerAddress = request.OwnerAddress,
                Status = CeremonyStatus.Initiated,
                InitiatedTimestamp = TimeUtil.GetTime(),
                RequiredThreshold = 51,
                ProgressPercentage = 0,
                CurrentRound = 0
            };

            if (!_ceremonies.TryAdd(request.CeremonyId, ceremony))
                return Fail("CEREMONY_CREATE_FAILED", "Failed to create ceremony", 500);

            var preSignedAuth = new ReserveBlockCore.Bitcoin.FROST.Models.PreSignedLeaderAuth
            {
                SessionId = request.SessionId,
                StartSignature = request.StartSignature,
                StartTimestamp = request.StartTimestamp,
                ShareDistributionSignature = request.ShareDistributionSignature,
                ShareDistributionTimestamp = request.ShareDistributionTimestamp
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    ceremony.Status = CeremonyStatus.ValidatingValidators;
                    ceremony.ProgressPercentage = 5;

                    // S3C §7.1: exclusion-only (public mints never use S3C validators)
                    var allValidators = BtcServices.VBTCValidatorRegistry.GetPublicValidators();
                    if (allValidators == null || !allValidators.Any())
                    {
                        ceremony.Status = CeremonyStatus.Failed;
                        ceremony.ErrorMessage = "No active validators available.";
                        ceremony.CompletedTimestamp = TimeUtil.GetTime();
                        return;
                    }

                    var activeValidators = await BtcServices.FrostMPCService.ProbeValidatorReachability(allValidators);
                    if (activeValidators.Count < 3)
                    {
                        ceremony.Status = CeremonyStatus.Failed;
                        ceremony.ErrorMessage = $"Insufficient reachable validators ({activeValidators.Count}).";
                        ceremony.CompletedTimestamp = TimeUtil.GetTime();
                        return;
                    }

                    ceremony.ProgressPercentage = 15;
                    ceremony.Status = CeremonyStatus.Round1InProgress;

                    var dkgResult = await BtcServices.FrostMPCService.CoordinateDKGCeremony(
                        request.CeremonyId,
                        request.OwnerAddress,
                        activeValidators,
                        ceremony.RequiredThreshold,
                        (round, percentage) =>
                        {
                            ceremony.CurrentRound = round;
                            ceremony.ProgressPercentage = percentage;
                            if (round == 1) ceremony.Status = CeremonyStatus.Round1InProgress;
                            else if (round == 2) ceremony.Status = CeremonyStatus.Round2InProgress;
                            else if (round == 3) ceremony.Status = CeremonyStatus.Round3InProgress;
                        },
                        preSignedAuth);

                    if (dkgResult == null)
                    {
                        ceremony.Status = CeremonyStatus.Failed;
                        ceremony.ErrorMessage = "FROST DKG ceremony failed";
                        ceremony.CompletedTimestamp = TimeUtil.GetTime();
                        return;
                    }

                    ceremony.ValidatorSnapshot = dkgResult.ParticipantAddresses;
                    ceremony.DepositAddress = dkgResult.TaprootAddress;
                    ceremony.FrostGroupPublicKey = dkgResult.GroupPublicKey;
                    ceremony.DKGProof = dkgResult.DKGProof;
                    ceremony.ProofBlockHeight = Globals.LastBlock.Height;
                    ceremony.Status = CeremonyStatus.Completed;
                    ceremony.ProgressPercentage = 100;
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                }
                catch (Exception ex)
                {
                    ceremony.Status = CeremonyStatus.Failed;
                    ceremony.ErrorMessage = ex.Message;
                    ceremony.CompletedTimestamp = TimeUtil.GetTime();
                }
            });

            return Created(new
            {
                Message = "MPC ceremony initiated with pre-signed auth. Poll the ceremony status endpoint for progress.",
                CeremonyId = request.CeremonyId,
                Status = ceremony.Status.ToString()
            });
        }

        #endregion

        #region Contract creation

        /// <summary>
        /// Create a vBTC contract from a completed (v2-initiated) MPC ceremony.
        /// Signs and broadcasts the mint transaction with the local wallet key.
        /// </summary>
        [HttpPost("contracts")]
        public async Task<IActionResult> CreateVbtcContract([FromBody] CreateVbtcContractRequest request)
        {
            if (!_ceremonies.TryGetValue(request.CeremonyId, out var ceremony))
                return Fail("NOT_FOUND", "Ceremony not found. Initiate a new ceremony first (ceremony state is v2-local).", 404);

            if (ceremony.Status != CeremonyStatus.Completed)
                return Fail("CEREMONY_INCOMPLETE",
                    $"Ceremony is not complete. Current status: {ceremony.Status} ({ceremony.ProgressPercentage}%).", 409);

            if (ceremony.OwnerAddress != request.OwnerAddress)
                return Fail("OWNER_MISMATCH", "Owner address does not match the ceremony owner address.", 403);

            var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            string depositAddress = ceremony.DepositAddress!;
            string frostGroupPublicKey = ceremony.FrostGroupPublicKey!;
            string dkgProof = ceremony.DKGProof!;
            var validatorSnapshot = ceremony.ValidatorSnapshot;

            // When the ceremony was delegated to a remote validator, validators stored their
            // FROST key packages under the remote ceremony ID — use it so signing finds the keys.
            var effectiveCeremonyId = ceremony.IsRemote && !string.IsNullOrEmpty(ceremony.RemoteCeremonyId)
                ? ceremony.RemoteCeremonyId
                : request.CeremonyId;

            if (effectiveCeremonyId != request.CeremonyId)
            {
                LogUtility.Log($"[FROST MPC] Using remote ceremony ID for contract. Local: {request.CeremonyId}, Remote (validators use): {effectiveCeremonyId}",
                    "VbtcController.CreateVbtcContract");
            }

            var tokenizationV2Feature = new TokenizationV2Feature
            {
                AssetName = request.Name,
                AssetTicker = request.Ticker ?? "vBTC",
                DepositAddress = depositAddress,
                Version = 2,
                ValidatorAddressesSnapshot = validatorSnapshot,
                FrostGroupPublicKey = frostGroupPublicKey,
                RequiredThreshold = 51,
                DKGProof = dkgProof,
                ProofBlockHeight = Globals.LastBlock.Height,
                CeremonyId = effectiveCeremonyId,
                ImageBase = request.ImageBase,
                // S3C §5.4/§7.1: persist the ceremony's pool choice + optional companion link
                // into the contract so it survives to every node via the state-trei seam.
                IsS3C = ceremony.IsS3C,
                LinkedContractUID = request.LinkedContractUID
            };

            var scMain = new SmartContractMain
            {
                SmartContractUID = scUID,
                Name = request.Name,
                Description = request.Description ?? "vBTC V2 Token - MPC-based Tokenized Bitcoin",
                MinterAddress = request.OwnerAddress,
                MinterName = request.OwnerAddress,
                IsPublic = true,
                SCVersion = Globals.SCVersion,
                IsMinter = true,
                IsPublished = false,
                IsToken = false,
                Id = 0,
                SmartContractAsset = new SmartContractAsset
                {
                    Name = "vbtc_v2_token",
                    Location = "default",
                    AssetAuthorName = request.OwnerAddress,
                    FileSize = 0
                },
                Features = new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.TokenizationV2,
                        FeatureFeatures = tokenizationV2Feature
                    }
                }
            };

            var result = await SmartContractWriterService.WriteSmartContract(scMain);
            if (string.IsNullOrWhiteSpace(result.Item1))
                return Fail("CONTRACT_GENERATION_FAILED", "Failed to generate smart contract code", 500);

            SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);
            await VBTCContractV2.SaveSmartContract(result.Item2, result.Item1, request.OwnerAddress);

            var scTx = await SmartContractService.MintSmartContractTx(result.Item2, TransactionType.VBTC_V2_CONTRACT_CREATE);
            if (scTx == null)
                return Fail("TX_FAILED", "Failed to create or broadcast smart contract transaction", 500);

            await TokenizedBitcoin.SetTokenContractIsPublished(scUID);

            // Ceremony results consumed — remove from memory immediately to free space
            _ceremonies.TryRemove(request.CeremonyId, out _);

            return Created(new
            {
                Message = "vBTC V2 contract created and published to blockchain successfully",
                SmartContractUID = scUID,
                TransactionHash = scTx.Hash,
                CeremonyId = request.CeremonyId,
                DepositAddress = depositAddress,
                FrostGroupPublicKey = frostGroupPublicKey,
                DKGProof = dkgProof,
                ValidatorCount = validatorSnapshot.Count,
                ProofBlockHeight = ceremony.ProofBlockHeight,
                RequiredThreshold = 51
            });
        }

        /// <summary>
        /// Create a vBTC contract from a pre-signed external request (signature +
        /// timestamp + replay protection). Still signs the mint TX with the local wallet key.
        /// </summary>
        [HttpPost("contracts/raw")]
        public async Task<IActionResult> CreateVbtcContractRaw([FromBody] CreateVbtcContractRawRequest request)
        {
            if (string.IsNullOrEmpty(request.UniqueId))
                return Fail("VALIDATION", "Unique ID cannot be null");

            if (string.IsNullOrEmpty(request.OwnerSignature))
                return Fail("VALIDATION", "Owner signature cannot be null");

            var currentTime = TimeUtil.GetTime();
            var timeDifference = Math.Abs(currentTime - request.Timestamp);
            if (timeDifference > 300)
                return Fail("STALE_REQUEST", $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)");

            var signatureData = $"{request.OwnerAddress}{request.Name}{request.Description}{request.Ticker}{request.CeremonyId}{request.Timestamp}{request.UniqueId}";
            var isValidSignature = SignatureService.VerifySignature(request.OwnerAddress, signatureData, request.OwnerSignature);
            if (!isValidSignature)
                return Fail("INVALID_SIGNATURE", "Invalid owner signature");

            if (!_ceremonies.TryGetValue(request.CeremonyId, out var ceremony))
                return Fail("NOT_FOUND", "Ceremony not found. Initiate a new ceremony first (ceremony state is v2-local).", 404);

            if (ceremony.Status != CeremonyStatus.Completed)
                return Fail("CEREMONY_INCOMPLETE",
                    $"Ceremony is not complete. Current status: {ceremony.Status} ({ceremony.ProgressPercentage}%).", 409);

            if (ceremony.OwnerAddress != request.OwnerAddress)
                return Fail("OWNER_MISMATCH", "Owner address does not match the ceremony owner address.", 403);

            var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            string depositAddress = ceremony.DepositAddress!;
            string frostGroupPublicKey = ceremony.FrostGroupPublicKey!;
            string dkgProof = ceremony.DKGProof!;
            var validatorSnapshot = ceremony.ValidatorSnapshot;

            var effectiveCeremonyId = ceremony.IsRemote && !string.IsNullOrEmpty(ceremony.RemoteCeremonyId)
                ? ceremony.RemoteCeremonyId
                : request.CeremonyId;

            if (effectiveCeremonyId != request.CeremonyId)
            {
                LogUtility.Log($"[FROST MPC] Using remote ceremony ID for raw contract. Local: {request.CeremonyId}, Remote (validators use): {effectiveCeremonyId}",
                    "VbtcController.CreateVbtcContractRaw");
            }

            var tokenizationV2Feature = new TokenizationV2Feature
            {
                AssetName = request.Name,
                AssetTicker = request.Ticker ?? "vBTC",
                DepositAddress = depositAddress,
                Version = 2,
                ValidatorAddressesSnapshot = validatorSnapshot,
                FrostGroupPublicKey = frostGroupPublicKey,
                RequiredThreshold = 51,
                DKGProof = dkgProof,
                ProofBlockHeight = Globals.LastBlock.Height,
                CeremonyId = effectiveCeremonyId,
                ImageBase = request.ImageBase,
                IsS3C = ceremony.IsS3C,
                LinkedContractUID = request.LinkedContractUID
            };

            var scMain = new SmartContractMain
            {
                SmartContractUID = scUID,
                Name = request.Name,
                Description = request.Description,
                MinterAddress = request.OwnerAddress,
                MinterName = request.OwnerAddress,
                SmartContractAsset = new SmartContractAsset
                {
                    Name = "vbtc_v2_token",
                    Location = "default",
                    AssetAuthorName = request.OwnerAddress,
                    FileSize = 0
                },
                Features = new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.TokenizationV2,
                        FeatureFeatures = tokenizationV2Feature
                    }
                }
            };

            var result = await SmartContractWriterService.WriteSmartContract(scMain);
            if (string.IsNullOrWhiteSpace(result.Item1))
                return Fail("CONTRACT_GENERATION_FAILED", "Failed to generate smart contract code", 500);

            SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);
            await VBTCContractV2.SaveSmartContract(result.Item2, result.Item1, request.OwnerAddress);

            var scTx = await SmartContractService.MintSmartContractTx(result.Item2, TransactionType.VBTC_V2_CONTRACT_CREATE);
            if (scTx == null)
                return Fail("TX_FAILED", "Failed to create or broadcast smart contract transaction", 500);

            _ceremonies.TryRemove(request.CeremonyId, out _);

            return Created(new
            {
                Message = "vBTC V2 contract created and published to blockchain successfully via raw request",
                SmartContractUID = scUID,
                TransactionHash = scTx.Hash,
                CeremonyId = request.CeremonyId,
                DepositAddress = depositAddress,
                DKGProof = dkgProof,
                ValidatorCount = validatorSnapshot.Count,
                ProofBlockHeight = ceremony.ProofBlockHeight,
                UniqueId = request.UniqueId,
                Timestamp = currentTime
            });
        }

        /// <summary>
        /// Build an unsigned VBTC_V2_CONTRACT_CREATE transaction for offline signing.
        /// Requires a completed (v2-initiated) MPC ceremony.
        /// </summary>
        [HttpPost("contracts/raw-tx")]
        public async Task<IActionResult> BuildCreateContractTx([FromBody] CreateVbtcContractRawRequest request)
        {
            if (!string.IsNullOrEmpty(request.OwnerSignature) && !string.IsNullOrEmpty(request.UniqueId))
            {
                var signatureData = $"{request.OwnerAddress}{request.Name}{request.Description}{request.Ticker}{request.CeremonyId}{request.Timestamp}{request.UniqueId}";
                if (!SignatureService.VerifySignature(request.OwnerAddress, signatureData, request.OwnerSignature))
                    return Fail("INVALID_SIGNATURE", "Invalid owner signature");
            }

            if (!_ceremonies.TryGetValue(request.CeremonyId, out var ceremony))
                return Fail("NOT_FOUND", "Ceremony not found (ceremony state is v2-local).", 404);

            if (ceremony.Status != CeremonyStatus.Completed)
                return Fail("CEREMONY_INCOMPLETE", $"Ceremony not complete. Status: {ceremony.Status}", 409);

            if (ceremony.OwnerAddress != request.OwnerAddress)
                return Fail("OWNER_MISMATCH", "Owner address mismatch.", 403);

            var effectiveCeremonyId = ceremony.IsRemote && !string.IsNullOrEmpty(ceremony.RemoteCeremonyId)
                ? ceremony.RemoteCeremonyId : request.CeremonyId;

            var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            var tokenizationV2Feature = new TokenizationV2Feature
            {
                AssetName = request.Name,
                AssetTicker = request.Ticker ?? "vBTC",
                DepositAddress = ceremony.DepositAddress!,
                Version = 2,
                ValidatorAddressesSnapshot = ceremony.ValidatorSnapshot,
                FrostGroupPublicKey = ceremony.FrostGroupPublicKey!,
                RequiredThreshold = 51,
                DKGProof = ceremony.DKGProof!,
                ProofBlockHeight = Globals.LastBlock.Height,
                CeremonyId = effectiveCeremonyId,
                ImageBase = request.ImageBase,
                IsS3C = ceremony.IsS3C,
                LinkedContractUID = request.LinkedContractUID
            };

            var scMain = new SmartContractMain
            {
                SmartContractUID = scUID,
                Name = request.Name,
                Description = request.Description,
                MinterAddress = request.OwnerAddress,
                MinterName = request.OwnerAddress,
                SCVersion = Globals.SCVersion,
                SmartContractAsset = new SmartContractAsset
                {
                    Name = "vbtc_v2_token",
                    Location = "default",
                    AssetAuthorName = request.OwnerAddress,
                    FileSize = 0
                },
                Features = new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.TokenizationV2,
                        FeatureFeatures = tokenizationV2Feature
                    }
                }
            };

            var result = await SmartContractWriterService.WriteSmartContract(scMain);
            if (string.IsNullOrWhiteSpace(result.Item1))
                return Fail("CONTRACT_GENERATION_FAILED", "Failed to generate smart contract code", 500);

            SmartContractMain.SmartContractData.SaveSmartContract(result.Item2, result.Item1);
            await VBTCContractV2.SaveSmartContract(result.Item2, result.Item1, request.OwnerAddress);

            // Build unsigned deploy TX (compress + Base64 the Trillium code, same as NFT mint pipeline)
            var bytes = Encoding.Unicode.GetBytes(result.Item1);
            var scBase64 = bytes.ToCompress().ToBase64();
            var defaultMD5 = "defaultvBTC.png::150b90aa9d06f7e4fc5703ca6d7f01db";
            string? md5List = null;
            if (scMain.SmartContractAsset.Location != "default")
                md5List = await MD5Utility.GetMD5FromSmartContract(scMain);
            else
                md5List = defaultMD5;

            var txData = JsonConvert.SerializeObject(new[]
            {
                new { Function = "Mint()", ContractUID = scUID, Data = scBase64, MD5List = md5List }
            });

            var deployTx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = request.OwnerAddress,
                ToAddress = request.OwnerAddress,
                Amount = 0.0M,
                Fee = 0.0M,
                Nonce = AccountStateTrei.GetNextNonce(request.OwnerAddress),
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = txData
            };

            deployTx.Fee = FeeCalcService.CalculateTXFee(deployTx);
            deployTx.Build();

            _pendingRawVbtcTxs[deployTx.Hash] = deployTx;

            // Ceremony is not consumed until the signed TX is submitted.
            return Ok(new
            {
                Hash = deployTx.Hash,
                SmartContractUID = scUID,
                DepositAddress = ceremony.DepositAddress,
                CeremonyId = request.CeremonyId,
                Timestamp = deployTx.Timestamp,
                Fee = deployTx.Fee,
                Nonce = deployTx.Nonce,
                Message = "Sign the Hash field and submit via the raw-tx send endpoint."
            });
        }

        /// <summary>
        /// Submit a pre-signed VBTC_V2_CONTRACT_CREATE transaction
        /// </summary>
        [HttpPost("contracts/raw-tx/send")]
        public async Task<IActionResult> SendCreateContractTx([FromBody] RawVbtcTxSubmissionRequest body)
        {
            if (!_pendingRawVbtcTxs.TryRemove(body.Hash, out var pendingTx))
                return Fail("NOT_FOUND", $"No pending transaction found for hash {body.Hash}.", 404);

            pendingTx.Signature = body.Signature;

            var (valid, reason) = await TransactionValidatorService.VerifyTX(pendingTx);
            if (!valid)
                return Fail("VERIFICATION_FAILED", $"Transaction verification failed: {reason}");

            await TransactionData.AddTxToWallet(pendingTx, true);
            await AccountData.UpdateLocalBalance(pendingTx.FromAddress, pendingTx.Fee + pendingTx.Amount);
            await TransactionData.AddToPool(pendingTx);
            await P2PClient.SendTXMempool(pendingTx);

            try
            {
                var txDataArr = JsonConvert.DeserializeObject<dynamic[]>(pendingTx.Data);
                if (txDataArr != null && txDataArr.Length > 0)
                {
                    string contractUID = txDataArr[0].ContractUID;
                    if (!string.IsNullOrEmpty(contractUID))
                        await TokenizedBitcoin.SetTokenContractIsPublished(contractUID);
                }
            }
            catch { /* Best-effort publish marking */ }

            return Created(new { Hash = pendingTx.Hash, Message = "vBTC contract creation transaction broadcast successfully." });
        }

        #endregion

        #region Transfers

        /// <summary>
        /// Transfer vBTC (signs and broadcasts with the local wallet)
        /// </summary>
        [HttpPost("transfers")]
        public async Task<IActionResult> TransferVbtc([FromBody] VbtcTransferRequest request)
        {
            var result = await BtcServices.VBTCService.TransferVBTC(
                request.SmartContractUID,
                request.FromAddress,
                request.ToAddress,
                request.Amount);

            if (!result.Item1)
                return Fail("TRANSFER_FAILED", result.Item2);

            return Created(new
            {
                Message = "vBTC V2 transfer transaction created and broadcast successfully",
                TransactionHash = result.Item2,
                From = request.FromAddress,
                To = request.ToAddress,
                Amount = request.Amount,
                SmartContractUID = request.SmartContractUID
            });
        }

        /// <summary>
        /// Build an unsigned VBTC_V2_TRANSFER transaction for offline signing
        /// </summary>
        [HttpPost("transfers/raw-tx")]
        public async Task<IActionResult> BuildTransferTx([FromBody] VbtcTransferRequest request)
        {
            var balResult = await BtcServices.VBTCService.TryGetAvailableTransparentVbtcBalance(request.SmartContractUID, request.FromAddress);
            if (!balResult.success)
                return Fail("BALANCE_LOOKUP_FAILED", balResult.error ?? "Balance lookup failed");

            if (balResult.availableBalance < request.Amount)
                return Fail("INSUFFICIENT_BALANCE", $"Insufficient balance. Available: {balResult.availableBalance}, Requested: {request.Amount}");

            var toAddress = request.ToAddress.ToAddressNormalize();

            var txData = JsonConvert.SerializeObject(new
            {
                Function = "TransferVBTCV2()",
                ContractUID = request.SmartContractUID,
                FromAddress = request.FromAddress,
                ToAddress = toAddress,
                Amount = request.Amount
            });

            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = request.FromAddress,
                ToAddress = toAddress,
                Amount = 0.0M,
                Fee = 0.0M,
                Nonce = AccountStateTrei.GetNextNonce(request.FromAddress),
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = txData
            };

            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();

            _pendingRawVbtcTxs[tx.Hash] = tx;

            return Ok(new
            {
                Hash = tx.Hash,
                Timestamp = tx.Timestamp,
                FromAddress = tx.FromAddress,
                ToAddress = tx.ToAddress,
                Amount = tx.Amount,
                Fee = tx.Fee,
                Nonce = tx.Nonce,
                TransactionType = tx.TransactionType.ToString(),
                Message = "Sign the Hash field with your private key (ECDSA secp256k1, UTF-8 encoded hash string) and submit via the raw-tx send endpoint."
            });
        }

        /// <summary>
        /// Submit a pre-signed VBTC_V2_TRANSFER transaction
        /// </summary>
        [HttpPost("transfers/raw-tx/send")]
        public async Task<IActionResult> SendTransferTx([FromBody] RawVbtcTxSubmissionRequest body)
        {
            if (!_pendingRawVbtcTxs.TryRemove(body.Hash, out var pendingTx))
                return Fail("NOT_FOUND", $"No pending transaction found for hash {body.Hash}. Build the transfer TX first.", 404);

            pendingTx.Signature = body.Signature;

            var (valid, reason) = await TransactionValidatorService.VerifyTX(pendingTx);
            if (!valid)
                return Fail("VERIFICATION_FAILED", $"Transaction verification failed: {reason}");

            await TransactionData.AddTxToWallet(pendingTx, true);
            await AccountData.UpdateLocalBalance(pendingTx.FromAddress, pendingTx.Fee + pendingTx.Amount);
            await TransactionData.AddToPool(pendingTx);
            await P2PClient.SendTXMempool(pendingTx);

            return Created(new { Hash = pendingTx.Hash, Message = "vBTC transfer transaction broadcast successfully." });
        }

        /// <summary>
        /// Transfer ownership of a vBTC contract to another address
        /// </summary>
        [HttpPost("contracts/{scUID}/transfer-ownership")]
        public async Task<IActionResult> TransferOwnership(string scUID, [FromBody] VbtcTransferOwnershipRequest request)
        {
            var raw = await BtcServices.VBTCService.TransferOwnership(scUID, request.ToAddress);

            // The service returns a v1-style JSON string; unwrap it into the v2 envelope.
            JObject parsed;
            try
            {
                parsed = JObject.Parse(raw);
            }
            catch
            {
                return Fail("TRANSFER_OWNERSHIP_FAILED", raw);
            }

            var success = parsed.Value<bool?>("Success") ?? false;
            if (!success)
                return Fail("TRANSFER_OWNERSHIP_FAILED", parsed.Value<string>("Message") ?? raw);

            return Created((object)parsed);
        }

        #endregion

        #region Withdrawals

        /// <summary>
        /// Request withdrawal of vBTC to a Bitcoin address (signs with the local wallet)
        /// </summary>
        [HttpPost("withdrawals")]
        public async Task<IActionResult> RequestVbtcWithdrawal([FromBody] VbtcWithdrawalRequestModel request)
        {
            var result = await BtcServices.VBTCService.RequestWithdrawal(
                request.SmartContractUID,
                request.RequestorAddress,
                request.BTCAddress,
                request.Amount,
                request.FeeRate);

            if (!result.Item1)
                return Fail("WITHDRAWAL_REQUEST_FAILED", result.Item2);

            return Created(new
            {
                Message = "vBTC V2 withdrawal request created successfully",
                RequestHash = result.Item2,
                SmartContractUID = request.SmartContractUID,
                Amount = request.Amount,
                Destination = request.BTCAddress,
                FeeRate = request.FeeRate,
                Status = "Requested"
            });
        }

        /// <summary>
        /// Complete a withdrawal: coordinates FROST MPC signing and broadcasts the BTC transaction
        /// </summary>
        [HttpPost("withdrawals/complete")]
        public async Task<IActionResult> CompleteVbtcWithdrawal([FromBody] VbtcCompleteWithdrawalRequest request)
        {
            var result = await BtcServices.VBTCService.CompleteWithdrawal(
                request.SmartContractUID,
                request.WithdrawalRequestHash);

            if (!result.Success)
                return Fail("WITHDRAWAL_COMPLETE_FAILED", result.ErrorMessage ?? "Withdrawal completion failed");

            return Created(new
            {
                Message = "vBTC V2 withdrawal completed successfully with FROST signing",
                VFXTransactionHash = result.VFXTxHash,
                BTCTransactionHash = result.BTCTxHash,
                Status = "Pending_BTC",
                SmartContractUID = request.SmartContractUID
            });
        }

        /// <summary>
        /// Request cancellation of a failed withdrawal (creates a validator-voted cancellation record)
        /// </summary>
        [HttpPost("withdrawals/cancel")]
        public IActionResult CancelVbtcWithdrawal([FromBody] VbtcCancelWithdrawalRequest request)
        {
            var cancellationUID = Guid.NewGuid().ToString();

            var cancellation = new VBTCWithdrawalCancellation
            {
                CancellationUID = cancellationUID,
                SmartContractUID = request.SmartContractUID,
                OwnerAddress = request.OwnerAddress,
                WithdrawalRequestHash = request.WithdrawalRequestHash,
                BTCTxHash = request.BTCTxHash,
                FailureProof = request.FailureProof,
                RequestTime = TimeUtil.GetTime(),
                ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = 0,
                RejectCount = 0,
                IsApproved = false,
                IsProcessed = false
            };

            VBTCWithdrawalCancellation.SaveCancellation(cancellation);

            return Created(new
            {
                Message = "Cancellation request created. Awaiting validator votes (75% required).",
                CancellationUID = cancellationUID,
                RequiredVotes = "75%"
            });
        }

        /// <summary>
        /// Cancel an active withdrawal by broadcasting a VBTC_V2_WITHDRAWAL_CANCEL
        /// transaction signed with the local wallet (distinct from the validator-voted
        /// cancellation record flow at withdrawals/cancel)
        /// </summary>
        [HttpPost("withdrawals/cancel-tx")]
        public async Task<IActionResult> CancelVbtcWithdrawalTx([FromBody] VbtcCancelWithdrawalTxRequest request)
        {
            var result = await BtcServices.VBTCService.CancelWithdrawal(
                request.SmartContractUID, request.RequestorAddress, request.WithdrawalRequestHash);

            if (!result.Item1)
                return Fail("WITHDRAWAL_CANCEL_FAILED", result.Item2);

            return Created(new { Message = result.Item2, SmartContractUID = request.SmartContractUID });
        }

        /// <summary>
        /// Request withdrawal with a pre-signed external request (signature, timestamp,
        /// and replay protection). Saves the request without a local wallet key.
        /// </summary>
        [HttpPost("withdrawals/raw")]
        public IActionResult RequestWithdrawalRaw([FromBody] VbtcWithdrawalRawRequest request)
        {
            var currentTime = TimeUtil.GetTime();
            var timeDifference = Math.Abs(currentTime - request.Timestamp);
            if (timeDifference > 300)
                return Fail("STALE_REQUEST", $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)");

            var existingRequest = VBTCWithdrawalRequest.GetByUniqueId(request.VFXAddress, request.UniqueId, request.SmartContractUID);
            if (existingRequest != null)
                return Fail("DUPLICATE_REQUEST", "Duplicate request detected. This UniqueId has already been processed.", 409);

            // S3C §0: per-CONTRACT active-withdrawal gate (anti-grief expiry inside the check).
            var hasActive = VBTCWithdrawalRequest.HasActiveContractRequest(request.SmartContractUID, Globals.LastBlock?.Height ?? 0);
            if (hasActive)
                return Fail("WITHDRAWAL_ACTIVE", "A withdrawal is already in progress for this contract; try again once it completes.", 409);

            var signatureData = $"{request.VFXAddress}{request.BTCAddress}{request.SmartContractUID}{request.Amount}{request.FeeRate}{request.Timestamp}{request.UniqueId}";
            var isValidSignature = SignatureService.VerifySignature(request.VFXAddress, signatureData, request.VFXSignature);
            if (!isValidSignature)
                return Fail("INVALID_SIGNATURE", "Invalid VFX signature");

            var scState = SmartContractStateTrei.GetSmartContractState(request.SmartContractUID);
            if (scState != null && scState.SCStateTreiTokenizationTXes != null)
            {
                var tokenTxs = scState.SCStateTreiTokenizationTXes
                    .Where(x => x.FromAddress == request.VFXAddress || x.ToAddress == request.VFXAddress).ToList();
                var vbtcBalance = tokenTxs.Sum(x => x.Amount);
                if (request.Amount > vbtcBalance)
                    return Fail("INSUFFICIENT_BALANCE", $"Insufficient vBTC balance. Available: {vbtcBalance}, Requested: {request.Amount}");
            }
            else
            {
                return Fail("NOT_FOUND", "Smart contract state not found or no tokenization transactions exist.", 404);
            }

            var withdrawalRequest = new VBTCWithdrawalRequest
            {
                RequestorAddress = request.VFXAddress,
                OriginalRequestTime = request.Timestamp,
                OriginalSignature = request.VFXSignature,
                OriginalUniqueId = request.UniqueId,
                Timestamp = currentTime,
                SmartContractUID = request.SmartContractUID,
                Amount = request.Amount,
                BTCDestination = request.BTCAddress,
                FeeRate = request.FeeRate,
                TransactionHash = "",
                IsCompleted = false,
                Status = VBTCWithdrawalStatus.Requested,
                // S3C §0: local fast-feedback height; corrected to the true mined height by
                // StateData when the request TX is processed into a block.
                RequestBlockHeight = Globals.LastBlock?.Height ?? 0
            };

            var saved = VBTCWithdrawalRequest.Save(withdrawalRequest);
            if (!saved)
                return Fail("SAVE_FAILED", "Failed to save withdrawal request to database", 500);

            var requestHash = $"{request.VFXAddress.Substring(0, 8)}_{request.UniqueId.Substring(0, 8)}_{currentTime}";

            VBTCContractV2.UpdateWithdrawalStatus(request.SmartContractUID, VBTCWithdrawalStatus.Requested,
                btcDestination: request.BTCAddress, amount: request.Amount, requestHash: requestHash);

            return Created(new
            {
                Message = "Raw withdrawal request created successfully",
                RequestHash = requestHash,
                SmartContractUID = request.SmartContractUID,
                Amount = request.Amount,
                Destination = request.BTCAddress,
                Status = "Requested",
                UniqueId = request.UniqueId,
                Timestamp = currentTime
            });
        }

        /// <summary>
        /// Cancel a withdrawal with pre-signed owner authorization (validator-voted)
        /// </summary>
        [HttpPost("withdrawals/cancel-raw")]
        public IActionResult CancelWithdrawalRaw([FromBody] VbtcCancelWithdrawalRawRequest request)
        {
            var currentTime = TimeUtil.GetTime();
            var timeDifference = Math.Abs(currentTime - request.Timestamp);
            if (timeDifference > 300)
                return Fail("STALE_REQUEST", $"Request timestamp is too old. Difference: {timeDifference} seconds (max 300)");

            var signatureData = $"{request.SmartContractUID}{request.OwnerAddress}{request.WithdrawalRequestHash}{request.BTCTxHash}{request.FailureProof}{request.Timestamp}{request.UniqueId}";
            var isValidSignature = SignatureService.VerifySignature(request.OwnerAddress, signatureData, request.OwnerSignature);
            if (!isValidSignature)
                return Fail("INVALID_SIGNATURE", "Invalid owner signature");

            var contract = VBTCContractV2.GetContract(request.SmartContractUID);
            if (contract == null || contract.OwnerAddress != request.OwnerAddress)
                return Fail("FORBIDDEN", "Only the contract owner can request cancellation", 403);

            var cancellationUID = Guid.NewGuid().ToString();

            var cancellation = new VBTCWithdrawalCancellation
            {
                CancellationUID = cancellationUID,
                SmartContractUID = request.SmartContractUID,
                OwnerAddress = request.OwnerAddress,
                WithdrawalRequestHash = request.WithdrawalRequestHash,
                BTCTxHash = request.BTCTxHash,
                FailureProof = request.FailureProof,
                RequestTime = currentTime,
                ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = 0,
                RejectCount = 0,
                IsApproved = false,
                IsProcessed = false
            };

            VBTCWithdrawalCancellation.SaveCancellation(cancellation);

            return Created(new
            {
                Message = "Raw cancellation request created successfully. Awaiting validator votes (75% required).",
                CancellationUID = cancellationUID,
                SmartContractUID = request.SmartContractUID,
                WithdrawalRequestHash = request.WithdrawalRequestHash,
                RequiredVotes = "75%",
                UniqueId = request.UniqueId,
                Timestamp = currentTime
            });
        }

        /// <summary>
        /// Prepare a complete-withdrawal FROST signing session for a web wallet.
        /// Returns the exact leader-auth messages the wallet must sign; then call complete-raw/execute.
        /// </summary>
        [HttpPost("withdrawals/complete-raw/prepare")]
        public IActionResult PrepareCompleteWithdrawalRaw([FromBody] PrepareCompleteWithdrawalRequest request)
        {
            var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(request.WithdrawalRequestHash);
            decimal amount = 0;
            string btcDestination = "";
            int feeRate = 10;

            if (withdrawalRequest != null)
            {
                if (withdrawalRequest.IsCompleted)
                    return Fail("ALREADY_COMPLETED", "Withdrawal request already completed", 409);

                amount = withdrawalRequest.Amount;
                btcDestination = withdrawalRequest.BTCDestination ?? "";
                feeRate = withdrawalRequest.FeeRate != 0 ? withdrawalRequest.FeeRate : 10;
            }

            var sessionId = Guid.NewGuid().ToString();
            var startTimestamp = TimeUtil.GetTime();
            var shareDistTimestamp = startTimestamp + 1;

            var startMessage = $"{sessionId}.{request.OwnerAddress}.{startTimestamp}";
            var shareDistMessage = $"{sessionId}.{request.OwnerAddress}.{shareDistTimestamp}";

            return Ok(new
            {
                Message = "Sign both LeaderAuthMessages with your private key (ECDSA secp256k1) and submit via the complete-raw execute endpoint.",
                SessionId = sessionId,
                OwnerAddress = request.OwnerAddress,
                SmartContractUID = request.SmartContractUID,
                WithdrawalRequestHash = request.WithdrawalRequestHash,
                Amount = amount,
                BTCDestination = btcDestination,
                FeeRate = feeRate,
                StartMessage = startMessage,
                StartTimestamp = startTimestamp,
                ShareDistributionMessage = shareDistMessage,
                ShareDistributionTimestamp = shareDistTimestamp
            });
        }

        /// <summary>
        /// Execute a complete-withdrawal using pre-signed leader authentication.
        /// Returns the FROST-signed BTC transaction hex for the wallet to broadcast.
        /// </summary>
        [HttpPost("withdrawals/complete-raw/execute")]
        public async Task<IActionResult> ExecuteCompleteWithdrawalRaw([FromBody] ExecuteCompleteWithdrawalRequest request)
        {
            var startMessage = $"{request.SessionId}.{request.OwnerAddress}.{request.StartTimestamp}";
            if (!SignatureService.VerifySignature(request.OwnerAddress, startMessage, request.StartSignature))
                return Fail("INVALID_SIGNATURE", "Invalid start signature");

            var shareDistMessage = $"{request.SessionId}.{request.OwnerAddress}.{request.ShareDistributionTimestamp}";
            if (!SignatureService.VerifySignature(request.OwnerAddress, shareDistMessage, request.ShareDistributionSignature))
                return Fail("INVALID_SIGNATURE", "Invalid share distribution signature");

            var currentTime = TimeUtil.GetTime();
            var timeDifference = Math.Abs(currentTime - request.StartTimestamp);
            if (timeDifference > 300)
                return Fail("STALE_REQUEST", $"Session timestamp is too old. Difference: {timeDifference}s (max 300)");

            var preSignedAuth = new ReserveBlockCore.Bitcoin.FROST.Models.PreSignedLeaderAuth
            {
                SessionId = request.SessionId,
                StartSignature = request.StartSignature,
                StartTimestamp = request.StartTimestamp,
                ShareDistributionSignature = request.ShareDistributionSignature,
                ShareDistributionTimestamp = request.ShareDistributionTimestamp
            };

            var withdrawalResult = await BtcServices.VBTCService.CompleteWithdrawal(
                request.SmartContractUID,
                request.WithdrawalRequestHash,
                delegatedAmount: request.Amount > 0 ? request.Amount : null,
                delegatedBTCDestination: !string.IsNullOrEmpty(request.BTCDestination) ? request.BTCDestination : null,
                delegatedFeeRate: request.FeeRate > 0 ? request.FeeRate : null,
                signOnly: true,
                preSignedAuth: preSignedAuth);

            if (!withdrawalResult.Success)
                return Fail("FROST_SIGNING_FAILED", withdrawalResult.ErrorMessage ?? "FROST signing ceremony failed");

            return Ok(new
            {
                Message = "FROST signing successful. Broadcast the SignedBTCTxHex to the Bitcoin network.",
                SignedBTCTxHex = withdrawalResult.BTCTxHash, // In signOnly mode, BTCTxHash contains the signed hex
                SmartContractUID = request.SmartContractUID,
                WithdrawalRequestHash = request.WithdrawalRequestHash
            });
        }

        /// <summary>
        /// Build an unsigned VBTC_V2_WITHDRAWAL_REQUEST blockchain transaction for offline signing
        /// </summary>
        [HttpPost("withdrawals/request-raw-tx")]
        public async Task<IActionResult> BuildRequestWithdrawalTx([FromBody] VbtcWithdrawalRequestModel request)
        {
            var existingRequest = VBTCWithdrawalRequest.GetActiveRequest(request.RequestorAddress, request.SmartContractUID);
            if (existingRequest != null)
                return Fail("WITHDRAWAL_ACTIVE", $"Active withdrawal already exists. Request Hash: {existingRequest.TransactionHash}", 409);

            var balResult = await BtcServices.VBTCService.TryGetAvailableTransparentVbtcBalance(request.SmartContractUID, request.RequestorAddress);
            if (!balResult.success)
                return Fail("BALANCE_LOOKUP_FAILED", balResult.error ?? "Balance lookup failed");

            if (balResult.availableBalance < request.Amount)
                return Fail("INSUFFICIENT_BALANCE", $"Insufficient balance. Available: {balResult.availableBalance}, Requested: {request.Amount}");

            var btcAddress = request.BTCAddress.ToBTCAddressNormalize();

            var txData = JsonConvert.SerializeObject(new
            {
                Function = "VBTCWithdrawalRequest()",
                ContractUID = request.SmartContractUID,
                RequestorAddress = request.RequestorAddress,
                BTCAddress = btcAddress,
                Amount = request.Amount,
                FeeRate = request.FeeRate
            });

            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = request.RequestorAddress,
                ToAddress = request.RequestorAddress,
                Amount = 0.0M,
                Fee = 0.0M,
                Nonce = AccountStateTrei.GetNextNonce(request.RequestorAddress),
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                Data = txData
            };

            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();

            _pendingRawVbtcTxs[tx.Hash] = tx;

            return Ok(new
            {
                Hash = tx.Hash,
                Timestamp = tx.Timestamp,
                FromAddress = tx.FromAddress,
                Fee = tx.Fee,
                Nonce = tx.Nonce,
                TransactionType = tx.TransactionType.ToString(),
                Message = "Sign the Hash field and submit via the request-raw-tx send endpoint."
            });
        }

        /// <summary>
        /// Submit a pre-signed VBTC_V2_WITHDRAWAL_REQUEST transaction
        /// </summary>
        [HttpPost("withdrawals/request-raw-tx/send")]
        public async Task<IActionResult> SendRequestWithdrawalTx([FromBody] RawVbtcTxSubmissionRequest body)
        {
            if (!_pendingRawVbtcTxs.TryRemove(body.Hash, out var pendingTx))
                return Fail("NOT_FOUND", $"No pending transaction found for hash {body.Hash}. Build the withdrawal-request TX first.", 404);

            if (pendingTx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_REQUEST)
                return Fail("INVALID_TX_TYPE", $"Invalid TX type: {pendingTx.TransactionType}");

            pendingTx.Signature = body.Signature;

            var (valid, reason) = await TransactionValidatorService.VerifyTX(pendingTx);
            if (!valid)
                return Fail("VERIFICATION_FAILED", $"Transaction verification failed: {reason}");

            await TransactionData.AddTxToWallet(pendingTx, true);
            await AccountData.UpdateLocalBalance(pendingTx.FromAddress, pendingTx.Fee + pendingTx.Amount);
            await TransactionData.AddToPool(pendingTx);
            await P2PClient.SendTXMempool(pendingTx);

            return Created(new { Hash = pendingTx.Hash, Message = "Withdrawal request transaction broadcast successfully." });
        }

        /// <summary>
        /// Build an unsigned VBTC_V2_WITHDRAWAL_COMPLETE transaction for offline signing
        /// (records the completion on the VFX chain after the BTC broadcast)
        /// </summary>
        [HttpPost("withdrawals/complete-raw-tx")]
        public IActionResult BuildCompleteWithdrawalTx([FromBody] RawCompleteWithdrawalTxRequest request)
        {
            if (request.Amount <= 0)
                return Fail("VALIDATION", "Amount must be greater than zero");

            var txData = JsonConvert.SerializeObject(new
            {
                Function = "VBTCWithdrawalComplete()",
                ContractUID = request.SmartContractUID,
                WithdrawalRequestHash = request.WithdrawalRequestHash,
                BTCTransactionHash = request.BTCTransactionHash,
                Amount = request.Amount,
                Destination = request.BTCDestination
            });

            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = request.FromAddress,
                ToAddress = request.FromAddress,
                Amount = 0.0M,
                Fee = 0.0M,
                Nonce = AccountStateTrei.GetNextNonce(request.FromAddress),
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                Data = txData
            };

            // Withdrawal-complete transactions are fee-free (matches VBTCService.CompleteWithdrawal)
            tx.Fee = 0M.ToNormalizeDecimal();
            tx.Build();

            _pendingRawVbtcTxs[tx.Hash] = tx;

            return Ok(new
            {
                Hash = tx.Hash,
                Timestamp = tx.Timestamp,
                FromAddress = tx.FromAddress,
                Fee = tx.Fee,
                Nonce = tx.Nonce,
                TransactionType = tx.TransactionType.ToString(),
                Message = "Sign the Hash field with your private key (ECDSA secp256k1) and submit via the complete-raw-tx send endpoint."
            });
        }

        /// <summary>
        /// Submit a pre-signed VBTC_V2_WITHDRAWAL_COMPLETE transaction
        /// </summary>
        [HttpPost("withdrawals/complete-raw-tx/send")]
        public async Task<IActionResult> SendCompleteWithdrawalTx([FromBody] RawVbtcTxSubmissionRequest body)
        {
            if (!_pendingRawVbtcTxs.TryRemove(body.Hash, out var pendingTx))
                return Fail("NOT_FOUND", $"No pending transaction found for hash {body.Hash}. Build the withdrawal-complete TX first.", 404);

            if (pendingTx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE)
                return Fail("INVALID_TX_TYPE", $"Invalid TX type: {pendingTx.TransactionType}");

            pendingTx.Signature = body.Signature;

            var (valid, reason) = await TransactionValidatorService.VerifyTX(pendingTx);
            if (!valid)
                return Fail("VERIFICATION_FAILED", $"Transaction verification failed: {reason}");

            await TransactionData.AddTxToWallet(pendingTx, true);
            await AccountData.UpdateLocalBalance(pendingTx.FromAddress, pendingTx.Fee + pendingTx.Amount);
            await TransactionData.AddToPool(pendingTx);
            await P2PClient.SendTXMempool(pendingTx);

            return Created(new { Hash = pendingTx.Hash, Message = "Withdrawal completion transaction broadcast successfully." });
        }

        /// <summary>
        /// Build an unsigned VBTC_V2_WITHDRAWAL_CANCEL transaction for offline signing
        /// </summary>
        [HttpPost("withdrawals/cancel-raw-tx")]
        public IActionResult BuildCancelWithdrawalTx([FromBody] VbtcCancelWithdrawalTxRequest request)
        {
            var existingRequest = VBTCWithdrawalRequest.GetByTransactionHash(request.WithdrawalRequestHash);
            if (existingRequest == null)
                return Fail("NOT_FOUND", $"Withdrawal request not found: {request.WithdrawalRequestHash}", 404);

            if (existingRequest.RequestorAddress != request.RequestorAddress)
                return Fail("FORBIDDEN", "Only the original requestor can cancel.", 403);

            if (existingRequest.IsCompleted)
                return Fail("ALREADY_COMPLETED", "Cannot cancel an already completed withdrawal.", 409);

            var txData = JsonConvert.SerializeObject(new
            {
                Function = "VBTCWithdrawalCancel()",
                ContractUID = request.SmartContractUID,
                WithdrawalRequestHash = request.WithdrawalRequestHash
            });

            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = request.RequestorAddress,
                ToAddress = request.RequestorAddress,
                Amount = 0.0M,
                Fee = 0.0M,
                Nonce = AccountStateTrei.GetNextNonce(request.RequestorAddress),
                TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                Data = txData
            };

            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();

            _pendingRawVbtcTxs[tx.Hash] = tx;

            return Ok(new
            {
                Hash = tx.Hash,
                Timestamp = tx.Timestamp,
                FromAddress = tx.FromAddress,
                Fee = tx.Fee,
                Nonce = tx.Nonce,
                TransactionType = tx.TransactionType.ToString(),
                Message = "Sign the Hash field and submit via the cancel-raw-tx send endpoint."
            });
        }

        /// <summary>
        /// Submit a pre-signed VBTC_V2_WITHDRAWAL_CANCEL transaction
        /// </summary>
        [HttpPost("withdrawals/cancel-raw-tx/send")]
        public async Task<IActionResult> SendCancelWithdrawalTx([FromBody] RawVbtcTxSubmissionRequest body)
        {
            if (!_pendingRawVbtcTxs.TryRemove(body.Hash, out var pendingTx))
                return Fail("NOT_FOUND", $"No pending transaction found for hash {body.Hash}. Build the withdrawal-cancel TX first.", 404);

            if (pendingTx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_CANCEL)
                return Fail("INVALID_TX_TYPE", $"Invalid TX type: {pendingTx.TransactionType}");

            pendingTx.Signature = body.Signature;

            var (valid, reason) = await TransactionValidatorService.VerifyTX(pendingTx);
            if (!valid)
                return Fail("VERIFICATION_FAILED", $"Transaction verification failed: {reason}");

            await TransactionData.AddTxToWallet(pendingTx, true);
            await AccountData.UpdateLocalBalance(pendingTx.FromAddress, pendingTx.Fee + pendingTx.Amount);
            await TransactionData.AddToPool(pendingTx);
            await P2PClient.SendTXMempool(pendingTx);

            return Created(new { Hash = pendingTx.Hash, Message = "Withdrawal cancel transaction broadcast successfully." });
        }

        #endregion

        #region Shielded (privacy)

        /// <summary>
        /// Shield transparent vBTC into the shielded pool (T→Z). Signs with the local wallet.
        /// </summary>
        [HttpPost("shield")]
        public async Task<IActionResult> ShieldVbtc([FromBody] ShieldVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.FromAddress))
                return Fail("VALIDATION", "FromAddress required.");
            if (!AddressValidateUtility.ValidateAddress(req.FromAddress))
                return Fail("INVALID_ADDRESS", "Invalid FromAddress.");
            var account = AccountData.GetSingleAccount(req.FromAddress);
            if (account == null)
                return Fail("NOT_FOUND", "FromAddress not in local wallet.", 404);

            var sw = PrivacyDbContext.Wallets().FindOne(x => x.TransparentSourceAddress == req.FromAddress);
            var vfxUnspent = PrivacyApiHelper.SumVfxUnspent(sw);
            string? coShieldWarning = vfxUnspent < Globals.PrivateTxFixedFee * 2
                ? "Low or no shielded VFX for future ZK fees; consider also shielding VFX."
                : null;

            var nonce = AccountStateTrei.GetNextNonce(req.FromAddress);
            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildShield(
                    req.FromAddress,
                    req.VbtcContractUid,
                    req.VbtcAmount,
                    Globals.MinFeePerKB,
                    nonce,
                    ts,
                    req.RecipientZfxAddress,
                    req.Memo,
                    out var tx,
                    out var err,
                    DbContext.DB_Privacy))
                return Fail("BUILD_FAILED", err ?? "Build failed.");

            tx!.Fee = req.TransparentFee ?? FeeCalcService.CalculateTXFee(tx);
            tx.BuildPrivate();
            var pk = account.GetPrivKey;
            if (pk == null)
                return Fail("WALLET_LOCKED", "Cannot sign (wallet locked?).", 401);
            var sig = SignatureService.CreateSignature(tx.Hash, pk, account.PublicKey);
            if (sig == "ERROR")
                return Fail("SIGNATURE_FAILED", "Signature failed.", 500);
            tx.Signature = sig;

            var (ok, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx);
            if (!ok)
                return FailFromV1Json(json, "BROADCAST_FAILED");
            return Created(new { Hash = tx.Hash, Message = "Broadcast.", CoShieldWarning = coShieldWarning });
        }

        /// <summary>
        /// Unshield vBTC back to a transparent address (Z→T)
        /// </summary>
        [HttpPost("unshield")]
        public async Task<IActionResult> UnshieldVbtc([FromBody] UnshieldVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet for zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var candidates = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal)).ToList() ?? new List<UnspentCommitment>();
            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(candidates, req.TransparentVbtcAmount + fee, out var inputs, out _, out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "Input selection failed.");

            var vfxCandidates = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)).ToList() ?? new List<UnspentCommitment>();
            var vfxFeeNote = vfxCandidates.Where(c => c.Amount >= fee).OrderBy(c => c.Amount).FirstOrDefault();
            if (vfxFeeNote == null)
                return Fail("NO_FEE_NOTE", "Need at least one shielded VFX note whose amount covers the fixed ZK fee. Co-shield VFX or consolidate notes first.");

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildUnshield(req.VbtcContractUid, inputs, req.TransparentVbtcAmount, req.TransparentToAddress, keys, ts, out var tx, out var berr, vfxFeeNote, DbContext.DB_Privacy))
                return Fail("BUILD_FAILED", berr ?? "Build failed.");

            var (ok, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
            if (!ok)
                return FailFromV1Json(json, "BROADCAST_FAILED");
            return Created(new { Hash = tx!.Hash, Message = "Broadcast." });
        }

        /// <summary>
        /// Shielded-to-shielded vBTC transfer (Z→Z)
        /// </summary>
        [HttpPost("private-transfer")]
        public async Task<IActionResult> PrivateTransferVbtc([FromBody] PrivateTransferVbtcRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ZfxAddress))
                return Fail("VALIDATION", "ZfxAddress required.");
            var w = ShieldedWalletService.FindByZfxAddress(req.ZfxAddress);
            if (w == null)
                return Fail("NOT_FOUND", "No shielded wallet for zfx address.", 404);
            if (!PrivacyApiHelper.TryGetKeyMaterial(w, req.WalletPassword, out var keys, out var kmErr))
                return Fail("KEY_MATERIAL", kmErr ?? "Keys", 401);

            var asset = VbtcPrivacyAsset.FormatAssetKey(req.VbtcContractUid);
            var candidates = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal)).ToList() ?? new List<UnspentCommitment>();
            var fee = Globals.PrivateTxFixedFee;
            if (!CommitmentSelectionService.TrySelectInputs(candidates, req.PaymentAmount + fee, out var inputs, out _, out var selErr))
                return Fail("INPUT_SELECTION_FAILED", selErr ?? "Input selection failed.");

            var vfxCandidates = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, "VFX", StringComparison.Ordinal)).ToList() ?? new List<UnspentCommitment>();
            var vfxFeeNote = vfxCandidates.Where(c => c.Amount >= fee).OrderBy(c => c.Amount).FirstOrDefault();
            if (vfxFeeNote == null)
                return Fail("NO_FEE_NOTE", "Need at least one shielded VFX note whose amount covers the fixed ZK fee. Co-shield VFX or consolidate notes first.");

            var ts = TimeUtil.GetTime();
            if (!VbtcPrivateTransactionBuilder.TryBuildPrivateTransfer(req.VbtcContractUid, inputs, req.PaymentAmount, req.RecipientZfxAddress, keys, ts, out var tx, out var berr, vfxFeeNote, DbContext.DB_Privacy))
                return Fail("BUILD_FAILED", berr ?? "Build failed.");

            var (ok, json) = await PrivacyApiHelper.BroadcastVerifiedPrivateTxAsync(tx!);
            if (!ok)
                return FailFromV1Json(json, "BROADCAST_FAILED");
            return Created(new { Hash = tx!.Hash, Message = "Broadcast." });
        }

        /// <summary>
        /// Shielded vBTC balance for a zfx address and contract
        /// </summary>
        [HttpGet("shielded-balance")]
        public IActionResult GetShieldedBalance([FromQuery] string zfxAddress, [FromQuery] string scUID)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress) || string.IsNullOrWhiteSpace(scUID))
                return Fail("VALIDATION", "zfxAddress and scUID required.");
            var w = ShieldedWalletService.FindByZfxAddress(zfxAddress);
            if (w == null)
                return Ok(new { Balance = 0.0m });
            var asset = VbtcPrivacyAsset.FormatAssetKey(scUID);
            var sum = w.UnspentCommitments?.Where(c => c != null && !c.IsSpent && string.Equals(c.AssetType, asset, StringComparison.Ordinal)).Sum(c => c.Amount) ?? 0;
            return Ok(new { Balance = sum, Asset = asset });
        }

        /// <summary>
        /// Shielded pool state for a vBTC contract
        /// </summary>
        [HttpGet("shielded-pool/{scUID}")]
        public IActionResult GetShieldedPoolState(string scUID)
        {
            var st = VBTCPrivacyService.GetPoolState(scUID);
            if (st == null)
                return Ok(new { Asset = VbtcPrivacyAsset.FormatAssetKey(scUID), CurrentMerkleRoot = (string?)null, TotalCommitments = 0L, TotalShieldedSupply = 0.0M, LastUpdateHeight = 0L });
            return Ok(new { st.AssetType, st.CurrentMerkleRoot, st.TotalCommitments, st.TotalShieldedSupply, st.LastUpdateHeight });
        }

        #endregion

        #region Base bridge

        /// <summary>
        /// Lock vBTC for bridging to Base (broadcasts a VBTC_V2_BRIDGE_LOCK signed by the local wallet)
        /// </summary>
        [HttpPost("bridge/to-base")]
        public async Task<IActionResult> BridgeVbtcToBase([FromBody] BridgeToBaseRequest request)
        {
            if (!request.EvmDestination.StartsWith("0x") || request.EvmDestination.Length != 42)
                return Fail("VALIDATION", "EvmDestination must be a valid EVM address (0x + 40 hex chars)");

            if (AccountData.GetSingleAccount(request.OwnerAddress) == null)
                return Fail("NOT_FOUND", "OwnerAddress must be a wallet account on this node (required to sign VBTC_V2_BRIDGE_LOCK).", 404);

            var balResult = await BtcServices.VBTCService.TryGetAvailableTransparentVbtcBalance(
                request.SmartContractUID, request.OwnerAddress);

            if (!balResult.success)
                return Fail("BALANCE_LOOKUP_FAILED", balResult.error ?? "Failed to check balance");

            // Deduct local bridge reservations not yet reflected in state trei (see BridgeLockRecord.GetLockedAmount)
            var alreadyLocked = BridgeLockRecord.GetLockedAmount(request.OwnerAddress, request.SmartContractUID);
            var availableBalance = balResult.availableBalance - alreadyLocked;

            if (request.Amount > availableBalance)
                return Fail("INSUFFICIENT_BALANCE",
                    $"Insufficient available balance. Available: {availableBalance} BTC, Requested: {request.Amount} BTC, Reserved (pending VFX confirmation): {alreadyLocked} BTC");

            var lockId = Guid.NewGuid().ToString("N");
            var amountSats = (long)(request.Amount * 100_000_000M);

            var txResult = await BtcServices.VBTCService.CreateBridgeLockTx(
                request.SmartContractUID,
                request.OwnerAddress,
                request.Amount,
                request.EvmDestination,
                lockId);

            if (!txResult.Success)
                return Fail("TX_FAILED", txResult.TxHashOrError);

            var vfxTxHash = txResult.TxHashOrError;

            var lockRecord = new BridgeLockRecord
            {
                LockId = lockId,
                SmartContractUID = request.SmartContractUID,
                OwnerAddress = request.OwnerAddress,
                Amount = request.Amount,
                AmountSats = amountSats,
                EvmDestination = request.EvmDestination,
                Status = BridgeLockStatus.Locked,
                CreatedAtUtc = TimeUtil.GetTime(),
                VfxLockTxHash = vfxTxHash,
                VfxLockConfirmedOnChain = false
            };

            if (!BridgeLockRecord.Save(lockRecord))
                return Fail("SAVE_FAILED",
                    $"VFX lock transaction was broadcast (LockId {lockId}, Tx {vfxTxHash}) but saving the local bridge record failed. Check wallet transactions.", 500);

            LogUtility.Log($"[BaseBridge] VFX bridge lock broadcast. LockId: {lockId}, Tx: {vfxTxHash}, Amount: {request.Amount} BTC, To: {request.EvmDestination}",
                "VbtcController.BridgeVbtcToBase");

            string status;
            if (VbtcBaseBridge.IsEnabled)
                status = "VFX lock broadcast. After the lock confirms on-chain, validators sign mint attestations and casters submit mintWithProof on Base.";
            else
                status = "VFX lock broadcast. Configure BaseBridgeRpcUrl and BaseBridgeContract in config.txt for Base mint (VBTCb).";

            return Created(new
            {
                Message = "vBTC bridge lock transaction broadcast",
                LockId = lockId,
                VfxLockTxHash = vfxTxHash,
                SmartContractUID = request.SmartContractUID,
                Amount = request.Amount,
                AmountSats = amountSats,
                EvmDestination = request.EvmDestination,
                Status = status,
                BridgeEnabled = VbtcBaseBridge.IsEnabled
            });
        }

        /// <summary>
        /// Bridge configuration and operational status
        /// </summary>
        [HttpGet("bridge/status")]
        public async Task<IActionResult> GetBridgeStatus()
        {
            var pendingAttestations = BridgeLockRecord.GetPendingV2Attestations().Count;
            var totalSupply = VbtcBaseBridge.CanReadVbtcToken
                ? await VbtcBaseBridge.GetBaseTotalSupply()
                : (false, 0M, "Not configured");

            var sync = BridgeExitSyncState.GetOrCreate();
            return Ok(new
            {
                BridgeEnabled = VbtcBaseBridge.IsEnabled,
                ExitWatchConfigured = ReserveBlockCore.Bitcoin.Services.BaseBridgeExitWatchService.IsConfigured,
                BaseRpcUrl = VbtcBaseBridge.BaseRpcUrl,
                VBTCbContractAddress = VbtcBaseBridge.VBTCbContractAddress,
                BaseChainId = VbtcBaseBridge.BaseChainId,
                PendingV2Attestations = pendingAttestations,
                BaseTotalSupply = totalSupply.Item2,
                ExitPollLastScannedBlock = sync.LastScannedBlock,
                Network = VbtcBaseBridge.BaseNetworkDisplayName
            });
        }

        /// <summary>
        /// Contract address, chainId, and ABI the frontend needs to call mintWithProof
        /// </summary>
        [HttpGet("bridge/config")]
        public IActionResult GetBridgeConfig()
        {
            return Ok(new
            {
                IsEnabled = VbtcBaseBridge.IsEnabled,
                ContractAddress = VbtcBaseBridge.ContractAddress,
                ChainId = VbtcBaseBridge.BaseChainId,
                Network = VbtcBaseBridge.BaseNetworkDisplayName,
                Abi = VbtcBaseBridge.CONTRACT_ABI
            });
        }

        /// <summary>
        /// Bridge lock status by lock ID
        /// </summary>
        [HttpGet("bridge/locks/{lockId}")]
        public IActionResult GetBridgeLock(string lockId)
        {
            var record = BridgeLockRecord.GetByLockId(lockId);
            if (record == null)
                return Fail("NOT_FOUND", "Bridge lock not found", 404);

            return Ok(new { Lock = record });
        }

        /// <summary>
        /// In-memory mint attestation progress for a lock ID on this node
        /// </summary>
        [HttpGet("bridge/locks/{lockId}/attestation")]
        public IActionResult GetMintAttestation(string lockId)
        {
            var state = ReserveBlockCore.Bitcoin.Services.BaseBridgeAttestationService.GetAttestationState(lockId);
            if (state == null)
                return Fail("NOT_FOUND", "No attestation state for this lock on this node", 404);

            return Ok(new { Attestation = state });
        }

        /// <summary>
        /// All bridge locks for a contract
        /// </summary>
        [HttpGet("bridge/contracts/{scUID}/locks")]
        public IActionResult GetBridgeLocksByContract(string scUID)
        {
            var records = BridgeLockRecord.GetBySmartContract(scUID);
            return Ok(new { Count = records.Count, Locks = records });
        }

        /// <summary>
        /// All bridge locks for an owner address (across contracts)
        /// </summary>
        [HttpGet("bridge/owners/{ownerAddress}/locks")]
        public IActionResult GetBridgeLocksByOwner(string ownerAddress)
        {
            var records = BridgeLockRecord.GetByOwner(ownerAddress);
            return Ok(new { Count = records.Count, Locks = records });
        }

        /// <summary>
        /// Bridge preflight for an owner + contract: balance, gas, config readiness
        /// </summary>
        [HttpGet("bridge/preflight/{ownerAddress}/{scUID}")]
        public async Task<IActionResult> BridgePreflight(string ownerAddress, string scUID)
        {
            var result = await ReserveBlockCore.BrowserWalletServices.WalletVbtcService.GetBridgePreflight(ownerAddress, scUID);
            return Ok(result);
        }

        /// <summary>
        /// Retry a failed bridge mint for a lock
        /// </summary>
        [HttpPost("bridge/locks/{lockId}/retry")]
        public async Task<IActionResult> RetryBridgeMint(string lockId, [FromBody] BridgeRetryRequest request)
        {
            var result = await ReserveBlockCore.BrowserWalletServices.WalletVbtcService.RetryBridgeMint(lockId, request.OwnerAddress);
            return Ok(result);
        }

        /// <summary>
        /// Force-retry a stuck bridge mint: reconstructs local tracking from on-chain state
        /// if missing, checks the Base contract, then collects fresh attestations
        /// </summary>
        [HttpPost("bridge/locks/{lockId}/force-retry")]
        public async Task<IActionResult> ForceRetryBridgeMint(string lockId, [FromBody] BridgeRetryRequest request)
        {
            var result = await ReserveBlockCore.BrowserWalletServices.WalletVbtcService.ForceRetryBridgeMint(lockId, request.OwnerAddress);
            return Ok(result);
        }

        /// <summary>
        /// Deterministic Base (EVM) address for a VFX address (Keccak256 of the secp256k1 key)
        /// </summary>
        [HttpGet("bridge/base-address/{vfxAddress}")]
        public IActionResult GetBaseAddress(string vfxAddress)
        {
            var baseAddress = ReserveBlockCore.Bitcoin.Services.ValidatorEthKeyService.DeriveBaseAddressFromAccount(vfxAddress);
            if (string.IsNullOrEmpty(baseAddress))
                return Fail("DERIVATION_FAILED", "Could not derive Base address. Account not found or key unavailable.", 404);

            return Ok(new { BaseAddress = baseAddress, VfxAddress = vfxAddress });
        }

        /// <summary>
        /// vBTC.b balance on Base for an EVM address
        /// </summary>
        [HttpGet("bridge/base-balance/{evmAddress}")]
        public async Task<IActionResult> GetBaseBalance(string evmAddress)
        {
            var result = await VbtcBaseBridge.GetBaseBalance(evmAddress);
            if (!result.Success)
                return Fail("BASE_QUERY_FAILED", result.Message);

            return Ok(new
            {
                EvmAddress = evmAddress,
                VBTCbBalance = result.Balance,
                ContractAddress = VbtcBaseBridge.VBTCbContractAddress,
                Network = VbtcBaseBridge.BaseNetworkDisplayName,
                ChainId = VbtcBaseBridge.BaseChainId
            });
        }

        #endregion

        #region Utility

        /// <summary>
        /// Default vBTC image metadata (Base64)
        /// </summary>
        [HttpGet("default-image")]
        public IActionResult GetDefaultImage()
        {
            return Ok(new
            {
                EncodingFormat = "base64",
                ImageExtension = "png",
                ImageName = "defaultvBTC_V2.png",
                ImageBase = string.Empty // No default image bundled; callers should provide their own
            });
        }

        /// <summary>
        /// Translate a v1-style {"Success":false,"Message":...} JSON string into a v2 Fail envelope.
        /// </summary>
        private IActionResult FailFromV1Json(string json, string code)
        {
            try
            {
                var parsed = JObject.Parse(json);
                return Fail(code, parsed.Value<string>("Message") ?? json);
            }
            catch
            {
                return Fail(code, json);
            }
        }

        #endregion
    }
}
