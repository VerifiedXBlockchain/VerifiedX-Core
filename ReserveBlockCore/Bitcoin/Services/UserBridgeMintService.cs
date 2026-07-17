using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Utilities;
using System.Numerics;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// User-driven bridge mint service. The user's own node collects validator attestation
    /// signatures and submits <c>mintWithProof</c> to the Base contract using the user's
    /// derived ETH key. The user pays gas — no caster involvement.
    /// </summary>
    public static class UserBridgeMintService
    {
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
        /// Full end-to-end bridge flow for the user:
        /// 1. Wait for VFX lock to confirm on-chain
        /// 2. Collect validator attestation signatures
        /// 3. Submit mintWithProof to Base using the user's own ETH key
        /// Returns immediately with initial status; runs the rest in background.
        /// </summary>
        public static async Task<(bool Success, string Message, string? LockId)> ExecuteBridgeToBase(
            string scUID, string ownerAddress, decimal amount, string evmDestination)
        {
            try
            {
                if (!BaseBridgeService.IsBridgeConfigured)
                    return (false, "Base bridge not configured. Set BaseBridgeContract in config.", null);

                // S3C §6.3: block bridge for S3C contracts early (also blocked in CreateBridgeLockTx + consensus).
                if (VBTCService.ResolveContractIsS3C(scUID))
                    return (false, "vBTC.b bridge is not available for S3C contracts. Withdraw to your own public companion contract to bridge.", null);

                // Validate EVM address
                if (string.IsNullOrWhiteSpace(evmDestination) || !evmDestination.StartsWith("0x") || evmDestination.Length != 42)
                    return (false, "Invalid EVM destination address. Expected 0x + 40 hex characters.", null);

                // Derive the user's Base address (gas payer)
                var userBaseAddress = ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);
                if (string.IsNullOrEmpty(userBaseAddress))
                    return (false, "Cannot derive Base address from your VFX account. Private key may be missing.", null);

                // Check ETH balance for gas
                var (ethOk, ethBal, ethMsg) = await BaseBridgeService.GetEthBalanceAsync(userBaseAddress);
                if (!ethOk || ethBal <= 0)
                    return (false, $"Your Base address ({userBaseAddress}) has no ETH for gas. Fund it before bridging.", null);

                // Create the VFX bridge lock transaction
                var lockResult = await VBTCService.CreateBridgeLockTx(scUID, ownerAddress, amount, evmDestination);
                if (!lockResult.Success)
                    return (false, lockResult.TxHashOrError, null);

                var lockId = lockResult.LockId;

                // Save BridgeLockRecord immediately on user's node for tracking
                var record = new BridgeLockRecord
                {
                    LockId = lockId,
                    SmartContractUID = scUID,
                    OwnerAddress = ownerAddress,
                    Amount = amount,
                    AmountSats = (long)(amount * 100_000_000M),
                    EvmDestination = evmDestination,
                    VfxLockTxHash = lockResult.TxHashOrError,
                    Status = BridgeLockStatus.Locked,
                    CreatedAtUtc = TimeUtil.GetTime()
                };
                BridgeLockRecord.Save(record);

                // Fire and forget: background task to wait for confirmation, collect sigs, submit mint
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WaitCollectAndMint(record, ownerAddress, userBaseAddress);
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[UserBridgeMint] Background error for lock {lockId}: {ex.Message}",
                            "UserBridgeMintService.ExecuteBridgeToBase");
                        BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                            errorMessage: $"Bridge failed: {ex.Message}");
                    }
                });

                return (true,
                    $"Bridge lock created (Lock ID: {lockId}). Waiting for VFX confirmation, then collecting validator signatures and minting on Base.",
                    lockId);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Background: wait for VFX lock confirmation → collect attestations → submit mintWithProof.
        /// </summary>
        private static async Task WaitCollectAndMint(BridgeLockRecord record, string ownerAddress, string userBaseAddress)
        {
            var lockId = record.LockId;

            // Step 1: Wait for VFX lock to be included in a block
            LogUtility.Log($"[UserBridgeMint] Waiting for VFX lock confirmation: {lockId}", "UserBridgeMintService");
            var confirmed = await VBTCService.WaitForBridgeLockInStateAsync(lockId, 180_000);
            if (!confirmed)
            {
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                    errorMessage: "VFX lock not confirmed within timeout (180s).");
                LogUtility.Log($"[UserBridgeMint] VFX lock timeout: {lockId}", "UserBridgeMintService");
                return;
            }

            // Refresh record and populate VfxLockBlockHeight from consensus state if needed
            record = BridgeLockRecord.GetByLockId(lockId) ?? record;
            if (record.VfxLockBlockHeight <= 0)
            {
                // Derive block height from the confirmed lock TX in state
                record.VfxLockBlockHeight = Globals.LastBlock.Height;
                BridgeLockRecord.Save(record);
            }
            LogUtility.Log($"[UserBridgeMint] VFX lock confirmed for {lockId} at block {record.VfxLockBlockHeight}", "UserBridgeMintService");

            // Step 2: Collect validator attestation signatures
            // Use BaseBridgeService.BaseChainId consistently (same source used for mint submission)
            var requiredSigs = await BaseBridgeService.GetRequiredMintSignaturesFromChainAsync();
            var contractAddress = BaseBridgeService.ContractAddress;
            var chainId = (long)BaseBridgeService.BaseChainId;
            var nonce = record.VfxLockBlockHeight;

            record.MintNonce = nonce;
            record.RequiredSignatures = requiredSigs;
            record.Status = BridgeLockStatus.AttestationPending;
            BridgeLockRecord.Save(record);

            LogUtility.Log($"[UserBridgeMint] Collecting attestations for {lockId}. Need {requiredSigs} signatures.", "UserBridgeMintService");

            var signatures = await CollectValidatorSignatures(record, nonce, chainId, contractAddress, requiredSigs);

            if (signatures == null || signatures.Count < requiredSigs)
            {
                var msg = $"Could not collect enough validator signatures ({signatures?.Count ?? 0}/{requiredSigs}).";
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.AttestationPending, errorMessage: msg);
                LogUtility.Log($"[UserBridgeMint] {msg} Lock: {lockId}", "UserBridgeMintService");
                return;
            }

            record.ValidatorSignatures = signatures;
            record.Status = BridgeLockStatus.AttestationReady;
            BridgeLockRecord.Save(record);
            LogUtility.Log($"[UserBridgeMint] Attestations ready for {lockId}: {signatures.Count}/{requiredSigs}", "UserBridgeMintService");

            // Step 3: Submit mintWithProof using user's own ETH key
            await SubmitMintWithProofAsUser(record, ownerAddress, userBaseAddress);
        }

        /// <summary>
        /// Collect attestation signatures from active validators.
        /// </summary>
        private static async Task<Dictionary<string, string>?> CollectValidatorSignatures(
            BridgeLockRecord record, long nonce, long chainId, string contractAddress, int requiredSigs)
        {
            var signatures = new Dictionary<string, string>();
            var validators = VBTCValidatorRegistry.GetPublicValidators();
            if (validators == null || validators.Count == 0)
            {
                LogUtility.Log("[UserBridgeMint] No active validators found.", "UserBridgeMintService");
                return signatures;
            }

            var client = Globals.HttpClientFactory?.CreateClient() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // Retry up to 3 rounds with delay between rounds
            for (int attempt = 0; attempt < 3 && signatures.Count < requiredSigs; attempt++)
            {
                if (attempt > 0)
                {
                    LogUtility.Log($"[UserBridgeMint] Retry round {attempt + 1} for attestations. Have {signatures.Count}/{requiredSigs}.", "UserBridgeMintService");
                    await Task.Delay(15_000);
                    // Refresh validator list
                    validators = VBTCValidatorRegistry.GetPublicValidators();
                }

                foreach (var v in validators)
                {
                    if (string.IsNullOrEmpty(v.IPAddress)) continue;
                    // Use the validator's registered Base address for dedup (matches the address
                    // that signs attestations and is registered on the Base contract).
                    // Fall back to ValidatorAddress if BaseAddress not set.
                    var dedupKey = !string.IsNullOrEmpty(v.BaseAddress) ? v.BaseAddress : v.ValidatorAddress;
                    if (string.IsNullOrEmpty(dedupKey) || signatures.ContainsKey(dedupKey)) continue;

                    var url = $"http://{v.IPAddress.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/Validator/SignMintAttestation";
                    var body = JsonConvert.SerializeObject(new MintAttestationRequest
                    {
                        LockId = record.LockId,
                        EvmDestination = record.EvmDestination,
                        AmountSats = record.AmountSats,
                        Nonce = nonce,
                        ChainId = chainId,
                        ContractAddress = contractAddress,
                        SmartContractUID = record.SmartContractUID
                    });

                    try
                    {
                        using var content = new StringContent(body, Encoding.UTF8, "application/json");
                        var resp = await client.PostAsync(url, content);
                        var txt = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode) continue;
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(txt);
                        if (jo["success"]?.ToObject<bool>() != true) continue;
                        var sig = jo["signature"]?.ToString();
                        if (!string.IsNullOrEmpty(sig))
                        {
                            signatures[dedupKey] = sig;
                            LogUtility.Log($"[UserBridgeMint] Got attestation from validator {dedupKey} for lock {record.LockId}", "UserBridgeMintService");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtility.Log($"[UserBridgeMint] Failed to get attestation from {v.IPAddress}: {ex.Message}", "UserBridgeMintService");
                    }

                    if (signatures.Count >= requiredSigs)
                        break;
                }
            }

            return signatures;
        }

        /// <summary>
        /// Submit mintWithProof on Base using the user's own derived ETH key.
        /// User pays gas.
        /// </summary>
        private static async Task SubmitMintWithProofAsUser(BridgeLockRecord record, string ownerAddress, string userBaseAddress)
        {
            var lockId = record.LockId;

            try
            {
                // Get user's ETH private key
                var account = AccountData.GetSingleAccount(ownerAddress);
                if (account == null)
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: "User account not found.");
                    return;
                }

                var privHex = account.GetKey;
                if (string.IsNullOrEmpty(privHex))
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: "Private key not available.");
                    return;
                }

                if (privHex.Length % 2 != 0)
                    privHex = "0" + privHex;

                var privBytes = HexByteUtility.HexToByte(privHex);
                var ethKey = new EthECKey(privBytes, true);

                var rpcUrl = BaseBridgeService.BaseRpcUrl;
                var contractAddress = BaseBridgeService.ContractAddress;
                var chainId = BaseBridgeService.BaseChainId;

                if (string.IsNullOrEmpty(rpcUrl) || string.IsNullOrEmpty(contractAddress))
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: "RPC or contract not configured.");
                    return;
                }

                // Mark as submitting
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.ProofSubmitted);

                // Create Web3 instance with the user's key
                var ethAccount = new Account(ethKey, chainId);
                var web3 = new Web3(ethAccount, rpcUrl);

                var contract = web3.Eth.GetContract(MINT_WITH_PROOF_ABI, contractAddress);
                var mintFunction = contract.GetFunction("mintWithProof");

                // Prepare signature bytes array
                var sigValues = record.ValidatorSignatures!.Values.ToList();
                var sigBytes = sigValues.Select(s => ReserveBlockCore.Extensions.GenericExtensions.HexToByteArray(
                    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s)).ToList();

                LogUtility.Log($"[UserBridgeMint] Submitting mintWithProof for lock {lockId}: to={record.EvmDestination}, amount={record.AmountSats}sats, nonce={record.MintNonce}, sigs={sigValues.Count}, payer={userBaseAddress}",
                    "UserBridgeMintService");

                // Estimate gas
                var gasEstimate = await mintFunction.EstimateGasAsync(
                    record.EvmDestination,
                    new BigInteger(record.AmountSats),
                    record.LockId,
                    new BigInteger(record.MintNonce),
                    sigBytes);

                // Add 20% buffer
                var gasLimit = new BigInteger((double)gasEstimate.Value * 1.2);

                var txHash = await mintFunction.SendTransactionAsync(
                    ethAccount.Address,
                    new HexBigInteger(gasLimit),
                    null, // gas price (let provider decide)
                    null, // value
                    record.EvmDestination,
                    new BigInteger(record.AmountSats),
                    record.LockId,
                    new BigInteger(record.MintNonce),
                    sigBytes);

                LogUtility.Log($"[UserBridgeMint] mintWithProof TX submitted for lock {lockId}: {txHash}",
                    "UserBridgeMintService");

                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.MintedOnBase, baseTxHash: txHash);

                // Wait for confirmation
                int attempts = 0;
                Nethereum.RPC.Eth.DTOs.TransactionReceipt? receipt = null;
                while (receipt == null && attempts < 60)
                {
                    await Task.Delay(5000);
                    receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    attempts++;
                }

                if (receipt != null && receipt.Status.Value == 1)
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted, baseTxHash: txHash);
                    LogUtility.Log($"[UserBridgeMint] mintWithProof CONFIRMED for lock {lockId} in block {receipt.BlockNumber.Value}",
                        "UserBridgeMintService");
                }
                else if (receipt != null)
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                        errorMessage: $"mintWithProof TX reverted: {txHash}");
                    LogUtility.Log($"[UserBridgeMint] mintWithProof REVERTED for lock {lockId}: {txHash}",
                        "UserBridgeMintService");
                }
                else
                {
                    LogUtility.Log($"[UserBridgeMint] mintWithProof TX pending (no receipt after 5min): {txHash}",
                        "UserBridgeMintService");
                }
            }
            catch (Exception ex)
            {
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                    errorMessage: $"Mint submission error: {ex.Message}");
                LogUtility.Log($"[UserBridgeMint] Error submitting mintWithProof for {lockId}: {ex.Message}",
                    "UserBridgeMintService");
            }
        }

        /// <summary>
        /// Force retry a stuck bridge mint. Handles two scenarios:
        /// <para><b>Case A — Lock TX was mined (on-chain):</b> Skips wait, goes straight to
        /// attestation collection + mintWithProof submission.</para>
        /// <para><b>Case B — Lock TX was never mined (e.g. mempool deleted):</b> Re-creates and
        /// re-broadcasts the VFX bridge lock TX using the same lock ID, then runs the full
        /// WaitCollectAndMint flow.</para>
        /// Also checks the Base contract to avoid double-minting if the lock was already used.
        /// </summary>
        public static async Task<(bool Success, string Message)> ForceRetryMintForLock(string lockId, string ownerAddress)
        {
            try
            {
                if (!BaseBridgeService.IsBridgeConfigured)
                    return (false, "Base bridge not configured. Set BaseBridgeContract in config.");

                // 1. Check if the lock has already been minted on the Base contract (avoid wasting gas)
                try
                {
                    foreach (var rpcUrl in BaseBridgeService.RpcUrlCandidates())
                    {
                        try
                        {
                            var web3 = new Nethereum.Web3.Web3(rpcUrl);
                            var contract = web3.Eth.GetContract(BaseBridgeService.CONTRACT_ABI, BaseBridgeService.ContractAddress);
                            var isUsedFunc = contract.GetFunction("isLockIdUsed");
                            var alreadyUsed = await isUsedFunc.CallAsync<bool>(lockId);
                            if (alreadyUsed)
                            {
                                var existingRec = BridgeLockRecord.GetByLockId(lockId);
                                if (existingRec != null && existingRec.Status != BridgeLockStatus.Minted && existingRec.Status != BridgeLockStatus.MintedOnBase)
                                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted);
                                return (false, $"Lock {lockId} has already been minted on the Base contract. No action needed.");
                            }
                            break;
                        }
                        catch { /* try next RPC */ }
                    }
                }
                catch { /* non-fatal */ }

                // 2. Derive Base address + check ETH for gas
                var userBaseAddress = ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);
                if (string.IsNullOrEmpty(userBaseAddress))
                    return (false, "Cannot derive Base address from your account. Private key may be missing.");

                var (ethOk, ethBal, _) = await BaseBridgeService.GetEthBalanceAsync(userBaseAddress);
                if (!ethOk || ethBal <= 0)
                    return (false, $"Your Base address ({userBaseAddress}) has no ETH for gas. Fund it before retrying.");

                // 3. Check consensus state — is the lock already on-chain?
                var consensusLock = VBTCBridgeLockState.GetByLockId(lockId);

                if (consensusLock != null)
                {
                    // ═══ CASE A: Lock TX was mined — skip straight to attestation + mint ═══
                    if (consensusLock.OwnerAddress != ownerAddress)
                        return (false, "You are not the owner of this lock.");

                    var record = BridgeLockRecord.GetByLockId(lockId);
                    if (record == null)
                    {
                        LogUtility.Log($"[UserBridgeMint] ForceRetry CaseA: Reconstructing BridgeLockRecord from consensus state for lock {lockId}",
                            "UserBridgeMintService.ForceRetryMintForLock");
                        record = new BridgeLockRecord
                        {
                            LockId = lockId,
                            SmartContractUID = consensusLock.SmartContractUID,
                            OwnerAddress = consensusLock.OwnerAddress,
                            Amount = consensusLock.Amount,
                            AmountSats = consensusLock.AmountSats,
                            EvmDestination = consensusLock.EvmDestination,
                            VfxLockTxHash = consensusLock.LockTxHash,
                            VfxLockConfirmedOnChain = true,
                            VfxLockBlockHeight = Globals.LastBlock.Height,
                            Status = BridgeLockStatus.Locked,
                            CreatedAtUtc = consensusLock.LockTimestamp > 0 ? consensusLock.LockTimestamp : TimeUtil.GetTime()
                        };
                        BridgeLockRecord.Save(record);
                    }

                    record.Status = BridgeLockStatus.Locked;
                    record.ErrorMessage = null;
                    record.VfxLockConfirmedOnChain = true;
                    if (record.VfxLockBlockHeight <= 0)
                        record.VfxLockBlockHeight = Globals.LastBlock.Height;
                    BridgeLockRecord.Save(record);

                    LogUtility.Log($"[UserBridgeMint] ForceRetry CaseA (on-chain) for lock {lockId}, owner={ownerAddress}, amount={record.Amount}",
                        "UserBridgeMintService.ForceRetryMintForLock");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await CollectAndMintDirect(record, ownerAddress, userBaseAddress);
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log($"[UserBridgeMint] ForceRetry CaseA error for lock {lockId}: {ex.Message}",
                                "UserBridgeMintService.ForceRetryMintForLock");
                            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                                errorMessage: $"Force retry failed: {ex.Message}");
                        }
                    });

                    return (true, $"Lock {lockId} found on-chain. Collecting validator signatures and submitting mint on Base. Check bridge history for status.");
                }
                else
                {
                    // ═══ CASE B: Lock TX was never mined — re-create and re-broadcast it ═══
                    var record = BridgeLockRecord.GetByLockId(lockId);
                    if (record == null)
                        return (false, $"Lock {lockId} not found on-chain and no local BridgeLockRecord exists. Cannot retry.");

                    if (record.OwnerAddress != ownerAddress)
                        return (false, "You are not the owner of this lock.");

                    LogUtility.Log($"[UserBridgeMint] ForceRetry CaseB (NOT on-chain): Re-creating lock TX for {lockId}, scUID={record.SmartContractUID}, amount={record.Amount}, evmDest={record.EvmDestination}",
                        "UserBridgeMintService.ForceRetryMintForLock");

                    // Re-create and broadcast the VFX bridge lock TX with the same lock ID
                    var lockResult = await VBTCService.CreateBridgeLockTx(
                        record.SmartContractUID, ownerAddress, record.Amount, record.EvmDestination,
                        lockIdOverride: lockId);

                    if (!lockResult.Success)
                        return (false, $"Failed to re-create lock TX: {lockResult.TxHashOrError}");

                    // Update the local record with the new TX hash
                    record.VfxLockTxHash = lockResult.TxHashOrError;
                    record.VfxLockConfirmedOnChain = false;
                    record.Status = BridgeLockStatus.Locked;
                    record.ErrorMessage = null;
                    BridgeLockRecord.Save(record);

                    LogUtility.Log($"[UserBridgeMint] ForceRetry CaseB: Lock TX re-broadcast for {lockId}, newTxHash={lockResult.TxHashOrError}",
                        "UserBridgeMintService.ForceRetryMintForLock");

                    // Run the full flow: wait for on-chain confirmation → collect attestations → mint
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WaitCollectAndMint(record, ownerAddress, userBaseAddress);
                        }
                        catch (Exception ex)
                        {
                            LogUtility.Log($"[UserBridgeMint] ForceRetry CaseB error for lock {lockId}: {ex.Message}",
                                "UserBridgeMintService.ForceRetryMintForLock");
                            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                                errorMessage: $"Force retry failed: {ex.Message}");
                        }
                    });

                    return (true, $"Lock TX re-broadcast for {lockId} (tx: {lockResult.TxHashOrError}). Waiting for VFX confirmation, then collecting attestations and minting on Base. Check bridge history for status.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Collect attestations and submit mintWithProof directly — skips the WaitForBridgeLockInState step.
        /// Used by ForceRetryMintForLock when we already know the lock is confirmed on-chain.
        /// </summary>
        private static async Task CollectAndMintDirect(BridgeLockRecord record, string ownerAddress, string userBaseAddress)
        {
            var lockId = record.LockId;

            // Collect validator attestation signatures
            var requiredSigs = await BaseBridgeService.GetRequiredMintSignaturesFromChainAsync();
            var contractAddress = BaseBridgeService.ContractAddress;
            var chainId = (long)BaseBridgeService.BaseChainId;
            var nonce = record.VfxLockBlockHeight;

            record.MintNonce = nonce;
            record.RequiredSignatures = requiredSigs;
            record.Status = BridgeLockStatus.AttestationPending;
            BridgeLockRecord.Save(record);

            LogUtility.Log($"[UserBridgeMint] ForceRetry: Collecting attestations for {lockId}. Need {requiredSigs} signatures.", "UserBridgeMintService");

            var signatures = await CollectValidatorSignatures(record, nonce, chainId, contractAddress, requiredSigs);

            if (signatures == null || signatures.Count < requiredSigs)
            {
                var msg = $"Could not collect enough validator signatures ({signatures?.Count ?? 0}/{requiredSigs}).";
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.AttestationPending, errorMessage: msg);
                LogUtility.Log($"[UserBridgeMint] ForceRetry: {msg} Lock: {lockId}", "UserBridgeMintService");
                return;
            }

            record.ValidatorSignatures = signatures;
            record.Status = BridgeLockStatus.AttestationReady;
            BridgeLockRecord.Save(record);
            LogUtility.Log($"[UserBridgeMint] ForceRetry: Attestations ready for {lockId}: {signatures.Count}/{requiredSigs}", "UserBridgeMintService");

            // Submit mintWithProof using user's own ETH key
            await SubmitMintWithProofAsUser(record, ownerAddress, userBaseAddress);
        }

        /// <summary>
        /// Manual retry: collect attestations and submit mint for an existing lock.
        /// Used when a previous attempt failed or timed out.
        /// </summary>
        public static async Task<(bool Success, string Message)> RetryMintForLock(string lockId, string ownerAddress)
        {
            try
            {
                var record = BridgeLockRecord.GetByLockId(lockId);
                if (record == null)
                    return (false, $"Lock not found: {lockId}");

                if (record.OwnerAddress != ownerAddress)
                    return (false, "You are not the owner of this lock.");

                if (record.Status == BridgeLockStatus.Minted || record.Status == BridgeLockStatus.MintedOnBase)
                    return (false, "This lock has already been minted.");

                var userBaseAddress = ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);
                if (string.IsNullOrEmpty(userBaseAddress))
                    return (false, "Cannot derive Base address from your account.");

                var (ethOk, ethBal, _) = await BaseBridgeService.GetEthBalanceAsync(userBaseAddress);
                if (!ethOk || ethBal <= 0)
                    return (false, $"Your Base address ({userBaseAddress}) has no ETH for gas.");

                // Reset status to allow retry
                record.Status = BridgeLockStatus.Locked;
                record.ErrorMessage = null;
                BridgeLockRecord.Save(record);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WaitCollectAndMint(record, ownerAddress, userBaseAddress);
                    }
                    catch (Exception ex)
                    {
                        BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed,
                            errorMessage: $"Retry failed: {ex.Message}");
                    }
                });

                return (true, "Retrying mint process. Check bridge status for updates.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}
