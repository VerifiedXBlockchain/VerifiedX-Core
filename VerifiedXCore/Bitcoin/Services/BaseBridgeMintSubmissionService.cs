using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Utilities;
using System.Numerics;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Background service that monitors BridgeLockRecords with status AttestationReady
    /// and submits mintWithProof transactions to the Base contract.
    /// Runs on caster nodes that have a funded Base (ETH) account.
    /// </summary>
    public static class BaseBridgeMintSubmissionService
    {
        private static bool _running;
        private static readonly object _lock = new();

        /// <summary>ABI for mintWithProof on VBTCb.</summary>
        private const string MINT_WITH_PROOF_ABI = @"[
            {
                ""inputs"": [
                    { ""internalType"": ""address"", ""name"": ""to"", ""type"": ""address"" },
                    { ""internalType"": ""uint256"", ""name"": ""amount"", ""type"": ""uint256"" },
                    { ""internalType"": ""string"", ""name"": ""lockId"", ""type"": ""string"" },
                    { ""internalType"": ""uint256"", ""name"": ""nonce"", ""type"": ""uint256"" },
                    { ""internalType"": ""bytes[]"", ""name"": ""signatures"", ""type"": ""bytes[]"" }
                ],
                ""name"": ""mintWithProof"",
                ""outputs"": [],
                ""stateMutability"": ""nonpayable"",
                ""type"": ""function""
            }
        ]";

        /// <summary>
        /// Starts the background loop that checks for AttestationReady records and submits mintWithProof.
        /// Should be called once at startup on caster/validator nodes.
        /// </summary>
        public static void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
            }

            _ = Task.Run(async () =>
            {
                LogUtility.Log("[MintSubmission] Service started.", "BaseBridgeMintSubmissionService.Start");

                while (_running)
                {
                    try
                    {
                        await ProcessAttestationReadyRecords();
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[MintSubmission] Loop error: {ex.Message}", "BaseBridgeMintSubmissionService.Start");
                    }

                    await Task.Delay(30_000); // Check every 30 seconds
                }
            });
        }

        public static void Stop()
        {
            lock (_lock)
            {
                _running = false;
            }
        }

        /// <summary>
        /// Processes all BridgeLockRecords with AttestationReady status.
        /// For each, submits mintWithProof to the Base contract using the validator's derived ETH key.
        /// </summary>
        private static async Task ProcessAttestationReadyRecords()
        {
            if (!BaseBridgeService.IsBridgeConfigured)
                return;

            // Only validators/casters with an ETH key can submit
            var ethKey = GetValidatorEthKey();
            if (ethKey == null)
                return;

            var validatorAddress = Globals.ValidatorBaseAddress;
            if (string.IsNullOrEmpty(validatorAddress))
            {
                LogUtility.Log("[MintSubmission] Validator Base address not derived. Cannot submit.", "BaseBridgeMintSubmissionService.ProcessAttestationReadyRecords");
                return;
            }

            var (balanceSuccess, balanceEth, _) = await BaseBridgeService.GetEthBalanceAsync(validatorAddress);
            if (!balanceSuccess || balanceEth <= 0)
            {
                LogUtility.Log($"[MintSubmission] Validator Base address {validatorAddress} has insufficient ETH ({balanceEth} ETH). Fund this address on Base to enable mint submissions.",
                    "BaseBridgeMintSubmissionService.ProcessAttestationReadyRecords");
                return;
            }

            await ResolveUnfollowedSentMintsAsync(TimeUtil.GetTime());

            var records = BridgeLockRecord.GetByStatus(BridgeLockStatus.AttestationReady);
            if (records == null || records.Count == 0)
                return;

            foreach (var record in records)
            {
                try
                {
                    await SubmitMintWithProof(record, ethKey);
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[MintSubmission] Error submitting mintWithProof for lock {record.LockId}: {ex.Message}",
                        "BaseBridgeMintSubmissionService.ProcessAttestationReadyRecords");
                    BridgeLockRecord.UpdateStatus(record.LockId, BridgeLockStatus.AttestationReady,
                        errorMessage: $"Mint submission failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// What a lock becomes once its sent mint's receipt is known (true: in a block and succeeded; false: reverted;
        /// null: in no block). Minted when Base holds the mint in a block, or when the contract says it has minted the
        /// lock (another submission was first). Otherwise back to ready, so that it is submitted again: the contract is
        /// asked first then, and mints a lock once.
        /// </summary>
        internal static async Task ResolveSentMintAsync(string lockId, string txHash, bool? receipt)
        {
            if (receipt == true || await UserBridgeMintService.LockMintedOnBaseAsync(lockId) == true)
            {
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted, baseTxHash: txHash, errorMessage: "");
                LogUtility.Log($"[MintSubmission] mintWithProof CONFIRMED for lock {lockId}: {txHash}", "BaseBridgeMintSubmissionService.SubmitMintWithProof");
                return;
            }
            var why = receipt == false ? $"mintWithProof TX reverted: {txHash}" : $"mintWithProof TX {txHash} was in no Base block within the wait";
            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.AttestationReady, errorMessage: why);
            LogUtility.Log($"[MintSubmission] {why} (lock {lockId}); it is submitted again.", "BaseBridgeMintSubmissionService.SubmitMintWithProof");
        }

        /// <summary>Seconds after which a sent mint nobody is following any more (the node restarted) is looked at again.</summary>
        internal const long SentMintFollowedSeconds = 10 * 60;

        /// <summary>
        /// Locks left as sent with a transaction hash for longer than <see cref="SentMintFollowedSeconds"/>: the task
        /// that followed them into a block is gone. Each is resolved from its receipt as it is now. Returns how many.
        /// </summary>
        internal static async Task<int> ResolveUnfollowedSentMintsAsync(long now)
        {
            var resolved = 0;
            foreach (var record in BridgeLockRecord.GetByStatus(BridgeLockStatus.ProofSubmitted))
            {
                if (string.IsNullOrEmpty(record.BaseTxHash) || now - (record.RelayedAtUtc ?? 0) < SentMintFollowedSeconds
                    || UserBridgeMintService.MintIsUnderWay(record.LockId))
                    continue;
                await ResolveSentMintAsync(record.LockId, record.BaseTxHash, await UserBridgeMintService.MintReceiptAsync(record.BaseTxHash));
                resolved++;
            }
            return resolved;
        }

        /// <summary>
        /// Submits a single mintWithProof transaction to the Base contract.
        /// </summary>
        private static async Task SubmitMintWithProof(BridgeLockRecord record, EthECKey ethKey)
        {
            if (record.ValidatorSignatures == null || record.ValidatorSignatures.Count == 0)
            {
                LogUtility.Log($"[MintSubmission] No signatures for lock {record.LockId}. Skipping.",
                    "BaseBridgeMintSubmissionService.SubmitMintWithProof");
                return;
            }

            if (record.ValidatorSignatures.Count < record.RequiredSignatures)
            {
                LogUtility.Log($"[MintSubmission] Insufficient signatures for lock {record.LockId}: {record.ValidatorSignatures.Count}/{record.RequiredSignatures}",
                    "BaseBridgeMintSubmissionService.SubmitMintWithProof");
                return;
            }

            var rpcUrl = BaseBridgeService.BaseRpcUrl;
            var contractAddress = BaseBridgeService.ContractAddress;
            var chainId = BaseBridgeService.BaseChainId;

            if (string.IsNullOrEmpty(rpcUrl) || string.IsNullOrEmpty(contractAddress))
            {
                LogUtility.Log("[MintSubmission] RPC or contract not configured.", "BaseBridgeMintSubmissionService.SubmitMintWithProof");
                return;
            }

            // The contract mints a lock once: one it has minted already (an earlier submission that was in no block when
            // its wait ended, or the holder's own) is minted, and is not submitted again.
            if (await UserBridgeMintService.LockMintedOnBaseAsync(record.LockId) == true)
            {
                BridgeLockRecord.UpdateStatus(record.LockId, BridgeLockStatus.Minted, errorMessage: "");
                return;
            }

            // Mark as ProofSubmitted to prevent duplicate submissions
            BridgeLockRecord.UpdateStatus(record.LockId, BridgeLockStatus.ProofSubmitted);

            try
            {
                // Create Web3 instance with signer
                var account = new Account(ethKey, chainId);
                var web3 = new Web3(account, rpcUrl);

                var contract = web3.Eth.GetContract(MINT_WITH_PROOF_ABI, contractAddress);
                var mintFunction = contract.GetFunction("mintWithProof");

                // Prepare signature bytes array
                var sigValues = record.ValidatorSignatures.Values.ToList();
                var sigBytes = sigValues.Select(s => VerifiedXCore.Extensions.GenericExtensions.HexToByteArray(
                    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s)).ToList();

                LogUtility.Log($"[MintSubmission] Submitting mintWithProof for lock {record.LockId}: to={record.EvmDestination}, amount={record.AmountSats}sats, nonce={record.MintNonce}, sigs={sigValues.Count}",
                    "BaseBridgeMintSubmissionService.SubmitMintWithProof");

                // Estimate gas first
                var gasEstimate = await mintFunction.EstimateGasAsync(
                    record.EvmDestination,
                    new BigInteger(record.AmountSats),
                    record.LockId,
                    new BigInteger(record.MintNonce),
                    sigBytes);

                // Add 20% buffer to gas estimate
                var gasLimit = new BigInteger((double)gasEstimate.Value * 1.2);

                var txHash = await mintFunction.SendTransactionAsync(
                    account.Address,
                    new HexBigInteger(gasLimit),
                    null, // gas price (let provider decide)
                    null, // value
                    record.EvmDestination,
                    new BigInteger(record.AmountSats),
                    record.LockId,
                    new BigInteger(record.MintNonce),
                    sigBytes);

                LogUtility.Log($"[MintSubmission] mintWithProof TX submitted for lock {record.LockId}: {txHash}",
                    "BaseBridgeMintSubmissionService.SubmitMintWithProof");

                // Sent, and not yet in a block: the lock is minted only once Base holds the transaction in one.
                BridgeLockRecord.UpdateStatus(record.LockId, BridgeLockStatus.ProofSubmitted, baseTxHash: txHash);

                // Wait for confirmation in background
                _ = Task.Run(async () =>
                {
                    bool? receipt = null;
                    for (var attempts = 0; receipt == null && attempts < UserBridgeMintService.MintReceiptPolls; attempts++)
                    {
                        await Task.Delay(UserBridgeMintService.MintReceiptPollMs);
                        receipt = await UserBridgeMintService.MintReceiptAsync(txHash);
                    }
                    await ResolveSentMintAsync(record.LockId, txHash, receipt);
                });
            }
            catch (Exception ex)
            {
                // Revert status back to AttestationReady so it can be retried
                BridgeLockRecord.UpdateStatus(record.LockId, BridgeLockStatus.AttestationReady,
                    errorMessage: $"Mint submission error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Manual trigger to submit mintWithProof for a specific lock ID.
        /// Used by the API for testing or manual intervention.
        /// </summary>
        public static async Task<(bool Success, string Message)> ManualSubmitMintWithProof(string lockId)
        {
            try
            {
                var record = BridgeLockRecord.GetByLockId(lockId);
                if (record == null)
                    return (false, $"Lock not found: {lockId}");

                if (record.Status != BridgeLockStatus.AttestationReady)
                    return (false, $"Lock {lockId} is not in AttestationReady status. Current status: {record.Status}");

                var ethKey = GetValidatorEthKey();
                if (ethKey == null)
                    return (false, "Validator ETH key not available. Cannot submit Base transactions.");

                await SubmitMintWithProof(record, ethKey);
                return (true, $"mintWithProof submitted for lock {lockId}. Check status for confirmation.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Gets the validator's EthECKey from the local account store.
        /// </summary>
        private static EthECKey? GetValidatorEthKey()
        {
            try
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return null;
                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                if (account == null) return null;
                var privHex = account.GetKey;
                if (string.IsNullOrEmpty(privHex)) return null;

                if (privHex.Length % 2 != 0)
                    privHex = "0" + privHex;

                var bytes = HexByteUtility.HexToByte(privHex);
                return new EthECKey(bytes, true);
            }
            catch
            {
                return null;
            }
        }
    }
}
