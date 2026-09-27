using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.P2P;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Service for vBTC V2 (MPC-based tokenized Bitcoin) operations
    /// </summary>
    public class VBTCService
    {
        /// <summary>vBTC amounts are denominated in BTC and settle in satoshis: 8 decimal places.</summary>
        public const int VbtcDecimalPlaces = 8;

        /// <summary>
        /// VX-01: consensus amount rule shared by every vBTC V2 transaction that moves value
        /// (transfer, withdrawal request, bridge lock). Returns null when valid, else the rejection
        /// reason. A non-positive amount is never meaningful: on the withdrawal escrow path a
        /// negative amount inverted the debit into an unbacked credit (mint).
        /// </summary>
        public static string? GetVbtcAmountError(decimal? amount, string context)
        {
            if (!amount.HasValue || amount.Value <= 0M)
                return $"Amount must be greater than zero for {context}.";
            if (amount.Value != Math.Round(amount.Value, VbtcDecimalPlaces))
                return $"Amount cannot have more than {VbtcDecimalPlaces} decimal places for {context}.";
            return null;
        }

        /// <summary>
        /// Virtual size of the smallest withdrawal transaction BitcoinTransactionService builds: one Taproot key-path
        /// input and two outputs (destination + change) = 57.5 + 2 × 43 + 10.5 vB.
        /// </summary>
        public const int MinWithdrawalTxVBytes = 154;

        /// <summary>Dust limit of a Taproot output: Bitcoin nodes do not relay a smaller one.</summary>
        public const long TaprootDustSats = 330;

        /// <summary>
        /// Consensus rule (VbtcWithdrawalConcurrencyHeight), shared with the wallet paths so both give the same message:
        /// a withdrawal pays its Bitcoin fee out of the amount, so the amount must cover the fee of the smallest withdrawal
        /// transaction at the request's own fee rate and still leave a relayable payout. Uses only the request's fields, so
        /// every node agrees. A request that passes can still need more inputs (and fee) when the vault's coins are small;
        /// that request fails at signing and blocks no one but its requester.
        /// </summary>
        public static string? GetWithdrawalFeeFloorError(decimal amount, long feeRate, string context)
        {
            if (feeRate <= 0 || amount <= 0M)
                return null; // the amount and fee-rate rules report these
            var minFeeSats = (decimal)feeRate * MinWithdrawalTxVBytes;
            var payoutSats = amount * 100_000_000M - minFeeSats;
            if (payoutSats >= TaprootDustSats)
                return null;
            var minAmount = (minFeeSats + TaprootDustSats) / 100_000_000M;
            return $"{context}: {amount:0.########} BTC cannot pay a Bitcoin withdrawal at {feeRate} sat/vB. The smallest withdrawal transaction costs {minFeeSats:0} sats in fees and the payout must be at least {TaprootDustSats} sats, so the minimum at this fee rate is {minAmount:0.########} BTC. Raise the amount or lower the fee rate.";
        }

        /// <summary>
        /// Balance-endpoint view of the escrow a holder has in open withdrawal requests on one contract: the total, the
        /// part in requests past the 360-block window (recoverable only by cancelling), and one entry per request so a
        /// wallet can offer Complete, or Cancel for an expired or unpayable one.
        /// </summary>
        public static (decimal EscrowedAmount, decimal ExpiredEscrowAmount, List<object> Requests) DescribeEscrowedWithdrawals(string address, string scUID)
        {
            var height = Globals.LastBlock?.Height ?? 0;
            var now = TimeUtil.GetTime();
            decimal total = 0M, expired = 0M;
            var requests = new List<object>();
            foreach (var r in VBTCWithdrawalRequest.GetOpenEscrowedRequests(address, scUID))
            {
                var isExpired = !VBTCWithdrawalRequest.IsStillBlocking(r, height, now);
                total += r.Amount;
                if (isExpired)
                    expired += r.Amount;
                requests.Add(new
                {
                    RequestHash = r.TransactionHash,
                    r.Amount,
                    r.BTCDestination,
                    r.FeeRate,
                    r.RequestBlockHeight,
                    ExpiresAtHeight = r.RequestBlockHeight + VBTCWithdrawalRequest.EXPIRY_BLOCKS,
                    Expired = isExpired,
                    Unpayable = GetUnpayableWithdrawalMessage(r) != null,
                    CancellationPending = VBTCWithdrawalCancellation.HasPendingCancellation(r.TransactionHash, now, scUID),
                });
            }
            return (total, expired, requests);
        }

        /// <summary>Prefix of the message for a withdrawal request that can never be paid (see GetUnpayableWithdrawalMessage).</summary>
        public const string UnpayableWithdrawalMarker = "[UNPAYABLE-WITHDRAWAL]";

        /// <summary>
        /// Null when the request can be paid; otherwise why not and how to recover. Applies to requests of any height:
        /// ones mined before the fee floor existed can be below it, and completing them fails on every attempt.
        /// </summary>
        public static string? GetUnpayableWithdrawalMessage(VBTCWithdrawalRequest? request)
        {
            if (request == null || request.IsCompleted || request.Amount <= 0)
                return null;
            var feeRate = request.FeeRate != 0 ? request.FeeRate : 10; // CompleteWithdrawal's default
            var floorError = GetWithdrawalFeeFloorError(request.Amount, feeRate, "This withdrawal");
            if (floorError == null)
                return null;
            var escrowed = VBTCWithdrawalRequest.EscrowAppliesTo(request.RequestBlockHeight)
                ? $" to refund the {request.Amount:0.########} vBTC held in escrow"
                : "";
            return $"{UnpayableWithdrawalMarker} Withdrawal {request.TransactionHash} can never be paid, so completing it will keep failing. {floorError} " +
                   $"Cancel it instead: a withdrawal-cancel transaction from {request.RequestorAddress} asks the contract's validators to vote{escrowed}.";
        }

        /// <summary>True once withdrawal concurrency and the fee floor apply to a transaction at <paramref name="height"/>.</summary>
        public static bool WithdrawalConcurrencyActive(long height) => height >= Globals.VbtcWithdrawalConcurrencyHeight;

        /// <summary>Height a transaction submitted now would be mined at (local gates and mempool admission).</summary>
        public static long NextBlockHeight => (Globals.LastBlock?.Height ?? 0) + 1;

        // Keyed by SmartContractUID; the stored digest guards against a (never expected) change of
        // ContractData under the same UID. Decompiling runs a Trillium REPL, so this is cached.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string DataDigest, bool IsV2)> _vbtcV2ContractCache = new();

        /// <summary>
        /// VX-01: consensus definition of "a vBTC V2 contract" — the contract code stored in the
        /// state trei (available on ALL nodes) declares the TokenizationV2 feature. The tokenization
        /// ledger machinery (transfer, withdrawal, bridge lock) must only ever operate on such a
        /// contract; before this check any minted smart contract (e.g. an NFT) was accepted as a
        /// target. S3C contracts carry the same feature (IsS3C flag) and pass. Missing or
        /// undecompilable contract data is NOT a vBTC V2 contract.
        /// </summary>
        public static bool IsVbtcV2Contract(SmartContractStateTrei? scState)
        {
            if (scState == null || string.IsNullOrEmpty(scState.SmartContractUID) || string.IsNullOrEmpty(scState.ContractData))
                return false;

            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(scState.ContractData)));
            if (_vbtcV2ContractCache.TryGetValue(scState.SmartContractUID, out var cached) && cached.DataDigest == digest)
                return cached.IsV2;

            bool isV2;
            try
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                isV2 = scMain?.Features?.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2) == true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"IsVbtcV2Contract: contract {scState.SmartContractUID} could not be decompiled: {ex.Message}",
                    "VBTCService.IsVbtcV2Contract()");
                isV2 = false;
            }

            _vbtcV2ContractCache[scState.SmartContractUID] = (digest, isV2);
            return isV2;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string DataDigest, bool IsV1)> _vbtcV1ContractCache = new();

        /// <summary>
        /// NEW-28: a legacy V1 vBTC (arbiter tokenization) contract - the code stored in the state trei declares the
        /// Tokenization feature and not TokenizationV2 (a V2 vault is never treated as V1). Same reading as
        /// <see cref="IsVbtcV2Contract"/>; missing or undecompilable contract data is not a V1 contract.
        /// </summary>
        public static bool IsVbtcV1Contract(SmartContractStateTrei? scState)
        {
            if (scState == null || string.IsNullOrEmpty(scState.SmartContractUID) || string.IsNullOrEmpty(scState.ContractData))
                return false;

            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(scState.ContractData)));
            if (_vbtcV1ContractCache.TryGetValue(scState.SmartContractUID, out var cached) && cached.DataDigest == digest)
                return cached.IsV1;

            bool isV1;
            try { isV1 = DeclaresVbtcV1(SmartContractMain.GenerateSmartContractInMemory(scState.ContractData)); }
            catch { isV1 = false; }

            _vbtcV1ContractCache[scState.SmartContractUID] = (digest, isV1);
            return isV1;
        }

        /// <summary>NEW-28: the decompiled contract carries the V1 Tokenization feature and no TokenizationV2 feature.</summary>
        public static bool DeclaresVbtcV1(SmartContractMain? scMain) =>
            scMain?.Features?.Any(f => f != null && f.FeatureName == FeatureName.Tokenization) == true
            && scMain.Features.Any(f => f != null && f.FeatureName == FeatureName.TokenizationV2) == false;

        /// <summary>
        /// Spendable transparent vBTC for <paramref name="fromAddress"/> on contract <paramref name="scUid"/>:
        /// owner = BTC deposit balance + tokenization ledger; non-owner = ledger only (matches <see cref="TransferVBTC"/>).
        /// </summary>
        /// <summary>
        /// Owner ledger component for transparent vBTC balance math:
        /// SUM of ALL tokenization ledger rows involving the owner (so transfer debits and bridge
        /// locks stay debited — the BTC never left the deposit address for those), PLUS an add-back
        /// of completed withdrawal amounts, whose burn rows are already reflected in the ElectrumX
        /// deposit balance and would otherwise be double-counted.
        /// Owner available = ElectrumX deposit balance + this value.
        /// </summary>
        public static decimal GetOwnerLedgerBalance(SmartContractStateTrei scState, string ownerAddress, long currentHeight)
        {
            decimal ledgerBalance = 0M;
            if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
            {
                var ownerRows = scState.SCStateTreiTokenizationTXes
                    .Where(x => x.FromAddress == ownerAddress || x.ToAddress == ownerAddress)
                    .ToList();

                if (ownerRows.Any())
                    ledgerBalance = ownerRows.Sum(x => x.Amount);
            }

            ledgerBalance += VBTCWithdrawalRequest.GetCompletedWithdrawalAmount(ownerAddress, scState.SmartContractUID, currentHeight);

            return ledgerBalance;
        }

        /// <summary>
        /// Deposit address for a vBTC contract: the local VBTCContractV2 record when present, else
        /// decompiled from the contract code stored in the state trei — available on ALL nodes, so
        /// owner balances resolve correctly on casters/remote nodes with no local record.
        /// </summary>
        public static string? ResolveDepositAddress(SmartContractStateTrei scState, VBTCContractV2? contract)
        {
            if (!string.IsNullOrEmpty(contract?.DepositAddress))
                return contract.DepositAddress;

            if (string.IsNullOrEmpty(scState?.ContractData))
                return null;

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
                        return tknz.DepositAddress;
                }
            }
            catch (Exception decompileEx)
            {
                ErrorLogUtility.LogError($"Failed to decompile contract {scState.SmartContractUID} for deposit address: {decompileEx.Message}",
                    "VBTCService.ResolveDepositAddress()");
            }
            return null;
        }

        public static async Task<(bool success, decimal availableBalance, string? error)> TryGetAvailableTransparentVbtcBalance(string scUid, string fromAddress, long? blockHeight = null)
        {
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUid);
                if (scState == null)
                {
                    return (false, 0M, $"Smart contract state not found: {scUid}");
                }

                // Calculate ledger balance from tokenization TXes (consensus data, available on all nodes)
                decimal ledgerBalance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == fromAddress || x.ToAddress == fromAddress)
                        .ToList();

                    if (transactions.Any())
                    {
                        var received = transactions.Where(x => x.ToAddress == fromAddress).Sum(x => x.Amount);
                        var sent = transactions.Where(x => x.FromAddress == fromAddress).Sum(x => x.Amount);
                        ledgerBalance = received + sent;
                    }
                }

                // Determine owner address and deposit address.
                // Try local DB first; fall back to State Trei + in-memory decompile for remote nodes.
                string? ownerAddress = null;
                string? depositAddress = null;
                decimal localCachedBalance = 0M;

                var vbtcContract = VBTCContractV2.GetContract(scUid);
                if (vbtcContract != null)
                {
                    ownerAddress = vbtcContract.OwnerAddress;
                    depositAddress = vbtcContract.DepositAddress;
                    localCachedBalance = vbtcContract.Balance;
                }
                else
                {
                    // Remote node fallback: owner address from State Trei, deposit address from contract code
                    ownerAddress = scState.OwnerAddress;
                    depositAddress = ResolveDepositAddress(scState, null);
                }

                bool isOwner = !string.IsNullOrEmpty(ownerAddress) && ownerAddress == fromAddress;

                if (!isOwner)
                {
                    // Non-owner: balance is purely from the tokenization ledger (works on all nodes)
                    return (true, ledgerBalance, null);
                }

                // Owner: must verify actual BTC deposit balance to prevent inflation.
                // Full ledger sum (transfer debits and bridge locks stay debited) plus an add-back
                // of completed withdrawals whose burn rows the ElectrumX balance already reflects.
                ledgerBalance = GetOwnerLedgerBalance(scState, fromAddress, blockHeight ?? Globals.LastBlock?.Height ?? 0);

                // Query Electrum for real-time balance of the deposit address.
                decimal btcDepositBalance = 0M;
                if (!string.IsNullOrEmpty(depositAddress))
                {
                    try
                    {
                        using var client = await VerifiedXCore.Bitcoin.Bitcoin.ElectrumXClient();
                        if (client != null)
                        {
                            var balance = await client.GetBalance(depositAddress, false);
                            btcDepositBalance = balance.Confirmed / 100_000_000M;
                        }
                        else
                        {
                            // Electrum unavailable — fall back to locally cached balance if we have it
                            btcDepositBalance = localCachedBalance;
                        }
                    }
                    catch (Exception elxEx)
                    {
                        // Electrum query failed — fall back to locally cached balance
                        ErrorLogUtility.LogError($"ElectrumX query failed for deposit balance, using cached: {elxEx.Message}",
                            "VBTCService.TryGetAvailableTransparentVbtcBalance()");
                        btcDepositBalance = localCachedBalance;
                    }
                }

                return (true, btcDepositBalance + ledgerBalance, null);
            }
            catch (Exception ex)
            {
                return (false, 0M, ApiErrorText.For(ex));
            }
        }

        #region Deposit Balance Scanning

        /// <summary>
        /// Scan all owned vBTC V2 contracts' deposit addresses via Electrum
        /// and update the local Balance field. Called on startup and periodically.
        /// Mirrors the v1 pattern where the owner scans their own deposit address.
        /// </summary>
        public static async Task ScanVBTCV2Balances()
        {
            try
            {
                var accounts = AccountData.GetAccounts();
                if (accounts == null) return;

                var allAddresses = accounts.FindAll().Select(a => a.Address).ToList();

                // Reserve (xRBX) accounts can own vBTC V2 contracts — include them or their
                // deposit-address Balance cache is never refreshed.
                var reserveAccounts = ReserveAccount.GetReserveAccounts();
                if (reserveAccounts != null)
                    allAddresses.AddRange(reserveAccounts.Select(r => r.Address));

                if (!allAddresses.Any()) return;

                foreach (var address in allAddresses)
                {
                    var contracts = VBTCContractV2.GetContractsByOwner(address);
                    if (contracts == null || !contracts.Any()) continue;

                    foreach (var contract in contracts)
                    {
                        await ScanSingleContractBalance(contract);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning vBTC V2 balances: {ex.Message}", "VBTCService.ScanVBTCV2Balances()");
            }
        }

        /// <summary>
        /// Scan a single contract's deposit address via Electrum and update local balance.
        /// </summary>
        public static async Task<decimal> ScanSingleContractBalance(VBTCContractV2 contract)
        {
            try
            {
                if (string.IsNullOrEmpty(contract.DepositAddress))
                    return 0M;

                var utxoLookup = await BitcoinTransactionService.GetTaprootUTXOs(contract.DepositAddress);
                if (!utxoLookup.Success)
                {
                    // Inconclusive lookup (data sources unreachable) — never overwrite the cached
                    // balance with 0 on a failed lookup; keep the last known value.
                    ErrorLogUtility.LogError($"UTXO lookup inconclusive for {contract.SmartContractUID}; keeping cached balance {contract.Balance}: {utxoLookup.Error}",
                        "VBTCService.ScanSingleContractBalance()");
                    return contract.Balance;
                }

                decimal btcBalance = 0M;

                if (utxoLookup.Utxos.Any())
                {
                    // Sum all UTXO values (in satoshis) and convert to BTC
                    ulong totalSatoshis = 0;
                    foreach (var utxo in utxoLookup.Utxos)
                    {
                        totalSatoshis += utxo.Value;
                    }
                    btcBalance = totalSatoshis * BitcoinTransactionService.SatoshiMultiplier;
                }

                // Update contract balance locally
                if (contract.Balance != btcBalance)
                {
                    contract.Balance = btcBalance;
                    VBTCContractV2.UpdateContract(contract);
                    SCLogUtility.Log($"Updated vBTC V2 balance for {contract.SmartContractUID}: {btcBalance} BTC", 
                        "VBTCService.ScanSingleContractBalance()");
                }

                return btcBalance;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error scanning contract {contract.SmartContractUID}: {ex.Message}", 
                    "VBTCService.ScanSingleContractBalance()");
                return contract.Balance;
            }
        }

        /// <summary>
        /// Background loop that periodically scans vBTC V2 deposit balances.
        /// Runs initial scan on startup, then every 5 minutes.
        /// </summary>
        public static async Task VBTCV2BalanceScanLoop()
        {
            try
            {
                // Wait for startup to complete before first scan
                await Task.Delay(TimeSpan.FromSeconds(30));

                LogUtility.Log("Starting vBTC V2 deposit balance scan loop", "VBTCService.VBTCV2BalanceScanLoop()");

                while (true)
                {
                    try
                    {
                        await ScanVBTCV2Balances();
                    }
                    catch (Exception ex)
                    {
                        ErrorLogUtility.LogError($"Error in balance scan loop iteration: {ex.Message}", "VBTCService.VBTCV2BalanceScanLoop()");
                    }

                    // Wait 5 minutes between scans
                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Fatal error in balance scan loop: {ex.Message}", "VBTCService.VBTCV2BalanceScanLoop()");
            }
        }

        #endregion

        /// <summary>
        /// Transfer ownership of a vBTC V2 contract to another address
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="toAddress">New owner address</param>
        /// <param name="backupURL">Optional backup URL</param>
        /// <returns>JSON result</returns>
        public static async Task<string> TransferOwnership(string scUID, string toAddress, string? backupURL = "")
        {
            try
            {
                // Get vBTC V2 contract
                var vbtcContract = VBTCContractV2.GetContract(scUID);

                if (vbtcContract == null)
                    return await SCLogUtility.LogAndReturn($"Failed to find vBTC V2 contract: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get smart contract
                var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

                if (sc == null)
                    return await SCLogUtility.LogAndReturn($"Failed to find Smart Contract Data: {scUID}", "VBTCService.TransferOwnership()", false);

                if (sc.Features == null)
                    return await SCLogUtility.LogAndReturn($"Contract has no features: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get TokenizationV2 feature
                var tknzFeature = sc.Features.Where(x => x.FeatureName == FeatureName.TokenizationV2).Select(x => x.FeatureFeatures).FirstOrDefault();

                if (tknzFeature == null)
                    return await SCLogUtility.LogAndReturn($"Contract missing a TokenizationV2 feature: {scUID}", "VBTCService.TransferOwnership()", false);

                var tknz = (TokenizationV2Feature)tknzFeature;

                if (tknz == null)
                    return await SCLogUtility.LogAndReturn($"Token feature error: {scUID}", "VBTCService.TransferOwnership()", false);

                // Get smart contract state
                var scState = SmartContractStateTrei.GetSmartContractState(sc.SmartContractUID);

                if (scState == null)
                    return await SCLogUtility.LogAndReturn($"SC State Missing: {scUID}", "VBTCService.TransferOwnership()", false);

                // Normalize destination up front so all checks (incl. xRBX rules) see the
                // resolved address, not an ADNR name.
                toAddress = toAddress.Replace(" ", "").ToAddressNormalize();

                // Check owner account exists. Reserve (xRBX) owners resolve through the
                // reserve store and exit on the reserve lifecycle (unlock delay + callback).
                var account = AccountData.GetSingleAccount(scState.OwnerAddress);
                var rAccount = scState.OwnerAddress.StartsWith("xRBX") ? ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress) : null;

                if (account == null && rAccount == null)
                    return await SCLogUtility.LogAndReturn($"Owner address account not found.", "VBTCService.TransferOwnership()", false);

                bool isReserveOwner = rAccount != null;
                VerifiedXCore.EllipticCurve.PrivateKey? reserveKey = null;
                int reserveUnlockHours = 0;
                if (isReserveOwner)
                {
                    if (toAddress.StartsWith("xRBX"))
                        return await SCLogUtility.LogAndReturn("Reserve-held vBTC contracts can only be transferred to a normal VFX address.", "VBTCService.TransferOwnership()", false);

                    if (!ReserveAccount.TryGetActiveUnlock(scState.OwnerAddress, out var rAUK)) // VX-14: expiry enforced
                        return await SCLogUtility.LogAndReturn("Reserve account is not unlocked. Please unlock it first.", "VBTCService.TransferOwnership()", false);

                    reserveUnlockHours = rAUK!.UnlockTimeHours;
                    reserveKey = rAccount.GetPrivKey;
                }

                // Validate balance > 0 (including state trei tokenization TXs)
                //0 balance check remove for ownership transfers

                if (Globals.VBTCDefaultAssetOnly)
                {
                    // Step over beacons: default image only, nothing to upload.
                    _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, null, "NA", backupURL, isReserveOwner, reserveKey, reserveUnlockHours, TransactionType.TKNZ_TX));
                    var response = JsonConvert.SerializeObject(new { Success = true, Message = isReserveOwner ? "vBTC V2 Contract Transfer has been started (reserve: 24h unlock delay applies, callback-able until then)." : "vBTC V2 Contract Transfer has been started." });
                    SCLogUtility.Log($"SC Process Completed in CLI (beacon-free). SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");
                    return response;
                }

                // Check beacons exist
                if (!Globals.Beacons.Any())
                    return await SCLogUtility.LogAndReturn("Error - You do not have any beacons stored.", "VBTCService.TransferOwnership()", false);

                if (!Globals.Beacon.Values.Where(x => x.IsConnected).Any())
                {
                    var beaconConnectionResult = await BeaconUtility.EstablishBeaconConnection(true, false);
                    if (!beaconConnectionResult)
                    {
                        return await SCLogUtility.LogAndReturn("Error - You failed to connect to any beacons.", "VBTCService.TransferOwnership()", false);
                    }
                }

                var connectedBeacon = Globals.Beacon.Values.Where(x => x.IsConnected).FirstOrDefault();
                if (connectedBeacon == null)
                    return await SCLogUtility.LogAndReturn("Error - You have lost connection to beacons. Please attempt to resend.", "VBTCService.TransferOwnership()", false);

                var localAddress = AccountData.GetSingleAccount(toAddress);

                // Get assets and MD5 list
                var assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);
                var md5List = await MD5Utility.GetMD5FromSmartContract(sc);

                SCLogUtility.Log($"Sending the following assets for upload: {md5List}", "VBTCService.TransferOwnership()");

                // Upload to beacon if recipient is not local
                bool result = false;
                if (localAddress == null)
                {
                    result = await P2PClient.BeaconUploadRequest(connectedBeacon, assets, sc.SmartContractUID, toAddress, md5List).WaitAsync(new TimeSpan(0, 0, 10));
                    SCLogUtility.Log($"SC Beacon Upload Request Completed. SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");
                }
                else
                {
                    result = true;
                }

                if (result == true)
                {
                    // Create asset queue item
                    var aqResult = AssetQueue.CreateAssetQueueItem(sc.SmartContractUID, toAddress, connectedBeacon.Beacons.BeaconLocator, md5List, assets,
                        AssetQueue.TransferType.Upload);
                    SCLogUtility.Log($"SC Asset Queue Items Completed. SCUID: {sc.SmartContractUID}", "VBTCService.TransferOwnership()");

                    if (aqResult)
                    {
                        // Transfer smart contract via standard transfer mechanism
                        _ = Task.Run(() => SmartContractService.TransferSmartContract(sc, toAddress, connectedBeacon, md5List, backupURL, isReserveOwner, reserveKey, reserveUnlockHours, TransactionType.TKNZ_TX));
                        var success = JsonConvert.SerializeObject(new { Success = true, Message = "vBTC V2 Contract Transfer has been started." });
                        SCLogUtility.Log($"SC Process Completed in CLI. SCUID: {sc.SmartContractUID}. Response: {success}", "VBTCService.TransferOwnership()");
                        return success;
                    }
                    else
                    {
                        return await SCLogUtility.LogAndReturn($"Failed to add upload to Asset Queue - TX terminated. Data: scUID: {sc.SmartContractUID} | toAddress: {toAddress} | Locator: {connectedBeacon.Beacons.BeaconLocator} | MD5List: {md5List} | backupURL: {backupURL}", "VBTCService.TransferOwnership()", false);
                    }
                }
                else
                {
                    return await SCLogUtility.LogAndReturn($"Beacon upload failed. Result was : {result}", "VBTCService.TransferOwnership()", false);
                }
            }
            catch (Exception ex)
            {
                return await SCLogUtility.LogAndReturn($"Unknown Error: {ApiErrorText.For(ex)}", "VBTCService.TransferOwnership()", false);
            }
        }

        /// <summary>
        /// Transfer vBTC V2 tokens from one address to another
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="fromAddress">Sender address</param>
        /// <param name="toAddress">Recipient address</param>
        /// <param name="amount">Amount to transfer</param>
        /// <returns>Transaction hash if successful</returns>
        public static async Task<(bool, string)> TransferVBTC(string scUID, string fromAddress, string toAddress, decimal amount)
        {
            try
            {
                // Get account and validate. Reserve (xRBX) senders resolve through the
                // reserve-account store and go out on the reserve lifecycle (unlock delay,
                // callback, recovery) — their only permitted destination is a normal VFX address.
                bool isReserveAccount = fromAddress.StartsWith("xRBX");
                var account = !isReserveAccount ? AccountData.GetSingleAccount(fromAddress) : null;
                var rAccount = isReserveAccount ? ReserveAccount.GetReserveAccountSingle(fromAddress) : null;
                if (account == null && rAccount == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Account not found: {fromAddress}");
                }

                // Normalize before any destination checks so an ADNR name resolving to a
                // reserve address can't slip past the pre-flight (consensus still catches it).
                toAddress = toAddress.ToAddressNormalize();

                long? unlockTime = null;
                if (isReserveAccount)
                {
                    if (toAddress.StartsWith("xRBX"))
                        return (false, "Reserve accounts cannot send vBTC to another Reserve Account.");

                    if (ReserveAccount.TryGetActiveUnlock(fromAddress, out var rAUK)) // VX-14: expiry enforced
                        unlockTime = TimeUtil.GetReserveTime(rAUK!.UnlockTimeHours);
                    else
                        return (false, "Reserve account is no longer unlocked. Please unlock again.");
                }

                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, fromAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.TransferVBTC()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.");
                }

                var availableBalance = balResult.availableBalance;
                if (isReserveAccount)
                    availableBalance -= ReserveTransactions.GetPendingVBTCTransferTotal(fromAddress, scUID);

                if (availableBalance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {availableBalance}, Requested: {amount}", "VBTCService.TransferVBTC()");
                    return (false, $"Insufficient balance. Available: {availableBalance}, Requested: {amount}");
                }

                toAddress = toAddress.ToAddressNormalize();

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "TransferVBTCV2()",
                    ContractUID = scUID,
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = amount
                });

                // Build transaction
                var tokenTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = 0.0M, // No VFX transferred, only vBTC
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                    TransactionType = TransactionType.VBTC_V2_TRANSFER,
                    Data = txData,
                    UnlockTime = unlockTime
                };

                tokenTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tokenTx);

                // Build and sign transaction
                tokenTx.Build();
                var txHash = tokenTx.Hash;
                var privateKey = !isReserveAccount ? account.GetPrivKey : rAccount.GetPrivKey;
                var publicKey = !isReserveAccount ? account.PublicKey : rAccount.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.TransferVBTC()");
                    return (false, $"Private key was null for account {fromAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.TransferVBTC()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                tokenTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(tokenTx);
                if (result.Item1)
                {
                    if (isReserveAccount)
                    {
                        // Reserve dispatch keeps the reserve balance bookkeeping consistent.
                        // noLockUp: true — VFX Amount is 0, only the fee moves.
                        await VerifiedXCore.Services.WalletService.SendReserveTransaction(tokenTx, rAccount, true);
                    }
                    else
                    {
                        await TransactionData.AddTxToWallet(tokenTx, true);
                        await AccountData.UpdateLocalBalance(fromAddress, tokenTx.Fee + tokenTx.Amount);
                        await TransactionData.AddToPool(tokenTx);
                        await P2PClient.SendTXMempool(tokenTx);
                    }
                    SCLogUtility.Log($"vBTC V2 Transfer TX Success. SCUID: {scUID}, TxHash: {tokenTx.Hash}", "VBTCService.TransferVBTC()");
                    return (true, tokenTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Transfer TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.TransferVBTC()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Transfer Error: {ex.Message}", "VBTCService.TransferVBTC()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Data.Function marker for a vBTC V2 multi-contract transfer. Distinct from the dead
        /// legacy "TransferVBTCMulti()" TKNZ-era name so the two shapes can never collide.
        /// </summary>
        public const string MultiTransferFunction = "TransferVBTCMultiV2()";

        /// <summary>
        /// Consensus cap on Inputs entries in one multi-contract transfer. The 30kb TX size cap
        /// also bounds it, but an explicit count is deterministic and cheap to check.
        /// </summary>
        public const int MaxMultiTransferInputs = 25;

        /// <summary>
        /// Per-contract vBTC outflows of a committed/candidate VBTC_V2_TRANSFER, for overspend
        /// accounting: multi-shaped Data (multi Function, no top-level ContractUID) yields one
        /// entry per input; anything else yields the single-shape (ContractUID, Amount) pair.
        /// A hybrid Data (multi Function PLUS top-level ContractUID) deliberately parses as
        /// single — that matches how pre-gate nodes validate/apply it, and post-gate the
        /// validator rejects the hybrid shape outright. Empty list on parse failure.
        /// </summary>
        public static List<(string ScUid, decimal Amount)> GetVbtcV2TransferOutflows(Transaction tx)
        {
            var outflows = new List<(string, decimal)>();
            try
            {
                if (tx.TransactionType != TransactionType.VBTC_V2_TRANSFER || string.IsNullOrEmpty(tx.Data))
                    return outflows;

                var jobj = JObject.Parse(tx.Data);
                var function = jobj["Function"]?.ToObject<string>();
                var scUID = jobj["ContractUID"]?.ToObject<string>();

                if (function == MultiTransferFunction && string.IsNullOrEmpty(scUID))
                {
                    var inputs = jobj["Inputs"]?.ToObject<List<VBTCV2MultiTransferInput>>();
                    if (inputs != null)
                    {
                        foreach (var input in inputs)
                        {
                            if (!string.IsNullOrEmpty(input.SCUID) && input.Amount > 0)
                                outflows.Add((input.SCUID, input.Amount));
                        }
                    }
                    return outflows;
                }

                var amount = jobj["Amount"]?.ToObject<decimal?>();
                if (!string.IsNullOrEmpty(scUID) && amount.HasValue && amount.Value > 0)
                    outflows.Add((scUID, amount.Value));
            }
            catch { }
            return outflows;
        }

        /// <summary>
        /// Per-contract vBTC this sender already has committed in UNMINED transfers sitting in the
        /// local mempool. Both preflights (node-side plan and caller-supplied Inputs) subtract this
        /// so a wallet is never handed a signable TX that the mempool double-spend guard will
        /// reject on submit — the chain is safe either way, but an exchange firing a second multi
        /// before the first mines would otherwise read that rejection as a bug.
        /// </summary>
        public static Dictionary<string, decimal> GetPendingVbtcTransferOutflowsByContract(string fromAddress)
        {
            var pendingByContract = new Dictionary<string, decimal>();
            try
            {
                var pool = TransactionData.GetPool();
                var pendingTxs = pool.Find(x => x.FromAddress == fromAddress && x.TransactionType == TransactionType.VBTC_V2_TRANSFER).ToList();
                foreach (var ptx in pendingTxs)
                {
                    foreach (var (scUid, amt) in GetVbtcV2TransferOutflows(ptx))
                    {
                        pendingByContract.TryGetValue(scUid, out var cur);
                        pendingByContract[scUid] = cur + amt;
                    }
                }
            }
            catch { }
            return pendingByContract;
        }

        /// <summary>
        /// Nonce for a raw (offline-signed) TX. The node's <c>GetNextNonce</c> is mempool-aware, so
        /// sequential build→submit→build is safe on its own; but two builds issued BEFORE either is
        /// submitted both receive the same nonce, and an exchange pipeline will do exactly that. A
        /// caller may therefore supply its own counter. It must not be BELOW the server's next
        /// nonce (that TX could never mine); a gap above it is allowed — whether the chain accepts
        /// an out-of-order nonce at submit is the chain's rule, not this preflight's.
        /// Pure so the rule is unit-testable.
        /// </summary>
        public static (bool Ok, long Nonce, string Error) ResolveRawNonce(long serverNextNonce, long? requestedNonce)
        {
            if (!requestedNonce.HasValue)
                return (true, serverNextNonce, string.Empty);

            if (requestedNonce.Value < serverNextNonce)
                return (false, serverNextNonce, $"Nonce {requestedNonce.Value} is below the next valid nonce for this address ({serverNextNonce}); a lower nonce can never mine.");

            return (true, requestedNonce.Value, string.Empty);
        }

        /// <summary>
        /// Greedy allocation of a total transfer across the sender's spendable contracts: largest
        /// available first (deterministic tie-break on SCUID) so the TX uses the fewest inputs.
        /// Availability matches the single-transfer preflight, minus incomplete withdrawals and any
        /// outflows already pending in the local mempool, so we never craft a TX our own overspend
        /// guards would reject.
        ///
        /// Candidate discovery reads the LOCAL VBTCContractV2 table, so it only sees contracts this
        /// node knows about. Callers serving a REMOTE signer (the raw/offline endpoints) should let
        /// the wallet supply its own Inputs and validate them with
        /// <see cref="ValidateTransferAllocations"/>, which reads the state trei and therefore works
        /// for any contract on any node.
        /// </summary>
        public static async Task<(bool Ok, string Error, List<VBTCV2MultiTransferInput> Allocations)> BuildTransferAllocationPlan(
            string fromAddress, decimal totalAmount)
        {
            var allocations = new List<VBTCV2MultiTransferInput>();
            // Allocate greedily across spendable contracts: largest available first so the TX
            // uses the fewest inputs (deterministic tie-break on SCUID). Availability matches
            // the single-transfer preflight, minus incomplete withdrawals and any outflows
            // already pending in the local mempool so we never craft a TX our own
            // overspend guards would reject.
            var pendingByContract = GetPendingVbtcTransferOutflowsByContract(fromAddress);

            var candidates = new List<(string ScUid, decimal Available)>();
            // The address's contracts from chain state: the local VBTCContractV2 table holds only this node's wallet's
            // contracts, so for a web wallet served by another node (raw builders) its balances were invisible.
            var contracts = VBTCChainView.VaultsFor(fromAddress);
            if (contracts != null)
            {
                foreach (var contract in contracts)
                {
                    var scUid = contract.SmartContractUID;
                    if (string.IsNullOrEmpty(scUid) || candidates.Any(c => c.ScUid == scUid))
                        continue;

                    var balResult = await TryGetAvailableTransparentVbtcBalance(scUid, fromAddress);
                    if (!balResult.success)
                        continue;

                    var available = balResult.availableBalance;
                    available -= VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(fromAddress, scUid);
                    if (pendingByContract.TryGetValue(scUid, out var pendingOut))
                        available -= pendingOut;

                    available = Math.Round(available, 8);
                    if (available > 0)
                        candidates.Add((scUid, available));
                }
            }

            if (!candidates.Any())
                return (false, "No spendable vBTC balance found for this address.", allocations);

            candidates = candidates
                .OrderByDescending(c => c.Available)
                .ThenBy(c => c.ScUid, StringComparer.Ordinal)
                .ToList();

            allocations = new List<VBTCV2MultiTransferInput>();
            var remaining = totalAmount;
            foreach (var candidate in candidates)
            {
                if (remaining <= 0)
                    break;

                var useAmount = Math.Round(Math.Min(remaining, candidate.Available), 8);
                if (useAmount < 0.00000001M)
                    continue;

                allocations.Add(new VBTCV2MultiTransferInput { SCUID = candidate.ScUid, Amount = useAmount });
                remaining -= useAmount;
            }

            if (remaining > 0)
            {
                var totalAvailable = candidates.Sum(c => c.Available);
                return (false, $"Insufficient combined vBTC balance. Available: {totalAvailable}, Requested: {totalAmount}", allocations);
            }
            return (true, string.Empty, allocations);
        }

        /// <summary>
        /// Preflight for CALLER-SUPPLIED transfer allocations (the raw/offline flow, where the
        /// remote wallet knows its own holdings and this node may hold no local record of those
        /// contracts). Mirrors the consensus rules in TransactionValidatorService so the wallet
        /// learns the problem before it signs, and reads balances from the STATE TREI — available on
        /// every node, unlike the local contract table <see cref="BuildTransferAllocationPlan"/>
        /// enumerates.
        /// </summary>
        public static async Task<(bool Ok, string Error)> ValidateTransferAllocations(
            string fromAddress, List<VBTCV2MultiTransferInput>? inputs, decimal totalAmount)
        {
            if (inputs == null || !inputs.Any())
                return (false, "Inputs cannot be empty for multi-contract vBTC transfer.");

            if (inputs.Count > MaxMultiTransferInputs)
                return (false, $"Multi-contract vBTC transfer exceeds the maximum of {MaxMultiTransferInputs} inputs.");

            if (inputs.Select(x => x.SCUID).Distinct().Count() != inputs.Count)
                return (false, "Multi-contract vBTC transfer inputs must reference distinct contracts.");

            decimal sum = 0M;
            foreach (var input in inputs)
            {
                if (string.IsNullOrEmpty(input.SCUID))
                    return (false, "Input SCUID cannot be null for multi-contract vBTC transfer.");
                if (input.Amount <= 0)
                    return (false, "Input amounts must be greater than zero for multi-contract vBTC transfer.");
                if (input.Amount != Math.Round(input.Amount, 8))
                    return (false, "Input amounts cannot have more than 8 decimal places for multi-contract vBTC transfer.");
                sum += input.Amount;
            }

            if (totalAmount != sum)
                return (false, "TotalAmount must equal the sum of input amounts for multi-contract vBTC transfer.");

            // Same availability the node-side plan uses: state balance, minus incomplete
            // withdrawals, minus what this sender already has pending in the local mempool.
            var pendingByContract = GetPendingVbtcTransferOutflowsByContract(fromAddress);

            foreach (var input in inputs)
            {
                if (SmartContractStateTrei.GetSmartContractState(input.SCUID) == null)
                    return (false, $"vBTC V2 contract not found in state trei: {input.SCUID}");

                var balResult = await TryGetAvailableTransparentVbtcBalance(input.SCUID, fromAddress);
                if (!balResult.success)
                    return (false, balResult.error ?? $"Balance lookup failed for contract {input.SCUID}.");

                var available = balResult.availableBalance - VBTCWithdrawalRequest.GetIncompleteWithdrawalAmount(fromAddress, input.SCUID);
                if (pendingByContract.TryGetValue(input.SCUID, out var pendingOut))
                    available -= pendingOut;

                if (available < input.Amount)
                    return (false, $"Insufficient vBTC balance for transfer input {input.SCUID}. Available: {available}, Requested: {input.Amount}"
                        + (pendingOut > 0 ? $" ({pendingOut} already pending in mempool)" : string.Empty));
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// The signed Data payload of a multi-contract TRANSFER. Shared by the local wallet path and
        /// the raw/offline builder so both produce the identical on-chain shape — if these ever
        /// diverge, an offline-signed TX validates differently from a locally signed one.
        /// </summary>
        public static string BuildMultiTransferTxData(
            string fromAddress, string toAddress, decimal totalAmount, List<VBTCV2MultiTransferInput> allocations)
            => JsonConvert.SerializeObject(new
            {
                Function = MultiTransferFunction,
                FromAddress = fromAddress,
                ToAddress = toAddress,
                TotalAmount = totalAmount,
                Inputs = allocations
            });

        /// <summary>
        /// Transfer a total vBTC V2 amount to one recipient, auto-allocated across every contract
        /// the sender holds spendable balance on, as ONE transaction (one nonce, one fee).
        /// Falls back to the plain single-contract path when one contract covers the amount.
        /// Reserve (xRBX) senders are not supported — the reserve deferred-apply lifecycle is
        /// keyed on the single-contract shape.
        /// </summary>
        public static async Task<(bool Success, string Result, List<VBTCV2MultiTransferInput>? Allocations)> TransferVBTCMulti(string fromAddress, string toAddress, decimal totalAmount)
        {
            try
            {
                if (fromAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot use multi-contract vBTC transfers. Send from a single contract instead.", null);

                if (totalAmount <= 0)
                    return (false, "Amount must be greater than zero.", null);

                if (totalAmount != Math.Round(totalAmount, 8))
                    return (false, "Amount cannot have more than 8 decimal places.", null);

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                    return (false, $"Account not found: {fromAddress}", null);

                toAddress = toAddress.ToAddressNormalize();

                var (planOk, planError, allocations) = await BuildTransferAllocationPlan(fromAddress, totalAmount);
                if (!planOk)
                    return (false, planError, null);


                if (allocations.Count == 1)
                {
                    // One contract covers it — use the plain single-contract path (works even
                    // before the multi activation height, and keeps the on-chain shape simple).
                    var singleResult = await TransferVBTC(allocations[0].SCUID, fromAddress, toAddress, totalAmount);
                    return (singleResult.Item1, singleResult.Item2, allocations);
                }

                if (Globals.LastBlock.Height + 1 < Globals.V2TransferMultiHeight)
                    return (false, "Multi-contract vBTC transfers are not active on this network yet. Send from a single contract instead.", null);

                if (allocations.Count > MaxMultiTransferInputs)
                    return (false, $"Transfer would require {allocations.Count} contract inputs (max {MaxMultiTransferInputs}). Send a smaller amount or split it across several transactions.", null);

                var txData = BuildMultiTransferTxData(fromAddress, toAddress, totalAmount, allocations);

                var tokenTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = fromAddress,
                    ToAddress = toAddress,
                    Amount = 0.0M, // No VFX transferred, only vBTC
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                    TransactionType = TransactionType.VBTC_V2_TRANSFER,
                    Data = txData
                };

                tokenTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tokenTx);

                tokenTx.Build();
                var privateKey = account.GetPrivKey;
                if (privateKey == null)
                    return (false, $"Private key was null for account {fromAddress}", null);

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(tokenTx.Hash, privateKey, account.PublicKey);
                if (signature == "ERROR")
                    return (false, "TX Signature Failed.", null);

                tokenTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(tokenTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(tokenTx, true);
                    await AccountData.UpdateLocalBalance(fromAddress, tokenTx.Fee + tokenTx.Amount);
                    await TransactionData.AddToPool(tokenTx);
                    await P2PClient.SendTXMempool(tokenTx);

                    SCLogUtility.Log($"vBTC V2 Multi Transfer TX Success. Inputs: {allocations.Count}, TxHash: {tokenTx.Hash}", "VBTCService.TransferVBTCMulti()");
                    return (true, tokenTx.Hash, allocations);
                }

                SCLogUtility.Log($"vBTC V2 Multi Transfer TX Verify Failed: {result.Item2}", "VBTCService.TransferVBTCMulti()");
                return (false, $"TX Verify Failed: {result.Item2}", null);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Multi Transfer Error: {ex.Message}", "VBTCService.TransferVBTCMulti()");
                return (false, $"Error: {ApiErrorText.For(ex)}", null);
            }
        }

        /// <summary>
        /// Request withdrawal of vBTC to Bitcoin address.
        /// Any address with a vBTC balance in the contract can request a withdrawal — not just the owner.
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="requestorAddress">Address requesting withdrawal (any address with a vBTC balance)</param>
        /// <param name="btcAddress">Bitcoin destination address</param>
        /// <param name="amount">Amount to withdraw</param>
        /// <param name="feeRate">Bitcoin fee rate (sats/vB)</param>
        /// <returns>Withdrawal request transaction hash</returns>
        public static async Task<(bool, string)> RequestWithdrawal(string scUID, string requestorAddress, string btcAddress, decimal amount, int feeRate)
        {
            try
            {
                // Reserve-held vBTC is locked: consensus denies xRBX withdrawal requests.
                // Fail here with the real reason instead of a generic account-not-found.
                if (requestorAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot request BTC withdrawals. Move the vBTC to a normal VFX address first.");

                // VX-01: fail fast with the consensus rule's own message.
                var amountError = GetVbtcAmountError(amount, "vBTC V2 withdrawal request");
                if (amountError != null)
                    return (false, amountError);
                if (feeRate <= 0)
                    return (false, "FeeRate must be greater than zero for vBTC V2 withdrawal request.");

                var concurrencyActive = WithdrawalConcurrencyActive(NextBlockHeight);
                if (concurrencyActive)
                {
                    var floorError = GetWithdrawalFeeFloorError(amount, feeRate, "vBTC V2 withdrawal request");
                    if (floorError != null)
                        return (false, floorError);
                }

                // Get account and validate
                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {requestorAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Account not found: {requestorAddress}");
                }

                // FIND-003 FIX: Check if THIS USER already has an active withdrawal request (per-user tracking).
                // From VbtcWithdrawalConcurrencyHeight a holder may have several open (each escrowed; the pending
                // check below keeps them within the balance) — an exchange pays many customers from one address.
                var existingRequest = concurrencyActive ? null : VBTCWithdrawalRequest.GetActiveRequest(requestorAddress, scUID);
                if (existingRequest != null)
                {
                    SCLogUtility.Log($"Active withdrawal already exists for user {requestorAddress}. Request Hash: {existingRequest.TransactionHash}", "VBTCService.RequestWithdrawal()");
                    return (false, $"You already have an active withdrawal request. Complete it before starting a new one. Request Hash: {existingRequest.TransactionHash}");
                }

                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, requestorAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.RequestWithdrawal()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.");
                }

                if (balResult.availableBalance < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {balResult.availableBalance}, Requested: {amount}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Insufficient balance. Available: {balResult.availableBalance}, Requested: {amount}");
                }

                btcAddress = btcAddress.ToBTCAddressNormalize();

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalRequest()",
                    ContractUID = scUID,
                    RequestorAddress = requestorAddress,
                    BTCAddress = btcAddress,
                    Amount = amount,
                    FeeRate = feeRate
                });

                // Build transaction — FromAddress and ToAddress are both the requestor's address
                var withdrawalTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = requestorAddress,
                    ToAddress = requestorAddress, // The address of the balance owner requesting withdrawal
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(requestorAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    Data = txData
                };

                withdrawalTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(withdrawalTx);

                // Build and sign transaction
                withdrawalTx.Build();
                var txHash = withdrawalTx.Hash;
                
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {requestorAddress}", "VBTCService.RequestWithdrawal()");
                    return (false, $"Private key was null for account {requestorAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.RequestWithdrawal()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                withdrawalTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(withdrawalTx);
                if (result.Item1 && concurrencyActive)
                    result = CheckPendingWithdrawalDebits(withdrawalTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(withdrawalTx, true);
                    await AccountData.UpdateLocalBalance(requestorAddress, withdrawalTx.Fee + withdrawalTx.Amount);
                    await TransactionData.AddToPool(withdrawalTx);
                    await P2PClient.SendTXMempool(withdrawalTx);
                    SCLogUtility.Log($"vBTC V2 Withdrawal Request TX Success. SCUID: {scUID}, TxHash: {withdrawalTx.Hash}", "VBTCService.RequestWithdrawal()");
                    return (true, withdrawalTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Request TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.RequestWithdrawal()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Request Error: {ex.Message}", "VBTCService.RequestWithdrawal()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Local wallet admission: this request together with the requester's other pending transactions must not
        /// debit more than it holds on any contract (the same SameBlockDebitGuard rule peers apply at admission).
        /// With several requests allowed open at once, two requests from one wallet can otherwise both pass VerifyTX,
        /// which reads committed state only.
        /// </summary>
        private static (bool, string) CheckPendingWithdrawalDebits(Transaction withdrawalTx)
        {
            try
            {
                var pending = TransactionData.GetPool().Find(x => x.FromAddress == withdrawalTx.FromAddress && x.Hash != withdrawalTx.Hash).ToList();
                var (ok, reason) = SameBlockDebitGuard.CheckAgainstPending(withdrawalTx, pending);
                return ok ? (true, string.Empty) : (false, $"Together with this address's pending transactions the withdrawal exceeds its balance: {reason}");
            }
            catch (Exception ex)
            {
                return (false, $"Pending-balance check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Data.Function marker for a vBTC V2 multi-contract withdrawal request. Distinct from the
        /// single-shape "VBTCWithdrawalRequest()" so the two can never collide.
        /// </summary>
        public const string MultiWithdrawalFunction = "VBTCWithdrawalRequestMultiV2()";

        /// <summary>
        /// Consensus cap on Inputs entries in one multi-contract withdrawal request. Lower than the
        /// transfer cap on purpose: every input is a separate FROST ceremony AND a separate Bitcoin
        /// transaction with its own miner fee, so a wide fan-out is expensive for the user and slow
        /// to settle. The 30kb TX size cap also bounds it, but an explicit count is deterministic.
        /// </summary>
        public const int MaxMultiWithdrawalInputs = 10;

        /// <summary>
        /// Per-contract vBTC outflows of a committed/candidate VBTC_V2_WITHDRAWAL_REQUEST, for the
        /// per-contract mempool conflict guard and local availability math: multi-shaped Data
        /// (multi Function, no top-level ContractUID) yields one entry per input; anything else
        /// yields the single-shape (ContractUID, Amount) pair. A hybrid Data (multi Function PLUS
        /// top-level ContractUID) deliberately parses as single — that matches how pre-gate nodes
        /// validate/apply it, and post-gate the validator rejects the hybrid shape outright.
        /// Empty list on parse failure. Mirrors <see cref="GetVbtcV2TransferOutflows"/>.
        /// </summary>
        public static List<(string ScUid, decimal Amount)> GetVbtcV2WithdrawalOutflows(Transaction tx)
        {
            var outflows = new List<(string, decimal)>();
            try
            {
                if (tx.TransactionType != TransactionType.VBTC_V2_WITHDRAWAL_REQUEST || string.IsNullOrEmpty(tx.Data))
                    return outflows;

                var jobj = JObject.Parse(tx.Data);
                var function = jobj["Function"]?.ToObject<string>();
                var scUID = jobj["ContractUID"]?.ToObject<string>();

                if (function == MultiWithdrawalFunction && string.IsNullOrEmpty(scUID))
                {
                    var inputs = jobj["Inputs"]?.ToObject<List<VBTCV2MultiWithdrawalInput>>();
                    if (inputs != null)
                    {
                        foreach (var input in inputs)
                        {
                            if (!string.IsNullOrEmpty(input.SCUID) && input.Amount > 0)
                                outflows.Add((input.SCUID, input.Amount));
                        }
                    }
                    return outflows;
                }

                var amount = jobj["Amount"]?.ToObject<decimal?>();
                if (!string.IsNullOrEmpty(scUID) && amount.HasValue && amount.Value > 0)
                    outflows.Add((scUID, amount.Value));
            }
            catch { }
            return outflows;
        }

        /// <summary>
        /// Request withdrawal of a total vBTC amount to ONE Bitcoin address, auto-allocated across
        /// every contract the requestor holds spendable balance on, as a single VFX transaction
        /// (one nonce, one VFX fee). Falls back to the plain single-contract path when one contract
        /// covers the amount.
        ///
        /// Each allocated contract is a separate Bitcoin vault, so completion later pays out one
        /// Bitcoin transaction per contract (see <see cref="CompleteWithdrawalMulti"/>) — the user
        /// pays N miner fees and the payout can settle partially. That is the deliberate trade for
        /// keeping one vault per Bitcoin transaction, which FROST authorization requires.
        /// Reserve (xRBX) requestors are not supported — consensus denies them withdrawals outright.
        /// </summary>
        public static async Task<(bool Success, string Result, List<VBTCV2MultiWithdrawalInput>? Allocations)> RequestWithdrawalMulti(
            string requestorAddress, string btcAddress, decimal totalAmount, int feeRate)
        {
            try
            {
                if (requestorAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot request BTC withdrawals. Move the vBTC to a normal VFX address first.", null);

                if (totalAmount <= 0)
                    return (false, "Amount must be greater than zero.", null);

                if (totalAmount != Math.Round(totalAmount, 8))
                    return (false, "Amount cannot have more than 8 decimal places.", null);

                if (feeRate <= 0)
                    return (false, "Fee rate must be greater than zero.", null);

                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                    return (false, $"Account not found: {requestorAddress}", null);

                btcAddress = btcAddress.ToBTCAddressNormalize();

                var currentHeight = Globals.LastBlock?.Height ?? 0;

                var (planOk, planError, allocations) = await BuildWithdrawalAllocationPlan(requestorAddress, totalAmount);
                if (!planOk)
                    return (false, planError, null);

                // Every allocation is its own Bitcoin transaction paying its own fee.
                var multiConcurrencyActive = WithdrawalConcurrencyActive(NextBlockHeight);
                if (multiConcurrencyActive)
                {
                    foreach (var allocation in allocations)
                    {
                        var floorError = GetWithdrawalFeeFloorError(allocation.Amount, feeRate, $"Withdrawal share from contract {allocation.SCUID}");
                        if (floorError != null)
                            return (false, $"{floorError} (The total was split across {allocations.Count} contracts; each share pays its own Bitcoin fee.)", allocations);
                    }
                }

                if (allocations.Count == 1)
                {
                    // One vault covers it — use the plain single-contract path (keeps the on-chain
                    // shape simple).
                    var singleResult = await RequestWithdrawal(allocations[0].SCUID, requestorAddress, btcAddress, totalAmount, feeRate);
                    return (singleResult.Item1, singleResult.Item2, allocations);
                }

                if (allocations.Count > MaxMultiWithdrawalInputs)
                    return (false, $"Withdrawal would require {allocations.Count} contract inputs (max {MaxMultiWithdrawalInputs}). Withdraw a smaller amount or split it across several requests.", null);

                var txData = BuildMultiWithdrawalTxData(requestorAddress, btcAddress, totalAmount, feeRate, allocations);

                var withdrawalTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = requestorAddress,
                    ToAddress = requestorAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(requestorAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                    Data = txData
                };

                withdrawalTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(withdrawalTx);
                withdrawalTx.Build();

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                    return (false, $"Private key was null for account {requestorAddress}", null);

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(withdrawalTx.Hash, privateKey, publicKey);
                if (signature == "ERROR")
                    return (false, "TX Signature Failed.", null);

                withdrawalTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(withdrawalTx);
                if (result.Item1 && multiConcurrencyActive)
                    result = CheckPendingWithdrawalDebits(withdrawalTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(withdrawalTx, true);
                    await AccountData.UpdateLocalBalance(requestorAddress, withdrawalTx.Fee + withdrawalTx.Amount);
                    await TransactionData.AddToPool(withdrawalTx);
                    await P2PClient.SendTXMempool(withdrawalTx);

                    SCLogUtility.Log($"vBTC V2 Multi Withdrawal Request TX Success. Inputs: {allocations.Count}, TxHash: {withdrawalTx.Hash}", "VBTCService.RequestWithdrawalMulti()");
                    return (true, withdrawalTx.Hash, allocations);
                }

                SCLogUtility.Log($"vBTC V2 Multi Withdrawal Request TX Verify Failed: {result.Item2}", "VBTCService.RequestWithdrawalMulti()");
                return (false, $"TX Verify Failed: {result.Item2}", null);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Multi Withdrawal Request Error: {ex.Message}", "VBTCService.RequestWithdrawalMulti()");
                return (false, $"Error: {ApiErrorText.For(ex)}", null);
            }
        }

        /// <summary>
        /// Greedy allocation of a total withdrawal across the contracts this address can actually
        /// withdraw from right now: largest available balance first (deterministic tie-break on
        /// SCUID) so the withdrawal uses the fewest vaults — and therefore the fewest Bitcoin
        /// transactions and miner fees. Before VbtcWithdrawalConcurrencyHeight, contracts consensus
        /// would reject the requestor on (an active request on the contract, the repeat-request
        /// cooldown, a request already pending in our mempool) are skipped; from it, the requester's
        /// own pending requests are subtracted from what each contract has left for it instead.
        ///
        /// Candidate discovery reads the LOCAL VBTCContractV2 table, so this only sees contracts
        /// this node knows about. Callers serving a remote signer (the raw/offline endpoints)
        /// should let that wallet supply its own Inputs and validate them with
        /// <see cref="ValidateWithdrawalAllocations"/>, which reads the state trei and therefore
        /// works for any contract on any node.
        /// </summary>
        public static async Task<(bool Ok, string Error, List<VBTCV2MultiWithdrawalInput> Allocations)> BuildWithdrawalAllocationPlan(
            string requestorAddress, decimal totalAmount)
        {
            var allocations = new List<VBTCV2MultiWithdrawalInput>();
            var currentHeight = Globals.LastBlock?.Height ?? 0;
            var concurrencyActive = WithdrawalConcurrencyActive(NextBlockHeight);

            // Before VbtcWithdrawalConcurrencyHeight any pending request locks its contract (skip those). From it,
            // only this requester's own pending requests matter, and they reduce what the contract has left for it.
            var pendingContracts = new HashSet<string>(StringComparer.Ordinal);
            var ownPendingByContract = new Dictionary<string, decimal>(StringComparer.Ordinal);
            try
            {
                var pool = TransactionData.GetPool();
                foreach (var ptx in pool.Find(x => x.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_REQUEST).ToList())
                {
                    foreach (var (scUid, amt) in GetVbtcV2WithdrawalOutflows(ptx))
                    {
                        pendingContracts.Add(scUid);
                        if (ptx.FromAddress == requestorAddress)
                            ownPendingByContract[scUid] = (ownPendingByContract.TryGetValue(scUid, out var sum) ? sum : 0M) + amt;
                    }
                }
            }
            catch { }

            var candidates = new List<(string ScUid, decimal Available)>();
            // The address's contracts from chain state: the local VBTCContractV2 table holds only this node's wallet's
            // contracts, so for a web wallet served by another node (raw builders) its balances were invisible.
            var contracts = VBTCChainView.VaultsFor(requestorAddress);
            if (contracts != null)
            {
                foreach (var contract in contracts)
                {
                    var scUid = contract.SmartContractUID;
                    if (string.IsNullOrEmpty(scUid) || candidates.Any(c => c.ScUid == scUid))
                        continue;

                    if (!concurrencyActive)
                    {
                        if (pendingContracts.Contains(scUid))
                            continue;
                        if (VBTCWithdrawalRequest.HasActiveContractRequest(scUid, currentHeight, includeLocalOnlyRows: true))
                            continue;
                        if (VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(requestorAddress, scUid, currentHeight))
                            continue;
                    }

                    var balResult = await TryGetAvailableTransparentVbtcBalance(scUid, requestorAddress);
                    if (!balResult.success)
                        continue;

                    var available = Math.Round(balResult.availableBalance - (concurrencyActive && ownPendingByContract.TryGetValue(scUid, out var ownPending) ? ownPending : 0M), 8);
                    if (available > 0)
                        candidates.Add((scUid, available));
                }
            }

            if (!candidates.Any())
                return (false, "No spendable vBTC balance found for this address.", allocations);

            candidates = candidates
                .OrderByDescending(c => c.Available)
                .ThenBy(c => c.ScUid, StringComparer.Ordinal)
                .ToList();

            var remaining = totalAmount;
            foreach (var candidate in candidates)
            {
                if (remaining <= 0)
                    break;

                var useAmount = Math.Round(Math.Min(remaining, candidate.Available), 8);
                if (useAmount < 0.00000001M)
                    continue;

                allocations.Add(new VBTCV2MultiWithdrawalInput { SCUID = candidate.ScUid, Amount = useAmount });
                remaining -= useAmount;
            }

            if (remaining > 0)
            {
                var totalAvailable = candidates.Sum(c => c.Available);
                return (false, $"Insufficient combined vBTC balance. Available: {totalAvailable}, Requested: {totalAmount}", allocations);
            }

            return (true, string.Empty, allocations);
        }

        /// <summary>
        /// Preflight for CALLER-SUPPLIED allocations (the raw/offline flow, where the remote wallet
        /// knows its own holdings and this node may hold no local record of those contracts). Mirrors
        /// the consensus rules in TransactionValidatorService so the wallet learns the problem before
        /// it signs, and reads balances from the STATE TREI — available on every node, unlike the
        /// local contract table <see cref="BuildWithdrawalAllocationPlan"/> enumerates.
        /// </summary>
        public static async Task<(bool Ok, string Error)> ValidateWithdrawalAllocations(
            string requestorAddress, List<VBTCV2MultiWithdrawalInput>? inputs, decimal totalAmount, int feeRate = 0)
        {
            var concurrencyActive = WithdrawalConcurrencyActive(NextBlockHeight);
            if (inputs == null || !inputs.Any())
                return (false, "Inputs cannot be empty for multi-contract vBTC withdrawal.");

            if (inputs.Count > MaxMultiWithdrawalInputs)
                return (false, $"Multi-contract vBTC withdrawal exceeds the maximum of {MaxMultiWithdrawalInputs} inputs.");

            if (inputs.Select(x => x.SCUID).Distinct().Count() != inputs.Count)
                return (false, "Multi-contract vBTC withdrawal inputs must reference distinct contracts.");

            decimal sum = 0M;
            foreach (var input in inputs)
            {
                if (string.IsNullOrEmpty(input.SCUID))
                    return (false, "Input SCUID cannot be null for multi-contract vBTC withdrawal.");
                if (input.Amount <= 0)
                    return (false, "Input amounts must be greater than zero for multi-contract vBTC withdrawal.");
                if (input.Amount != Math.Round(input.Amount, 8))
                    return (false, "Input amounts cannot have more than 8 decimal places for multi-contract vBTC withdrawal.");
                if (concurrencyActive)
                {
                    var floorError = GetWithdrawalFeeFloorError(input.Amount, feeRate, $"Multi-contract vBTC withdrawal input {input.SCUID}");
                    if (floorError != null)
                        return (false, floorError);
                }
                sum += input.Amount;
            }

            if (totalAmount != sum)
                return (false, "TotalAmount must equal the sum of input amounts for multi-contract vBTC withdrawal.");

            var currentHeight = Globals.LastBlock?.Height ?? 0;

            foreach (var input in inputs)
            {
                if (SmartContractStateTrei.GetSmartContractState(input.SCUID) == null)
                    return (false, $"vBTC V2 contract not found in state trei: {input.SCUID}");

                if (!concurrencyActive && VBTCWithdrawalRequest.HasActiveContractRequest(input.SCUID, currentHeight, includeLocalOnlyRows: false))
                    return (false, $"A withdrawal is already in progress for contract {input.SCUID}; try again once it completes.");

                if (!concurrencyActive && VBTCWithdrawalRequest.IsRequestorInRepeatCooldown(requestorAddress, input.SCUID, currentHeight))
                    return (false, $"Requestor {requestorAddress} is in the repeat-request cooldown for contract {input.SCUID}.");

                var balResult = await TryGetAvailableTransparentVbtcBalance(input.SCUID, requestorAddress);
                if (!balResult.success)
                    return (false, balResult.error ?? $"Balance lookup failed for contract {input.SCUID}.");

                if (balResult.availableBalance < input.Amount)
                    return (false, $"Insufficient vBTC balance for withdrawal input {input.SCUID}. Available: {balResult.availableBalance}, Requested: {input.Amount}");
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// The signed Data payload of a multi-contract withdrawal REQUEST. Shared by the local
        /// wallet path and the raw/offline builder so both produce the identical on-chain shape —
        /// if these ever diverge, an offline-signed TX validates differently from a locally signed
        /// one. UniqueId is carried so a RequestWithdrawalRaw pre-registration row is UPDATED at
        /// mine time rather than duplicated; validators ignore fields they do not know.
        /// </summary>
        public static string BuildMultiWithdrawalTxData(
            string requestorAddress, string btcAddress, decimal totalAmount, int feeRate,
            List<VBTCV2MultiWithdrawalInput> allocations, string? uniqueId = null)
            => JsonConvert.SerializeObject(new
            {
                Function = MultiWithdrawalFunction,
                RequestorAddress = requestorAddress,
                BTCAddress = btcAddress,
                TotalAmount = totalAmount,
                FeeRate = feeRate,
                Inputs = allocations,
                UniqueId = !string.IsNullOrEmpty(uniqueId) ? uniqueId : null
            });

        /// <summary>Per-contract outcome of a multi-contract withdrawal completion.</summary>
        public class MultiWithdrawalCompletionResult
        {
            public string SmartContractUID { get; set; } = "";
            public decimal Amount { get; set; }
            public bool Success { get; set; }
            public string? VFXTxHash { get; set; }
            public string? BTCTxHash { get; set; }
            public string? ErrorMessage { get; set; }
            /// <summary>True when the contract's share was already completed before this call.</summary>
            public bool AlreadyComplete { get; set; }
        }

        /// <summary>
        /// Complete every outstanding share of a (possibly multi-contract) withdrawal request: for
        /// each contract row minted by the REQUEST tx, run its own FROST ceremony, broadcast its own
        /// Bitcoin transaction and mine its own VBTC_V2_WITHDRAWAL_COMPLETE.
        ///
        /// Deliberately NOT all-or-nothing, mirroring the bridge BTC-exit executor: one vault's
        /// ceremony failing (unreachable validators, Electrum blip, thin UTXO set) must not strand
        /// the vaults that can pay. Failed shares stay open and are simply retried by calling this
        /// again — each share is independently pinned, so a retry re-signs the identical Bitcoin
        /// transaction rather than racing a second one.
        /// </summary>
        public static async Task<(bool AllSucceeded, List<MultiWithdrawalCompletionResult> Results)> CompleteWithdrawalMulti(
            string withdrawalRequestHash)
        {
            var results = new List<MultiWithdrawalCompletionResult>();

            var rows = VBTCWithdrawalRequest.GetAllByTransactionHash(withdrawalRequestHash);
            if (!rows.Any())
            {
                results.Add(new MultiWithdrawalCompletionResult
                {
                    Success = false,
                    ErrorMessage = $"Withdrawal request not found for hash: {withdrawalRequestHash}"
                });
                return (false, results);
            }

            foreach (var row in rows)
            {
                if (row.IsCompleted)
                {
                    results.Add(new MultiWithdrawalCompletionResult
                    {
                        SmartContractUID = row.SmartContractUID,
                        Amount = row.Amount,
                        Success = true,
                        AlreadyComplete = true,
                        BTCTxHash = row.BTCTxHash
                    });
                    continue;
                }

                try
                {
                    var (success, vfxTxHash, btcTxHash, error, _) = await CompleteWithdrawal(row.SmartContractUID, withdrawalRequestHash);

                    results.Add(new MultiWithdrawalCompletionResult
                    {
                        SmartContractUID = row.SmartContractUID,
                        Amount = row.Amount,
                        Success = success,
                        VFXTxHash = success ? vfxTxHash : null,
                        BTCTxHash = success ? btcTxHash : null,
                        ErrorMessage = success ? null : error
                    });

                    if (!success)
                    {
                        SCLogUtility.Log($"Multi withdrawal share failed for contract {row.SmartContractUID} of request {withdrawalRequestHash}: {error}. Other shares continue; retry this one by calling CompleteWithdrawalMulti again.",
                            "VBTCService.CompleteWithdrawalMulti()");
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new MultiWithdrawalCompletionResult
                    {
                        SmartContractUID = row.SmartContractUID,
                        Amount = row.Amount,
                        Success = false,
                        ErrorMessage = $"Error: {ex.Message}"
                    });
                }
            }

            var allSucceeded = results.All(r => r.Success);
            SCLogUtility.Log($"Multi withdrawal completion for {withdrawalRequestHash}: {results.Count(r => r.Success)}/{results.Count} share(s) paid.",
                "VBTCService.CompleteWithdrawalMulti()");

            return (allSucceeded, results);
        }

        /// <summary>
        /// Complete withdrawal by coordinating FROST signing and broadcasting Bitcoin transaction
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="withdrawalRequestHash">Hash of withdrawal request transaction</param>
        /// <returns>Completion transaction hash and Bitcoin transaction hash</returns>
        public static async Task<(bool Success, string VFXTxHash, string BTCTxHash, string ErrorMessage, FROST.Models.FrostCeremonyOutcome? Ceremony)> CompleteWithdrawal(
            string scUID, string withdrawalRequestHash,
            decimal? delegatedAmount = null, string? delegatedBTCDestination = null, int? delegatedFeeRate = null,
            bool signOnly = false,
            FROST.Models.PreSignedLeaderAuth? preSignedAuth = null)
        {
            try
            {
                // Unified MPC: Any VFX wallet owner can now coordinate FROST signing directly.
                // The leader check in FrostStartup.cs has been relaxed to accept any valid VFX
                // signature, and FrostMPCService signs with AddressSignature (owner's key).

                // Get contract — try local DB first, fall back to State Trei for remote validators
                var vbtcContract = VBTCContractV2.GetContract(scUID);
                bool hasLocalContract = vbtcContract != null;

                // If local DB doesn't have the contract (e.g. remote validator node), reconstruct
                // the needed data from the State Trei + SmartContractMain which all nodes share.
                string depositAddress = null;
                int totalRegisteredValidators = 0;
                long lastValidatorActivityBlock = 0;
                List<string> snapshotAddresses = null; // DKG participant addresses from the contract

                if (vbtcContract != null)
                {
                    depositAddress = vbtcContract.DepositAddress;
                    totalRegisteredValidators = vbtcContract.TotalRegisteredValidators;
                    lastValidatorActivityBlock = vbtcContract.LastValidatorActivityBlock;
                    
                    // Try to get snapshot from local contract first
                    // If not available locally, we'll try State Trei below
                }
                
                // Always try to resolve the snapshot from State Trei (authoritative source for DKG participants)
                {
                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                    if (scStateTreiRec != null && !string.IsNullOrEmpty(scStateTreiRec.ContractData))
                    {
                        try
                        {
                            var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scStateTreiRec.ContractData);
                            if (scMainDecompile?.Features != null)
                            {
                                var tknzFeature = scMainDecompile.Features
                                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                                    .Select(x => x.FeatureFeatures)
                                    .FirstOrDefault();

                                if (tknzFeature is TokenizationV2Feature tknz)
                                {
                                    snapshotAddresses = tknz.ValidatorAddressesSnapshot;
                                    
                                    // If we didn't have a local contract, fill in from State Trei
                                    if (vbtcContract == null)
                                    {
                                        depositAddress = tknz.DepositAddress;
                                        totalRegisteredValidators = tknz.ValidatorAddressesSnapshot?.Count ?? 0;
                                        lastValidatorActivityBlock = tknz.ProofBlockHeight;
                                        SCLogUtility.Log($"Resolved from State Trei — DepositAddress: {depositAddress}, Validators: {totalRegisteredValidators}, ActivityBlock: {lastValidatorActivityBlock}", "VBTCService.CompleteWithdrawal()");
                                    }
                                }
                            }
                        }
                        catch (Exception decompileEx)
                        {
                            SCLogUtility.Log($"Failed to decompile contract from State Trei: {decompileEx.Message}", "VBTCService.CompleteWithdrawal()");
                        }
                    }
                    else if (vbtcContract == null)
                    {
                        SCLogUtility.Log($"Smart contract state not found in State Trei and no local contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"vBTC V2 contract not found in local DB or State Trei: {scUID}", null);
                    }
                }

                if (string.IsNullOrEmpty(depositAddress))
                {
                    SCLogUtility.Log($"Deposit address is empty for contract: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Deposit address not found for contract: {scUID}", null);
                }

                // FIND-003 FIX: Look up withdrawal request using per-user tracking.
                // VBTCWithdrawalRequest is a local DB record — remote validators may not have it if
                // StateData hasn't saved it yet or if the TX was processed differently on that node.
                // When the local lookup fails, fall back to delegated params passed from the requesting node.
                // Contract-scoped: a multi-contract REQUEST mints one row per input under this same
                // hash, and this call completes exactly ONE of them (its own Bitcoin transaction).
                var withdrawalRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                var isTransientRequest = withdrawalRequest == null; // synthesized rows have no DB home for pinned-build persistence
                if (withdrawalRequest == null)
                {
                    // Check if we have delegated withdrawal details from the requesting node
                    if (delegatedAmount.HasValue && delegatedAmount.Value > 0 && !string.IsNullOrEmpty(delegatedBTCDestination))
                    {
                        SCLogUtility.Log($"Withdrawal request not in local DB for hash: {withdrawalRequestHash}. Using delegated params: Amount={delegatedAmount.Value}, Dest={delegatedBTCDestination}, FeeRate={delegatedFeeRate}", 
                            "VBTCService.CompleteWithdrawal()");
                        
                        // Create a transient withdrawal request from delegated data (not saved to DB)
                        withdrawalRequest = new VBTCWithdrawalRequest
                        {
                            TransactionHash = withdrawalRequestHash,
                            SmartContractUID = scUID,
                            Amount = delegatedAmount.Value,
                            BTCDestination = delegatedBTCDestination,
                            FeeRate = delegatedFeeRate ?? 10,
                            IsCompleted = false,
                            Status = VBTCWithdrawalStatus.Requested
                        };
                    }
                    else
                    {
                        SCLogUtility.Log($"Withdrawal request not found for hash: {withdrawalRequestHash} and no delegated params provided", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, $"Withdrawal request not found for hash: {withdrawalRequestHash}", null);
                    }
                }

                // Validate the request is not already completed
                if (withdrawalRequest.IsCompleted)
                {
                    SCLogUtility.Log($"Withdrawal request already completed: {withdrawalRequestHash}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Withdrawal request already completed: {withdrawalRequestHash}", null);
                }

                // Reclaim deferral (VbtcWithdrawalConcurrencyHeight): this withdrawal's tx took back coins of a
                // withheld transaction, which could still replace it until it confirms, so its COMPLETE waits.
                if (!signOnly && !isTransientRequest && !string.IsNullOrEmpty(withdrawalRequest.CompleteAfterBtcTxId))
                {
                    var (deferredAction, deferredMessage) = await ResolveDeferredCompletion(withdrawalRequest, depositAddress);
                    if (deferredAction == DeferredCompletionAction.Submit)
                        return await SubmitWithdrawalCompletion(scUID, withdrawalRequestHash, withdrawalRequest,
                            withdrawalRequest.Amount, withdrawalRequest.BTCDestination, withdrawalRequest.CompleteAfterBtcTxId!, vbtcContract);
                    if (deferredAction == DeferredCompletionAction.Wait)
                        return (false, string.Empty, withdrawalRequest.CompleteAfterBtcTxId!, deferredMessage, null);
                    // Rebuild: our tx lost to the withheld one (which paid ITS withdrawal); build a new one below.
                }

                // A request whose amount cannot cover the smallest withdrawal tx at its own fee rate can never be
                // paid (the builder refuses it on every attempt). Say so now, and point at the only way out.
                // Requests mined before VbtcWithdrawalConcurrencyHeight's fee floor can be like this.
                if (!isTransientRequest)
                {
                    var unpayable = GetUnpayableWithdrawalMessage(withdrawalRequest);
                    if (unpayable != null)
                    {
                        SCLogUtility.Log($"Withdrawal {withdrawalRequestHash} on {scUID} can never be paid; not starting a ceremony.", "VBTCService.CompleteWithdrawal()");
                        return (false, string.Empty, string.Empty, unpayable, null);
                    }
                }

                // ============================================================
                // FROST INTEGRATION: Execute Bitcoin Withdrawal Transaction
                // ============================================================
                
                SCLogUtility.Log($"Starting FROST withdrawal for contract {scUID}", "VBTCService.CompleteWithdrawal()");

                // Get validators for FROST signing — use the contract's DKG snapshot (only those validators
                // hold FROST key shares), filtered by the registry (to get IPs) and reachability probe.
                var allRegistryValidators = VBTCValidatorRegistry.GetActiveValidators();
                if (allRegistryValidators == null || !allRegistryValidators.Any())
                {
                    SCLogUtility.Log($"No active validators in registry for FROST signing", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "No active validators in registry for FROST signing", null);
                }

                // Filter to only validators from the contract's DKG snapshot (they hold the key shares)
                List<VBTCValidator> snapshotValidators;
                if (snapshotAddresses != null && snapshotAddresses.Any())
                {
                    var snapshotSet = new HashSet<string>(snapshotAddresses);
                    snapshotValidators = allRegistryValidators.Where(v => snapshotSet.Contains(v.ValidatorAddress)).ToList();
                    SCLogUtility.Log($"[FROST Signing] Filtered to {snapshotValidators.Count} snapshot validators " +
                        $"(from {allRegistryValidators.Count} registry, {snapshotAddresses.Count} in snapshot)", "VBTCService.CompleteWithdrawal()");
                }
                else
                {
                    // Fallback: no snapshot — use PUBLIC validators only (§7.2: never pull S3C
                    // validators into a legacy public contract).
                    snapshotValidators = VBTCValidatorRegistry.GetPublicValidators();
                    SCLogUtility.Log($"[FROST Signing] WARNING: No snapshot addresses found for contract {scUID}. " +
                        $"Using all {allRegistryValidators.Count} registry validators (legacy fallback)", "VBTCService.CompleteWithdrawal()");
                }

                if (!snapshotValidators.Any())
                {
                    SCLogUtility.Log($"No snapshot validators found in registry for FROST signing. " +
                        $"Snapshot had {snapshotAddresses?.Count ?? 0} addresses but none matched active registry.", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "No DKG snapshot validators are currently active in the registry", null);
                }

                // Probe reachability — only contact validators that are actually online
                var validators = await FrostMPCService.ProbeValidatorReachability(snapshotValidators);
                if (!validators.Any())
                {
                    SCLogUtility.Log($"No reachable validators for FROST signing (0/{snapshotValidators.Count} responded to health check)", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"No reachable validators for FROST signing (0/{snapshotValidators.Count} snapshot validators online)", null);
                }

                // Use the snapshot total (not registry total) for threshold calculation
                int snapshotTotal = snapshotAddresses?.Count ?? totalRegisteredValidators;

                // Calculate DYNAMIC adjusted threshold based on validator availability
                int adjustedThreshold = VBTCThresholdCalculator.CalculateAdjustedThreshold(
                    snapshotTotal,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                
                // Calculate required validators based on adjusted threshold
                int requiredValidators = VBTCThresholdCalculator.CalculateRequiredValidators(adjustedThreshold, validators.Count);
                
                // Log threshold information
                string thresholdInfo = VBTCThresholdCalculator.GetThresholdExplanation(
                    snapshotTotal,
                    validators.Count,
                    lastValidatorActivityBlock,
                    Globals.LastBlock.Height
                );
                SCLogUtility.Log($"Threshold calculation (snapshot-based): {thresholdInfo}", "VBTCService.CompleteWithdrawal()");
                
                if (validators.Count < requiredValidators)
                {
                    SCLogUtility.Log($"Insufficient reachable validators. Have: {validators.Count}, Need: {requiredValidators} " +
                        $"(Adjusted threshold: {adjustedThreshold}%, Snapshot: {snapshotTotal})", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, $"Insufficient reachable validators. Have: {validators.Count}, Need: {requiredValidators} (Adjusted threshold: {adjustedThreshold}%)", null);
                }

                // Withdrawal details come from THIS request's row (keyed by request hash + contract). The contract's
                // single Active* slot holds whichever request was mined last, so with several requests open on one
                // contract it would pay this withdrawal with another holder's amount and destination. It is only a
                // fallback for a row that carries no details.
                decimal withdrawalAmount;
                string btcDestination;

                if (withdrawalRequest.Amount > 0 && !string.IsNullOrEmpty(withdrawalRequest.BTCDestination))
                {
                    withdrawalAmount = withdrawalRequest.Amount;
                    btcDestination = withdrawalRequest.BTCDestination;
                }
                else if (hasLocalContract
                    && vbtcContract!.ActiveWithdrawalRequestHash == withdrawalRequestHash
                    && vbtcContract.ActiveWithdrawalAmount.HasValue && vbtcContract.ActiveWithdrawalAmount.Value > 0
                    && !string.IsNullOrEmpty(vbtcContract.ActiveWithdrawalBTCDestination))
                {
                    SCLogUtility.Log($"Withdrawal request row has no details; using the contract's Active* fields for this same request. Amount: {vbtcContract.ActiveWithdrawalAmount}, Dest: {vbtcContract.ActiveWithdrawalBTCDestination}",
                        "VBTCService.CompleteWithdrawal()");
                    withdrawalAmount = vbtcContract.ActiveWithdrawalAmount.Value;
                    btcDestination = vbtcContract.ActiveWithdrawalBTCDestination;
                }
                else
                {
                    SCLogUtility.Log($"Invalid withdrawal details in both contract and request record", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, string.Empty, "Invalid withdrawal details — amount/destination not found in contract or request record", null);
                }
                long feeRate = withdrawalRequest.FeeRate != 0 ? withdrawalRequest.FeeRate : 10; // Default fee rate (sats/vB) - TODO: Get from withdrawal request

                // ── FIND-028 retry determinism ─────────────────────────────────────────────
                // If a previous attempt already announced a tx for this withdrawal, reuse it
                // verbatim (validators allow idempotent same-sighash re-signs). A rebuild that
                // selects a different equal-value UTXO — or a different input order — changes
                // the sighashes and is correctly refused by the validators' sighash pin.
                string? pinnedUnsignedTxHex = null;
                List<PinnedWithdrawalCoin>? pinnedCoins = null;
                HashSet<string>? preferredOutpoints = null;

                if (!string.IsNullOrEmpty(withdrawalRequest.PinnedUnsignedTxHex) && !string.IsNullOrEmpty(withdrawalRequest.PinnedCoinsJson))
                {
                    var parsedPinnedCoins = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(withdrawalRequest.PinnedCoinsJson);
                    if (parsedPinnedCoins is { Count: > 0 })
                    {
                        // Stale-pin escape: reuse only while ≥1 pinned outpoint is still unspent.
                        // If ALL are gone, the pinned tx either CONFIRMED (this withdrawal is
                        // already paid — never sign another tx for it) or was conflicted away
                        // (safe to rebuild fresh). Inconclusive lookups → reuse (always safe) —
                        // including an UNKNOWN confirmation answer: rebuilding on "Electrum couldn't
                        // tell" produced a non-conflicting tx that pin-holding validators 409'd.
                        var pinLookup = await BitcoinTransactionService.GetTaprootUTXOs(depositAddress);
                        var anyPinnedUnspent = pinLookup.Success && parsedPinnedCoins.Any(c =>
                            pinLookup.Utxos.Any(u => $"{u.TxHash}:{u.TxPos}".ToLowerInvariant() == c.ToOutpointKey()));

                        var pinnedTxId = withdrawalRequest.LastSignedBtcTxId;
                        int? pinnedTxConfirmations = 0;
                        if (pinLookup.Success && !anyPinnedUnspent && !string.IsNullOrEmpty(pinnedTxId))
                        {
                            var confLookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(pinnedTxId);
                            pinnedTxConfirmations = confLookup.Confirmations;
                        }

                        var disposition = DecidePinnedTxDisposition(pinLookup.Success, anyPinnedUnspent, pinnedTxConfirmations);
                        switch (disposition)
                        {
                            case PinnedReuseDecision.ReusePinned:
                                pinnedUnsignedTxHex = withdrawalRequest.PinnedUnsignedTxHex;
                                pinnedCoins = parsedPinnedCoins;
                                SCLogUtility.Log($"Reusing pinned unsigned tx for withdrawal {withdrawalRequestHash} ({parsedPinnedCoins.Count} input(s)).", "VBTCService.CompleteWithdrawal()");
                                break;

                            case PinnedReuseDecision.AlreadyPaid:
                                SCLogUtility.Log($"Withdrawal {withdrawalRequestHash} already paid on-chain as {pinnedTxId} ({pinnedTxConfirmations} conf) — refusing to sign a second tx.", "VBTCService.CompleteWithdrawal()");
                                return (false, string.Empty, string.Empty,
                                    $"The previously signed Bitcoin tx {pinnedTxId} for this withdrawal is already confirmed on-chain — this withdrawal is paid. Complete/reconcile it instead of re-signing.", null);

                            case PinnedReuseDecision.RebuildFresh:
                                SCLogUtility.Log($"Pinned tx for withdrawal {withdrawalRequestHash} was conflicted away on-chain (all pinned outpoints spent, {pinnedTxId ?? "n/a"} known-unconfirmed) — clearing pin and rebuilding.", "VBTCService.CompleteWithdrawal()");
                                withdrawalRequest.PinnedUnsignedTxHex = null;
                                withdrawalRequest.PinnedCoinsJson = null;
                                if (!isTransientRequest)
                                    VBTCWithdrawalRequest.Save(withdrawalRequest, true);
                                break;
                        }
                    }
                }

                var concurrencyActive = WithdrawalConcurrencyActive(NextBlockHeight);
                HashSet<string>? excludedOutpoints = null;
                HashSet<string> liveOutpoints = new HashSet<string>();
                HashSet<string> reclaimOutpoints = new HashSet<string>();
                // A reclaim build is never made for a delegated/bridge-exit call (no row to carry the deferral).
                var allowReclaim = !isTransientRequest;
                // A reused pinned build keeps the reclaim intent it was built with.
                var isReclaim = pinnedUnsignedTxHex != null && withdrawalRequest.CompleteAfterConfirmation;

                if (pinnedUnsignedTxHex == null && concurrencyActive)
                {
                    // Concurrency: build only from coins no other withdrawal's signed tx holds (validators refuse
                    // the rest). Coins of a withheld tx (reclaimable) are held back too, and used only if the free
                    // coins cannot cover this withdrawal.
                    var (pinsAnswered, pins) = await FrostMPCService.FetchContractPins(scUID, validators);
                    var otherPins = pins.Where(p => !string.Equals(p.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase)).ToList();
                    liveOutpoints = otherPins.Where(p => !p.Reclaimable).SelectMany(p => p.Outpoints).ToHashSet();
                    reclaimOutpoints = otherPins.Where(p => p.Reclaimable).SelectMany(p => p.Outpoints).Where(o => !liveOutpoints.Contains(o)).ToHashSet();
                    excludedOutpoints = liveOutpoints.Concat(reclaimOutpoints).ToHashSet();
                    if (!pinsAnswered)
                        SCLogUtility.Log($"No validator served /frost/pins for {scUID}; building without exclusions (a refusal will name held coins).", "VBTCService.CompleteWithdrawal()");
                    else if (excludedOutpoints.Count > 0)
                        SCLogUtility.Log($"Excluding {excludedOutpoints.Count} coin(s) held by {otherPins.Count} other withdrawal(s) on {scUID} ({reclaimOutpoints.Count} reclaimable).", "VBTCService.CompleteWithdrawal()");
                }
                else if (pinnedUnsignedTxHex == null)
                {
                    // Conflict-preference: if an EARLIER request for this contract left a pinned,
                    // unresolved tx behind, prefer its outpoints so our new tx conflicts with any
                    // outstanding validator pin (the condition under which a different withdrawal
                    // is allowed to sign — and the guarantee that at most one tx ever confirms).
                    var priorPinned = VBTCWithdrawalRequest.GetLatestPinnedForContract(scUID);
                    if (priorPinned != null && priorPinned.TransactionHash != withdrawalRequestHash && !string.IsNullOrEmpty(priorPinned.PinnedCoinsJson))
                    {
                        var priorCoins = JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(priorPinned.PinnedCoinsJson);
                        if (priorCoins is { Count: > 0 })
                        {
                            preferredOutpoints = priorCoins.Select(c => c.ToOutpointKey()).ToHashSet();
                            SCLogUtility.Log($"Preferring {preferredOutpoints.Count} outpoint(s) pinned by prior request {priorPinned.TransactionHash} for contract {scUID}.", "VBTCService.CompleteWithdrawal()");
                        }
                    }
                }

                // Execute FROST withdrawal (build + sign; broadcast only if not signOnly)
                // Use the withdrawal requestor's address as the FROST coordinator/leader —
                // they already proved token ownership during the withdrawal request step.
                var coordinatorAddress = withdrawalRequest.RequestorAddress;
                SCLogUtility.Log($"Executing FROST withdrawal: {withdrawalAmount} BTC to {btcDestination} (signOnly={signOnly}, coordinator={coordinatorAddress})", "VBTCService.CompleteWithdrawal()");

                Task<(bool Success, string TxHash, string SignedTxHex, string ErrorMessage, FROST.Models.FrostCeremonyOutcome? Ceremony)> RunWithdrawal(
                    HashSet<string>? preferred, HashSet<string>? excluded, bool reclaimBuild) =>
                    BitcoinTransactionService.ExecuteFROSTWithdrawal(
                        depositAddress,
                        btcDestination,
                        withdrawalAmount,
                        feeRate,
                        scUID,
                        validators,
                        adjustedThreshold,
                        broadcast: !signOnly,
                        coordinatorAddress: coordinatorAddress,
                        withdrawalRequestHash: withdrawalRequestHash,  // FIND-028: Validator-side dedup
                        preSignedAuth: preSignedAuth,
                        pinnedUnsignedTxHex: pinnedUnsignedTxHex,
                        pinnedCoins: pinnedCoins,
                        persistPinnedBuild: isTransientRequest ? null : (hex, coins) => PersistPinnedBuild(withdrawalRequestHash, scUID, hex, coins, reclaimBuild),
                        preferredOutpoints: preferred,
                        excludedOutpoints: excluded,
                        onSigned: isTransientRequest ? null : (txid, hex) =>
                        {
                            // Before broadcast: a coordinator that dies after broadcasting still waits for the
                            // confirmation on restart, and one that dies before can rebroadcast.
                            if (reclaimBuild || isReclaim)
                                PersistDeferredCompletion(withdrawalRequestHash, scUID, txid, hex);
                        });

                var btcResult = await RunWithdrawal(preferredOutpoints, excludedOutpoints, reclaimBuild: false);

                // Free coins cannot cover it but a withheld tx's coins can: take those back. Validators sign this only
                // for pins they also find reclaimable, and COMPLETE waits for this tx to confirm (the withheld tx could
                // otherwise replace it after this withdrawal completed).
                if (!btcResult.Success && pinnedUnsignedTxHex == null && allowReclaim && reclaimOutpoints.Count > 0
                    && btcResult.ErrorMessage.Contains(BitcoinTransactionService.PinnedCoinsShortfallMarker))
                {
                    SCLogUtility.Log($"Free coins cannot cover withdrawal {withdrawalRequestHash}; reclaiming {reclaimOutpoints.Count} coin(s) of withheld transaction(s) on {scUID}.", "VBTCService.CompleteWithdrawal()");
                    isReclaim = true;
                    btcResult = await RunWithdrawal(reclaimOutpoints, liveOutpoints, reclaimBuild: true);
                    if (!btcResult.Success)
                        isReclaim = false;
                }

                if (!btcResult.Success)
                {
                    SCLogUtility.Log($"Bitcoin transaction failed: {btcResult.ErrorMessage}", "VBTCService.CompleteWithdrawal()");

                    // Local-only observability: record what/when/why the last signing attempt failed
                    // on the stored request. Never touches IsCompleted/Status=Completed semantics.
                    RecordSigningFailureOnRequest(withdrawalRequestHash, scUID, btcResult.Ceremony);

                    return (false, string.Empty, string.Empty, $"Bitcoin transaction failed: {btcResult.ErrorMessage}", btcResult.Ceremony);
                }

                string btcTxHash = btcResult.TxHash;
                string signedTxHex = btcResult.SignedTxHex;
                SCLogUtility.Log($"FROST signing successful. TxHash: {btcTxHash}, SignedTxHex length: {signedTxHex?.Length ?? 0}", "VBTCService.CompleteWithdrawal()");

                // Persist the signed BTC txid on the stored request (local-only). If the caller dies
                // between signing and completion, this is the only durable pointer to the outstanding
                // signed transaction — a future watcher can use it to detect an out-of-band broadcast.
                PersistSignedBtcTxId(withdrawalRequestHash, scUID, btcTxHash);

                // signOnly mode: Return the signed TX hex without broadcasting or creating VFX TX.
                // The caller (wallet node) will handle broadcast and VFX completion TX. A reclaim build is
                // recorded on the row (CompleteAfterBtcTxId): the raw COMPLETE builder waits for its confirmation.
                if (signOnly)
                {
                    SCLogUtility.Log($"signOnly mode: returning signed TX hex to caller. TxHash: {btcTxHash}" + (isReclaim ? " (reclaim: complete after it confirms)" : ""), "VBTCService.CompleteWithdrawal()");
                    return (true, string.Empty, signedTxHex, string.Empty, null);
                }

                if (isReclaim)
                {
                    SCLogUtility.Log($"Reclaim tx {btcTxHash} broadcast for withdrawal {withdrawalRequestHash}; COMPLETE deferred until it confirms.", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, AwaitingConfirmationMessage(btcTxHash, "broadcast"), null);
                }

                return await SubmitWithdrawalCompletion(scUID, withdrawalRequestHash, withdrawalRequest, withdrawalAmount, btcDestination, btcTxHash, vbtcContract);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Complete Error: {ex.Message}", "VBTCService.CompleteWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ApiErrorText.For(ex)}", null);
            }
        }

        /// <summary>
        /// Coins of the contract that other withdrawals' signed transactions hold, as the contract's validators report
        /// them: Live (never spend) and Reclaimable (withheld; spend only when the free coins cannot cover the
        /// withdrawal). The same split CompleteWithdrawal builds with, for callers that must predict its build
        /// (PrepareCompleteWithdrawalRaw). Empty sets before VbtcWithdrawalConcurrencyHeight.
        /// </summary>
        public static async Task<(HashSet<string> Live, HashSet<string> Reclaimable)> GetHeldCoins(string scUID, string withdrawalRequestHash)
        {
            var live = new HashSet<string>();
            var reclaimable = new HashSet<string>();
            if (!WithdrawalConcurrencyActive(NextBlockHeight))
                return (live, reclaimable);
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState == null || string.IsNullOrEmpty(scState.ContractData))
                    return (live, reclaimable);
                var feature = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData)?.Features?
                    .Where(x => x.FeatureName == FeatureName.TokenizationV2).Select(x => x.FeatureFeatures).FirstOrDefault() as TokenizationV2Feature;
                var snapshot = new HashSet<string>(feature?.ValidatorAddressesSnapshot ?? new List<string>());
                var validators = (VBTCValidatorRegistry.GetActiveValidators() ?? new List<VBTCValidator>())
                    .Where(v => snapshot.Count == 0 || snapshot.Contains(v.ValidatorAddress)).ToList();

                var (_, pins) = await FrostMPCService.FetchContractPins(scUID, validators);
                var others = pins.Where(p => !string.Equals(p.WithdrawalRequestHash, withdrawalRequestHash, StringComparison.OrdinalIgnoreCase)).ToList();
                live = others.Where(p => !p.Reclaimable).SelectMany(p => p.Outpoints).ToHashSet();
                reclaimable = others.Where(p => p.Reclaimable).SelectMany(p => p.Outpoints).Where(o => !live.Contains(o)).ToHashSet();
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Held-coin lookup failed for {scUID}: {ex.Message}", "VBTCService.GetHeldCoins()");
            }
            return (live, reclaimable);
        }

        /// <summary>Prefix of the CompleteWithdrawal result for a reclaim tx whose COMPLETE waits for its confirmation.</summary>
        public const string AwaitingConfirmationMarker = "[AWAITING-BTC-CONFIRMATION]";

        private static string AwaitingConfirmationMessage(string btcTxHash, string state) =>
            $"{AwaitingConfirmationMarker} Bitcoin transaction {btcTxHash} {state}. It takes back coins a withheld withdrawal was holding, so this withdrawal is completed on VFX only after it confirms (a node wallet does this automatically; otherwise call CompleteWithdrawal again).";

        public enum DeferredCompletionAction { Submit, Wait, Rebuild }

        /// <summary>
        /// Next step for a reclaim withdrawal whose COMPLETE waits for its own tx (CompleteAfterBtcTxId): submit once
        /// it confirms; wait while it is in a mempool (rebroadcasting the stored signed tx when no server knows it);
        /// rebuild when it can never confirm (the withheld tx won: an input is spent by a confirmed tx) — the fields
        /// are then cleared, and validators allow a new tx on the same evidence.
        /// </summary>
        private static async Task<(DeferredCompletionAction Action, string Message)> ResolveDeferredCompletion(VBTCWithdrawalRequest row, string depositAddress)
        {
            var txid = row.CompleteAfterBtcTxId!;

            if (HasPendingCompletion(row.TransactionHash, row.SmartContractUID))
                return (DeferredCompletionAction.Wait, $"The completion for withdrawal {row.TransactionHash} is already in the mempool.");

            var lookup = await BitcoinTransactionService.GetTransactionConfirmationsResilient(txid);
            if (lookup.Confirmations >= 1)
                return (DeferredCompletionAction.Submit, string.Empty);
            if (lookup.Confirmations == 0)
                return (DeferredCompletionAction.Wait, AwaitingConfirmationMessage(txid, "is in the mempool, waiting for its first confirmation"));

            // Unknown to every server: never broadcast (coordinator died after signing), evicted, or replaced.
            var coins = string.IsNullOrEmpty(row.PinnedCoinsJson)
                ? new List<PinnedWithdrawalCoin>()
                : JsonConvert.DeserializeObject<List<PinnedWithdrawalCoin>>(row.PinnedCoinsJson) ?? new List<PinnedWithdrawalCoin>();
            var (verdict, detail) = await BitcoinTransactionService.GetOutpointSpendVerdict(depositAddress, coins.Select(c => c.ToOutpointKey()));
            if (verdict == BitcoinTransactionService.OutpointSpendVerdict.SpentByConfirmed)
            {
                SCLogUtility.Log($"Reclaim tx {txid} for withdrawal {row.TransactionHash} can never confirm ({detail}); rebuilding.", "VBTCService.ResolveDeferredCompletion()");
                row.CompleteAfterConfirmation = false;
                row.CompleteAfterBtcTxId = null;
                row.DeferredSignedTxHex = null;
                row.PinnedUnsignedTxHex = null;
                row.PinnedCoinsJson = null;
                VBTCWithdrawalRequest.Save(row, true);
                return (DeferredCompletionAction.Rebuild, string.Empty);
            }

            if (!string.IsNullOrEmpty(row.DeferredSignedTxHex) && verdict == BitcoinTransactionService.OutpointSpendVerdict.NotSpentByConfirmed)
            {
                var rebroadcast = await BitcoinTransactionService.BroadcastTransaction(NBitcoin.Transaction.Parse(row.DeferredSignedTxHex, Globals.BTCNetwork));
                return (DeferredCompletionAction.Wait, AwaitingConfirmationMessage(txid, rebroadcast.Success ? "was not seen by any server and has been rebroadcast" : $"was not seen by any server; rebroadcast failed ({rebroadcast.ErrorMessage})"));
            }

            return (DeferredCompletionAction.Wait, AwaitingConfirmationMessage(txid, $"is not known to any server yet ({detail})"));
        }

        /// <summary>True when a WITHDRAWAL_COMPLETE for this request and contract is already in the local mempool.</summary>
        private static bool HasPendingCompletion(string withdrawalRequestHash, string scUID)
        {
            try
            {
                return TransactionData.GetPool().Find(x => x.TransactionType == TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE).ToList()
                    .Any(x =>
                    {
                        try
                        {
                            var j = JObject.Parse(x.Data);
                            return j["WithdrawalRequestHash"]?.ToObject<string>() == withdrawalRequestHash && j["ContractUID"]?.ToObject<string>() == scUID;
                        }
                        catch { return false; }
                    });
            }
            catch { return false; }
        }

        /// <summary>
        /// Signs and submits the VFX WITHDRAWAL_COMPLETE for a withdrawal whose Bitcoin tx is out (and, for a reclaim,
        /// confirmed). Signed by the requester's local account.
        /// </summary>
        private static async Task<(bool Success, string VFXTxHash, string BTCTxHash, string ErrorMessage, FROST.Models.FrostCeremonyOutcome? Ceremony)> SubmitWithdrawalCompletion(
            string scUID, string withdrawalRequestHash, VBTCWithdrawalRequest withdrawalRequest,
            decimal withdrawalAmount, string btcDestination, string btcTxHash, VBTCContractV2? vbtcContract)
        {
            try
            {
                var hasLocalContract = vbtcContract != null;

                // Use validator address or first available account for transaction creation
                string fromAddress = withdrawalRequest.RequestorAddress;

                var account = AccountData.GetSingleAccount(fromAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Account not found: {fromAddress}", null);
                }

                // Create transaction data (use resolved withdrawal details, not contract Active* fields which may be null)
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalComplete()",
                    ContractUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash,
                    BTCTransactionHash = btcTxHash,
                    Amount = withdrawalAmount,
                    Destination = btcDestination
                });

                // Build transaction — self-transaction by the validator recording the withdrawal completion
                var completionTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = fromAddress,
                    ToAddress = fromAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(fromAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_COMPLETE,
                    Data = txData
                };

                // BURN-EXIT FIX: Bridge/withdrawal-complete transactions MUST remain fee-free.
                // The previous call to FeeCalcService.CalculateTXFee(...) was overwriting Fee = 0.0M
                // with a non-zero value which broke consensus on fee-free TX types and caused
                // validator balance checks to reject casters that don't have a funded wallet.
                completionTx.Fee = 0M.ToNormalizeDecimal();

                // Build and sign transaction
                completionTx.Build();
                var txHash = completionTx.Hash;
                
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {fromAddress}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"Private key was null for account {fromAddress}", null);
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Signature Failed. SCUID: {scUID}", null);
                }

                completionTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(completionTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(completionTx, true);
                    await AccountData.UpdateLocalBalance(fromAddress, completionTx.Fee + completionTx.Amount);
                    await TransactionData.AddToPool(completionTx);
                    await P2PClient.SendTXMempool(completionTx);
                    
                    // Phase 5: Update activity tracking after successful withdrawal (only if local contract exists)
                    if (hasLocalContract && vbtcContract != null)
                    {
                        vbtcContract.LastValidatorActivityBlock = Globals.LastBlock.Height;
                        VBTCContractV2.UpdateContract(vbtcContract);
                        SCLogUtility.Log($"Updated LastValidatorActivityBlock to {Globals.LastBlock.Height}", "VBTCService.CompleteWithdrawal()");
                    }
                    
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Success. SCUID: {scUID}, TxHash: {completionTx.Hash}, BTCTxHash: {btcTxHash}", "VBTCService.CompleteWithdrawal()");
                    return (true, completionTx.Hash, btcTxHash, string.Empty, null);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Complete TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CompleteWithdrawal()");
                    return (false, string.Empty, btcTxHash, $"TX Verify Failed: {result.Item2}", null);
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Complete Error: {ex.Message}", "VBTCService.CompleteWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ApiErrorText.For(ex)}", null);
            }
        }

        /// <summary>
        /// Local-only observability write: stamps the stored withdrawal request with the last
        /// signing failure's code/session/time. Purely informational for UIs and support — never
        /// touches IsCompleted or completion Status values (those are consensus-adjacent).
        /// </summary>
        private static void RecordSigningFailureOnRequest(string withdrawalRequestHash, string scUID, FROST.Models.FrostCeremonyOutcome? ceremony)
        {
            try
            {
                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (storedRequest == null)
                    return;

                storedRequest.LastSigningFailureCode = ceremony?.FailureCode.ToString() ?? "Unknown";
                storedRequest.LastSigningFailureAt = TimeUtil.GetTime();
                storedRequest.LastSigningSessionId = ceremony?.SessionId ?? string.Empty;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to record signing failure on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.RecordSigningFailureOnRequest()");
            }
        }

        /// <summary>
        /// What to do with a withdrawal's previously-pinned (announced) unsigned tx.
        /// </summary>
        public enum PinnedReuseDecision
        {
            /// <summary>Re-sign the identical pinned tx (idempotent under the validators' sighash pin).</summary>
            ReusePinned,
            /// <summary>The pinned tx confirmed — this withdrawal is already paid; never sign another tx for it.</summary>
            AlreadyPaid,
            /// <summary>The pinned tx was definitively conflicted away — safe to clear the pin and build fresh.</summary>
            RebuildFresh
        }

        /// <summary>
        /// FIND-028 stale-pin escape, as a pure function (no I/O) so the rule is unit-testable.
        /// The critical case: pinned outpoints all gone + confirmation lookup UNKNOWN (null) must
        /// REUSE, not rebuild. The old code collapsed "Electrum couldn't answer" into confs==0 and
        /// rebuilt a fresh NON-conflicting tx — which validators holding the pin correctly 409
        /// (FIND-028), deadlocking the withdrawal until their pin cleared. Reuse is always safe:
        /// re-signing the identical tx is idempotent under the validators' sighash rules.
        /// </summary>
        public static PinnedReuseDecision DecidePinnedTxDisposition(bool utxoLookupSucceeded, bool anyPinnedOutpointUnspent, int? pinnedTxConfirmations)
        {
            // Inconclusive UTXO view, or the pinned tx can still confirm as-is → reuse it verbatim.
            if (!utxoLookupSucceeded || anyPinnedOutpointUnspent)
                return PinnedReuseDecision.ReusePinned;

            if (pinnedTxConfirmations >= 1)
                return PinnedReuseDecision.AlreadyPaid;

            // All pinned outpoints spent, and no server could say whether OUR tx is what spent them
            // → unknown; keep reusing until a definitive answer arrives.
            if (pinnedTxConfirmations == null)
                return PinnedReuseDecision.ReusePinned;

            // Definitive: outpoints gone AND the pinned tx is known-unconfirmed → conflicted away.
            return PinnedReuseDecision.RebuildFresh;
        }

        /// <summary>
        /// FIND-028: persist the exact unsigned tx (and its coins) about to be announced to the
        /// validators, so any retry re-signs the identical sighashes instead of rebuilding.
        /// Written BEFORE the first announce; local-only, never consensus-read.
        /// </summary>
        private static void PersistPinnedBuild(string withdrawalRequestHash, string scUID, string unsignedTxHex, List<PinnedWithdrawalCoin> coins, bool reclaimBuild = false)
        {
            try
            {
                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (storedRequest == null)
                    return;

                storedRequest.PinnedUnsignedTxHex = unsignedTxHex;
                storedRequest.PinnedCoinsJson = JsonConvert.SerializeObject(coins);
                // A retry reuses this exact tx, so it must keep waiting for confirmation too.
                storedRequest.CompleteAfterConfirmation = reclaimBuild;
                storedRequest.CompleteAfterBtcTxId = null;
                storedRequest.DeferredSignedTxHex = null;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to persist pinned build on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.PersistPinnedBuild()");
            }
        }

        /// <summary>
        /// Reclaim: record the signed tx this withdrawal's COMPLETE waits for (txid) and its signed hex (rebroadcast
        /// after a crash). Written before broadcast. Local-only, never consensus-read.
        /// </summary>
        private static void PersistDeferredCompletion(string withdrawalRequestHash, string scUID, string btcTxId, string signedTxHex)
        {
            try
            {
                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (storedRequest == null)
                    return;

                storedRequest.CompleteAfterConfirmation = true;
                storedRequest.CompleteAfterBtcTxId = btcTxId;
                storedRequest.DeferredSignedTxHex = signedTxHex;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to persist deferred completion on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.PersistDeferredCompletion()");
            }
        }

        /// <summary>
        /// Local-only: persist the signed BTC txid on the stored request so a signed-but-unbroadcast
        /// transaction remains traceable if the caller (e.g. a web wallet) dies before completing.
        /// </summary>
        private static void PersistSignedBtcTxId(string withdrawalRequestHash, string scUID, string btcTxHash)
        {
            try
            {
                if (string.IsNullOrEmpty(btcTxHash))
                    return;

                var storedRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (storedRequest == null)
                    return;

                storedRequest.LastSignedBtcTxId = btcTxHash;
                VBTCWithdrawalRequest.Save(storedRequest, true);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Failed to persist signed BTC txid on request {withdrawalRequestHash}: {ex.Message}", "VBTCService.PersistSignedBtcTxId()");
            }
        }

        /// <summary>
        /// Cancel an active withdrawal request for a vBTC V2 contract.
        /// Only the original requestor can cancel their own withdrawal.
        /// </summary>
        /// <param name="scUID">Smart contract UID</param>
        /// <param name="requestorAddress">Address that originally requested the withdrawal</param>
        /// <param name="withdrawalRequestHash">Hash of the withdrawal request transaction to cancel</param>
        /// <returns>Success flag and message</returns>
        public static async Task<(bool, string)> CancelWithdrawal(string scUID, string requestorAddress, string withdrawalRequestHash)
        {
            try
            {
                // Get account and validate
                var account = AccountData.GetSingleAccount(requestorAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Account not found: {requestorAddress}");
                }

                // Verify the withdrawal request exists and belongs to this user. Contract-scoped:
                // a multi-contract request has one row per contract under this hash, and a cancel
                // targets exactly one of them.
                var existingRequest = VBTCWithdrawalRequest.GetByTransactionHash(withdrawalRequestHash, scUID);
                if (existingRequest == null)
                {
                    SCLogUtility.Log($"Withdrawal request not found: {withdrawalRequestHash}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Withdrawal request not found: {withdrawalRequestHash}");
                }

                if (existingRequest.RequestorAddress != requestorAddress)
                {
                    SCLogUtility.Log($"Requestor address mismatch. Expected: {existingRequest.RequestorAddress}, Got: {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, "Only the original withdrawal requestor can cancel this withdrawal.");
                }

                if (existingRequest.IsCompleted)
                {
                    SCLogUtility.Log($"Withdrawal already completed/cancelled: {withdrawalRequestHash}", "VBTCService.CancelWithdrawal()");
                    return (false, "Cannot cancel an already completed or cancelled withdrawal.");
                }

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCWithdrawalCancel()",
                    ContractUID = scUID,
                    WithdrawalRequestHash = withdrawalRequestHash
                });

                // Build transaction — self-transaction from the requestor
                var cancelTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = requestorAddress,
                    ToAddress = requestorAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(requestorAddress),
                    TransactionType = TransactionType.VBTC_V2_WITHDRAWAL_CANCEL,
                    Data = txData
                };

                cancelTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(cancelTx);

                // Build and sign transaction
                cancelTx.Build();
                var txHash = cancelTx.Hash;

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {requestorAddress}", "VBTCService.CancelWithdrawal()");
                    return (false, $"Private key was null for account {requestorAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CancelWithdrawal()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                cancelTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(cancelTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(cancelTx, true);
                    await AccountData.UpdateLocalBalance(requestorAddress, cancelTx.Fee + cancelTx.Amount);
                    await TransactionData.AddToPool(cancelTx);
                    await P2PClient.SendTXMempool(cancelTx);
                    SCLogUtility.Log($"vBTC V2 Withdrawal Cancel TX Success. SCUID: {scUID}, TxHash: {cancelTx.Hash}", "VBTCService.CancelWithdrawal()");
                    return (true, cancelTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Withdrawal Cancel TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CancelWithdrawal()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Withdrawal Cancel Error: {ex.Message}", "VBTCService.CancelWithdrawal()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        #region Bridge Lock / Unlock Transactions

        /// <summary>
        /// Create and broadcast a VBTC_V2_BRIDGE_LOCK transaction on the VFX chain.
        /// This deducts the vBTC balance in the State Trei so all nodes see the lock.
        /// </summary>
        /// <param name="scUID">Smart contract UID of the vBTC contract</param>
        /// <param name="ownerAddress">VFX address that owns the vBTC being locked</param>
        /// <param name="amount">Amount of vBTC to lock (in BTC, e.g. 0.001)</param>
        /// <param name="evmDestination">EVM address on Base to receive vBTC.b</param>
        /// <param name="lockIdOverride">Optional; default is a new GUID (format N). Must be unique on-chain.</param>
        /// <returns>(Success, TxHashOrError, LockId)</returns>
        /// <summary>
        /// S3C §6.3: resolve a contract's IsS3C flag — local record first, else state-trei
        /// decompile (works on any node; used by the consensus bridge-lock rejection too).
        /// </summary>
        public static bool ResolveContractIsS3C(string scUID)
        {
            var local = VBTCContractV2.GetContract(scUID);
            if (local != null) return local.IsS3C;
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState != null && !string.IsNullOrEmpty(scState.ContractData))
                {
                    var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                    var tknz = scMain?.Features?
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault() as TokenizationV2Feature;
                    if (tknz != null) return tknz.IsS3C;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// S3C §7.4/§8: resolve a contract's DKG validator snapshot — local record first, else
        /// state-trei decompile (works on any node). Empty list if none (legacy no-snapshot).
        /// </summary>
        public static List<string> ResolveContractSnapshot(string scUID)
        {
            var local = VBTCContractV2.GetContract(scUID);
            if (local != null && local.ValidatorAddressesSnapshot != null && local.ValidatorAddressesSnapshot.Count > 0)
                return local.ValidatorAddressesSnapshot;
            try
            {
                var scState = SmartContractStateTrei.GetSmartContractState(scUID);
                if (scState != null && !string.IsNullOrEmpty(scState.ContractData))
                {
                    var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);
                    var tknz = scMain?.Features?
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault() as TokenizationV2Feature;
                    if (tknz?.ValidatorAddressesSnapshot != null) return tknz.ValidatorAddressesSnapshot;
                }
            }
            catch { }
            return new List<string>();
        }

        /// <summary>
        /// S3C §7.4: the eligible cancellation-voter set for a contract. Snapshot validators if the
        /// contract has a DKG snapshot; else the public validators (legacy fallback). The 75%
        /// denominator is this set's Count (FULL snapshot — dead members still count). Used
        /// identically by TransactionValidatorService (eligibility) and StateData (denominator).
        /// </summary>
        public static HashSet<string> ResolveCancellationVoterSet(string scUID)
        {
            var snapshot = ResolveContractSnapshot(scUID);
            if (snapshot.Count > 0)
                return new HashSet<string>(snapshot);
            return new HashSet<string>(VBTCValidatorRegistry.GetPublicValidators().Select(v => v.ValidatorAddress));
        }

        public static async Task<(bool Success, string TxHashOrError, string LockId)> CreateBridgeLockTx(
            string scUID, string ownerAddress, decimal amount, string evmDestination, string? lockIdOverride = null)
        {
            try
            {
                // Reserve-held vBTC is locked: consensus denies xRBX bridge locks.
                if (ownerAddress.StartsWith("xRBX"))
                    return (false, "Reserve accounts cannot bridge vBTC. Move the vBTC to a normal VFX address first.", string.Empty);

                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {ownerAddress}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Account not found: {ownerAddress}", string.Empty);
                }

                // S3C §6.3: vBTC.b bridge is not available for S3C contracts (client-side gate;
                // also consensus-enforced at TX validation). Companion path (§5) is the route.
                if (ResolveContractIsS3C(scUID))
                    return (false, "vBTC.b bridge is not available for S3C contracts. Withdraw to your own public companion contract to bridge.", string.Empty);

                // Validate balance (subtract local bridge reservations not yet reflected in state trei)
                var balResult = await TryGetAvailableTransparentVbtcBalance(scUID, ownerAddress);
                if (!balResult.success)
                {
                    SCLogUtility.Log(balResult.error ?? "Balance lookup failed", "VBTCService.CreateBridgeLockTx()");
                    return (false, balResult.error ?? "Could not resolve vBTC transparent balance.", string.Empty);
                }

                var reservedLocal = BridgeLockRecord.GetLockedAmount(ownerAddress, scUID);
                var spendable = balResult.availableBalance - reservedLocal;
                if (spendable < amount)
                {
                    SCLogUtility.Log($"Insufficient balance. Available: {spendable} (raw: {balResult.availableBalance}, local bridge reserve: {reservedLocal}), Requested: {amount}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Insufficient balance. Available: {spendable} BTC, Requested: {amount}", string.Empty);
                }

                // Auto-derive Base address from VFX key if evmDestination not provided
                if (string.IsNullOrWhiteSpace(evmDestination))
                    evmDestination = ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);

                // Validate EVM destination
                if (string.IsNullOrWhiteSpace(evmDestination) || !evmDestination.StartsWith("0x") || evmDestination.Length != 42)
                {
                    return (false, "Invalid EVM destination address. Must be 0x-prefixed, 42 characters.", string.Empty);
                }

                var lockId = string.IsNullOrWhiteSpace(lockIdOverride) ? Guid.NewGuid().ToString("N") : lockIdOverride.Trim();
                long amountSats = (long)(amount * 100_000_000M);

                // Create transaction data
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeLock()",
                    ContractUID = scUID,
                    LockId = lockId,
                    Amount = amount,
                    AmountSats = amountSats,
                    EvmDestination = evmDestination
                });

                // Build transaction (self-TX: from=to=owner, amount=0 VFX)
                var lockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_LOCK,
                    Data = txData
                };

                lockTx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(lockTx);

                // Build and sign
                lockTx.Build();
                var txHash = lockTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {ownerAddress}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"Private key was null for account {ownerAddress}", string.Empty);
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}", string.Empty);
                }

                lockTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(lockTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(lockTx, true);
                    await AccountData.UpdateLocalBalance(ownerAddress, lockTx.Fee + lockTx.Amount);
                    await TransactionData.AddToPool(lockTx);
                    await P2PClient.SendTXMempool(lockTx);
                    SCLogUtility.Log($"vBTC V2 Bridge Lock TX Success. SCUID: {scUID}, TxHash: {lockTx.Hash}, LockId: {lockId}", "VBTCService.CreateBridgeLockTx()");
                    return (true, lockTx.Hash, lockId);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Bridge Lock TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CreateBridgeLockTx()");
                    return (false, $"TX Verify Failed: {result.Item2}", string.Empty);
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Bridge Lock Error: {ex.Message}", "VBTCService.CreateBridgeLockTx()");
                return (false, $"Error: {ApiErrorText.For(ex)}", string.Empty);
            }
        }

        /// <summary>
        /// Polls until <see cref="VBTCBridgeLockState"/> exists (lock included in a block) or the timeout elapses.
        /// Used before relay mint on Base so the VFX lock is consensus-valid.
        /// </summary>
        public static async Task<bool> WaitForBridgeLockInStateAsync(string lockId, int timeoutMs = 120_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (VBTCBridgeLockState.GetByLockId(lockId) != null)
                    return true;
                var rec = BridgeLockRecord.GetByLockId(lockId);
                if (rec?.VfxLockConfirmedOnChain == true)
                    return true;
                await Task.Delay(400);
            }
            return VBTCBridgeLockState.GetByLockId(lockId) != null;
        }

        /// <summary>
        /// Create and broadcast a VBTC_V2_BRIDGE_UNLOCK transaction on the VFX chain.
        /// This restores the vBTC balance after a burn on Base has been detected.
        /// Called by the user's node when it detects its own ExitBurned event on Base.
        /// </summary>
        /// <param name="scUID">Smart contract UID of the vBTC contract</param>
        /// <param name="ownerAddress">VFX address that originally locked the vBTC</param>
        /// <param name="lockId">The lock ID being unlocked</param>
        /// <param name="amount">Amount being unlocked</param>
        /// <param name="exitBurnTxHash">The Base transaction hash of the burnForExit call</param>
        /// <returns>(Success, TxHashOrError)</returns>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeUnlockTx(
            string scUID, string ownerAddress, string lockId, decimal amount, string exitBurnTxHash,
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null)
        {
            try
            {
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found: {ownerAddress}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"Account not found: {ownerAddress}");
                }

                long amountSats = (long)(amount * 100_000_000M);

                // Create transaction data
                object payload = new
                {
                    Function = "VBTCBridgeUnlock()",
                    ContractUID = scUID,
                    LockId = lockId,
                    Amount = amount,
                    AmountSats = amountSats,
                    ExitBurnTxHash = exitBurnTxHash,
                    CasterConsensusVotes = casterConsensusVotes
                };
                var txData = JsonConvert.SerializeObject(payload);

                // Build transaction (self-TX: from=to=owner, amount=0 VFX)
                var unlockTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
                    Data = txData
                };

                unlockTx.Fee = 0.00M;

                // Build and sign
                unlockTx.Build();
                var txHash = unlockTx.Hash;
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;

                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {ownerAddress}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"Private key was null for account {ownerAddress}");
                }

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(txHash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX Signature Failed. SCUID: {scUID}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"TX Signature Failed. SCUID: {scUID}");
                }

                unlockTx.Signature = signature;

                // Verify transaction
                var result = await TransactionValidatorService.VerifyTX(unlockTx);
                if (result.Item1)
                {
                    await TransactionData.AddTxToWallet(unlockTx, true);
                    await AccountData.UpdateLocalBalance(ownerAddress, unlockTx.Fee + unlockTx.Amount);
                    await TransactionData.AddToPool(unlockTx);
                    await P2PClient.SendTXMempool(unlockTx);
                    SCLogUtility.Log($"vBTC V2 Bridge Unlock TX Success. SCUID: {scUID}, TxHash: {unlockTx.Hash}, LockId: {lockId}", "VBTCService.CreateBridgeUnlockTx()");
                    return (true, unlockTx.Hash);
                }
                else
                {
                    SCLogUtility.Log($"vBTC V2 Bridge Unlock TX Verify Failed: {scUID}. Result: {result.Item2}", "VBTCService.CreateBridgeUnlockTx()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"vBTC V2 Bridge Unlock Error: {ex.Message}", "VBTCService.CreateBridgeUnlockTx()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC"/> after a Base <c>burnForBTCExit</c> is agreed by casters.
        /// V3: Now includes FIFO allocation plan and per-contract BTC withdrawal records so
        /// <see cref="StateData.ApplyVBTCBridgeExitToBTC"/> can apply partial unlocks to each lock.
        /// </summary>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCTx(
            string ownerAddress,
            decimal totalAmount,
            string btcDestination,
            string baseBurnTxHash,
            List<PoolUnlockAllocation> allocations,
            List<BtcExitWithdrawalRecord>? btcWithdrawals = null,
            IReadOnlyList<CasterConsensusVote>? casterConsensusVotes = null)
        {
            try
            {
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                    return (false, $"Account not found: {ownerAddress}");

                var totalAmountSats = (long)(totalAmount * 100_000_000M);
                var firstAlloc = allocations?.FirstOrDefault();
                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTC()",
                    // Backward-compatible fields required by TransactionValidatorService
                    ContractUID = firstAlloc?.SmartContractUID ?? "",
                    LockId = firstAlloc?.LockId ?? "",
                    Amount = totalAmount,
                    AmountSats = totalAmountSats,
                    // V3 fields
                    TotalAmount = totalAmount,
                    TotalAmountSats = totalAmountSats,
                    BtcDestination = btcDestination,
                    BaseBurnTxHash = baseBurnTxHash,
                    Allocations = allocations,
                    BtcWithdrawals = btcWithdrawals,
                    CasterConsensusVotes = casterConsensusVotes
                });

                var tx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = ownerAddress,
                    ToAddress = ownerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(ownerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC,
                    Data = txData
                };
                tx.Fee = 0.00M;
                tx.Build();
                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                    return (false, "Private key was null");
                tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, privateKey, publicKey);
                if (tx.Signature == "ERROR")
                    return (false, "TX Signature Failed");

                var result = await TransactionValidatorService.VerifyTX(tx);
                if (!result.Item1)
                    return (false, $"TX Verify Failed: {result.Item2}");

                await TransactionData.AddTxToWallet(tx, true);
                await AccountData.UpdateLocalBalance(ownerAddress, tx.Fee + tx.Amount);
                await TransactionData.AddToPool(tx);
                await P2PClient.SendTXMempool(tx);
                return (true, tx.Hash);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE"/> after the caster has
        /// FROST-signed and broadcast the Bitcoin withdrawal for a <c>burnForBTCExit</c>.
        /// This is the consensus-critical second half of the BTC-exit flow — it is what causes
        /// <see cref="StateData.ApplyVBTCBridgeExitToBTCComplete"/> to mark the pending
        /// <see cref="VBTCBridgeBtcExitState"/> row as complete.
        /// Runs entirely on the caster — signer is the caster's validator address, Fee is 0, self-TX.
        /// </summary>
        /// <param name="signerAddress">Caster / validator VFX address (must have a local account).</param>
        /// <param name="baseBurnTxHash">The Base <c>burnForBTCExit</c> transaction hash (32-byte hex).</param>
        /// <param name="btcTxHash">The broadcasted Bitcoin withdrawal transaction hash.</param>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCCompleteTx(
            string signerAddress,
            string baseBurnTxHash,
            string btcTxHash,
            List<BtcExitWithdrawalRecord>? btcWithdrawals = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signerAddress))
                    return (false, "Signer address is required for bridge exit to BTC complete.");
                if (string.IsNullOrWhiteSpace(baseBurnTxHash))
                    return (false, "BaseBurnTxHash is required for bridge exit to BTC complete.");
                if (string.IsNullOrWhiteSpace(btcTxHash))
                    return (false, "BtcTxHash is required for bridge exit to BTC complete.");

                var account = AccountData.GetSingleAccount(signerAddress);
                if (account == null)
                {
                    SCLogUtility.Log($"Account not found for signer: {signerAddress}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"Account not found: {signerAddress}");
                }

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                {
                    SCLogUtility.Log($"Private key was null for account {signerAddress}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"Private key was null for account {signerAddress}");
                }

                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTCComplete()",
                    BaseBurnTxHash = baseBurnTxHash.Trim(),
                    BtcTxHash = btcTxHash.Trim(),
                    BtcWithdrawals = btcWithdrawals
                });

                // Self-TX signed by the caster. Fee = 0 (enforced by validator). Amount = 0.
                var completionTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
                    Data = txData
                };

                // Do NOT call FeeCalcService.CalculateTXFee here — bridge TXs must remain fee-free.
                completionTx.Fee = 0.00M;
                completionTx.Build();

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(completionTx.Hash, privateKey, publicKey);
                if (signature == "ERROR")
                {
                    SCLogUtility.Log($"TX signature failed for bridge exit-to-BTC complete. BurnHash: {baseBurnTxHash}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, "TX Signature Failed");
                }

                completionTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(completionTx);
                if (!result.Item1)
                {
                    SCLogUtility.Log($"Bridge exit-to-BTC complete TX verify failed: {result.Item2}. BurnHash: {baseBurnTxHash}, BtcTxHash: {btcTxHash}",
                        "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                    return (false, $"TX Verify Failed: {result.Item2}");
                }

                await TransactionData.AddTxToWallet(completionTx, true);
                await AccountData.UpdateLocalBalance(signerAddress, completionTx.Fee + completionTx.Amount);
                await TransactionData.AddToPool(completionTx);
                await P2PClient.SendTXMempool(completionTx);

                SCLogUtility.Log(
                    $"vBTC V2 Bridge Exit-to-BTC Complete TX broadcast. TxHash: {completionTx.Hash}, BurnHash: {baseBurnTxHash}, BtcTxHash: {btcTxHash}",
                    "VBTCService.CreateBridgeExitToBTCCompleteTx()");

                return (true, completionTx.Hash);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Bridge exit-to-BTC complete error: {ex.Message}", "VBTCService.CreateBridgeExitToBTCCompleteTx()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Broadcast <see cref="TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL"/> when FROST signing
        /// fails for one or more lock allocations during a BTC exit. This transaction:
        /// (1) Reverses the partial unlocks for the failed locks (restores their RemainingAmount).
        /// (2) Blacklists those locks so they are never selected for future FIFO allocations.
        /// (3) Allows the burn to be retried with different locks.
        /// </summary>
        /// <param name="signerAddress">Caster / validator VFX address.</param>
        /// <param name="baseBurnTxHash">The original Base burn transaction hash.</param>
        /// <param name="exitTxHash">The EXIT_TO_BTC transaction hash that reserved the locks.</param>
        /// <param name="failedAllocations">The allocations whose contracts failed FROST signing.</param>
        /// <param name="reason">Human-readable reason for the failure.</param>
        public static async Task<(bool Success, string TxHashOrError)> CreateBridgeExitToBTCFailTx(
            string signerAddress,
            string baseBurnTxHash,
            string exitTxHash,
            List<PoolUnlockAllocation> failedAllocations,
            string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signerAddress))
                    return (false, "Signer address is required for bridge exit fail TX.");
                if (string.IsNullOrWhiteSpace(baseBurnTxHash))
                    return (false, "BaseBurnTxHash is required for bridge exit fail TX.");
                if (failedAllocations == null || failedAllocations.Count == 0)
                    return (false, "FailedAllocations must contain at least one allocation.");

                var account = AccountData.GetSingleAccount(signerAddress);
                if (account == null)
                    return (false, $"Account not found: {signerAddress}");

                var privateKey = account.GetPrivKey;
                var publicKey = account.PublicKey;
                if (privateKey == null)
                    return (false, $"Private key was null for account {signerAddress}");

                var txData = JsonConvert.SerializeObject(new
                {
                    Function = "VBTCBridgeExitToBTCFail()",
                    BaseBurnTxHash = baseBurnTxHash.Trim(),
                    ExitTxHash = exitTxHash?.Trim() ?? "",
                    FailedAllocations = failedAllocations,
                    Reason = reason
                });

                var failTx = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(),
                    FromAddress = signerAddress,
                    ToAddress = signerAddress,
                    Amount = 0.0M,
                    Fee = 0.0M,
                    Nonce = AccountStateTrei.GetNextNonce(signerAddress),
                    TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL,
                    Data = txData
                };

                failTx.Fee = 0.00M;
                failTx.Build();

                var signature = VerifiedXCore.Services.SignatureService.CreateSignature(failTx.Hash, privateKey, publicKey);
                if (signature == "ERROR")
                    return (false, "TX Signature Failed");

                failTx.Signature = signature;

                var result = await TransactionValidatorService.VerifyTX(failTx);
                if (!result.Item1)
                    return (false, $"TX Verify Failed: {result.Item2}");

                await TransactionData.AddTxToWallet(failTx, true);
                await AccountData.UpdateLocalBalance(signerAddress, failTx.Fee + failTx.Amount);
                await TransactionData.AddToPool(failTx);
                await P2PClient.SendTXMempool(failTx);

                SCLogUtility.Log(
                    $"vBTC V2 Bridge Exit-to-BTC FAIL TX broadcast. TxHash: {failTx.Hash}, BurnHash: {baseBurnTxHash}, " +
                    $"FailedLocks: {failedAllocations.Count}, Reason: {reason}",
                    "VBTCService.CreateBridgeExitToBTCFailTx()");

                return (true, failTx.Hash);
            }
            catch (Exception ex)
            {
                SCLogUtility.Log($"Bridge exit-to-BTC fail error: {ex.Message}", "VBTCService.CreateBridgeExitToBTCFailTx()");
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        #endregion

        #region Backfill Local V2 Contract Records

        /// <summary>
        /// Startup/on-demand backfill: ensures local SmartContract + VBTCContractV2 records exist for
        /// every vBTC V2 contract in the state trei that a local account owns or holds a ledger balance
        /// on. Covers transfers received before receive-time record creation existed. Idempotent
        /// (all saves are insert-only), so it is safe to run every boot or via ResyncVBTCContracts.
        /// </summary>
        public static async Task<(int Scanned, int LocalInvolvement, int Created, int SkippedExisting)> BackfillLocalVBTCContracts()
        {
            int scanned = 0, localInvolvement = 0, created = 0, skippedExisting = 0;
            try
            {
                var accountDb = AccountData.GetAccounts();
                if (accountDb == null)
                    return (scanned, localInvolvement, created, skippedExisting);

                var localAddresses = accountDb.FindAll().Select(x => x.Address).ToHashSet();

                // Reserve (xRBX) accounts can own and hold vBTC V2 — a wallet holding vBTC
                // only on reserve addresses previously backfilled nothing after a restore.
                var reserveAccountsBackfill = ReserveAccount.GetReserveAccounts();
                if (reserveAccountsBackfill != null)
                {
                    foreach (var r in reserveAccountsBackfill)
                        localAddresses.Add(r.Address);
                }

                if (!localAddresses.Any())
                {
                    SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: skipped — no local accounts.", "VBTCService.BackfillLocalVBTCContracts()");
                    return (scanned, localInvolvement, created, skippedExisting);
                }

                var scStateTrei = SmartContractStateTrei.GetSCST();
                if (scStateTrei == null)
                {
                    SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: skipped — state trei DB was null.", "VBTCService.BackfillLocalVBTCContracts()");
                    return (scanned, localInvolvement, created, skippedExisting);
                }

                SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: starting — local accounts: {localAddresses.Count}", "VBTCService.BackfillLocalVBTCContracts()");

                foreach (var scState in scStateTrei.Query().ToEnumerable())
                {
                    try
                    {
                        scanned++;
                        if (string.IsNullOrWhiteSpace(scState.SmartContractUID) || string.IsNullOrWhiteSpace(scState.ContractData))
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (empty-contract-data) — SCUID: '{scState.SmartContractUID}'", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        var isLocalOwner = localAddresses.Contains(scState.OwnerAddress);
                        var hasLocalLedger = scState.SCStateTreiTokenizationTXes != null &&
                            scState.SCStateTreiTokenizationTXes.Any(t => localAddresses.Contains(t.ToAddress) || localAddresses.Contains(t.FromAddress));

                        if (!isLocalOwner && !hasLocalLedger)
                            continue;

                        localInvolvement++;

                        if (VBTCContractV2.GetContract(scState.SmartContractUID) != null)
                        {
                            skippedExisting++;
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (already-exists) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue; // record already present; logo handled by the startup logo sweep
                        }

                        // Prefer the locally-saved SC record; if it is missing OR lacks the V2 feature
                        // (stale/partial save), decompile the authoritative state-trei contract data.
                        var scMain = SmartContractMain.SmartContractData.GetSmartContract(scState.SmartContractUID);
                        if (scMain?.Features?.Exists(x => x.FeatureName == FeatureName.TokenizationV2) != true)
                            scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);

                        if (scMain == null)
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (decompile-failed) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        if (scMain.Features?.Exists(x => x.FeatureName == FeatureName.TokenizationV2) != true)
                        {
                            SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: SKIP (no-V2-feature) — SCUID: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                            continue;
                        }

                        SmartContractMain.SmartContractData.SaveSmartContract(scMain, null);

                        if (isLocalOwner)
                        {
                            await VBTCContractV2.SaveSmartContract(scMain, null, scState.OwnerAddress);
                        }
                        else
                        {
                            // FIND-001: the holder param is deliberately ignored — this creates
                            // the single canonical record (owner = minter); balances live in the
                            // state trei, so which local address triggered the backfill is moot.
                            await VBTCContractV2.SaveSmartContractTransfer(scMain, localAddresses.First());
                        }

                        if (Globals.VBTCDefaultAssetOnly)
                            await NFTAssetFileUtility.AssociateDefaultVBTCLogo(scState.SmartContractUID);

                        created++;
                        SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: backfilled local vBTC V2 contract record: {scState.SmartContractUID}", "VBTCService.BackfillLocalVBTCContracts()");
                    }
                    catch (Exception scEx)
                    {
                        ErrorLogUtility.LogError($"VBTC-TRACE [6-Backfill]: backfill failed for SCUID: {scState.SmartContractUID}. Error: {scEx.Message}", "VBTCService.BackfillLocalVBTCContracts()");
                    }
                }

                SCLogUtility.Log($"VBTC-TRACE [6-Backfill]: complete — state records scanned: {scanned}, with local involvement: {localInvolvement}, records created: {created}, skipped existing: {skippedExisting}", "VBTCService.BackfillLocalVBTCContracts()");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError(ex.ToString(), "VBTCService.BackfillLocalVBTCContracts()");
            }

            return (scanned, localInvolvement, created, skippedExisting);
        }

        #endregion
    }
}
