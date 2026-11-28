using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using System.Numerics;
using System.Text;

namespace VerifiedXCore.Bitcoin.Services
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
                return (false, $"Error: {ApiErrorText.For(ex)}", null);
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

            // Steps 2 and 3: collect the signatures and submit mintWithProof with the user's own key.
            await CollectAndMint(record, ownerAddress, userBaseAddress, "");
        }

        /// <summary>What became of a mint's submission to Base.</summary>
        internal enum MintSend
        {
            /// <summary>Base holds the mint in a block.</summary>
            Confirmed,
            /// <summary>Sent to Base, and not seen in a block within the wait.</summary>
            Sent,
            /// <summary>Nothing was sent: Base refused the gas estimate or the transaction.</summary>
            Refused,
            /// <summary>Base holds the transaction in a block, reverted.</summary>
            Reverted,
            /// <summary>Nothing could be sent from this node (no account, key or configuration).</summary>
            NotPossible,
        }

        /// <summary>How many times signatures are collected and the mint submitted before the lock is marked failed.</summary>
        internal const int MintAttempts = 3;
        /// <summary>Milliseconds between two such attempts.</summary>
        internal static int MintRetryDelayMs = 15_000;

        private static Task CollectAndMint(BridgeLockRecord record, string ownerAddress, string userBaseAddress, string tag) =>
            CollectAndMint(record, tag,
                async () =>
                {
                    // Use BaseBridgeService.BaseChainId consistently (same source used for mint submission)
                    var required = await BaseBridgeService.GetRequiredMintSignaturesFromChainAsync();
                    record.MintNonce = record.VfxLockBlockHeight;
                    record.RequiredSignatures = required;
                    record.Status = BridgeLockStatus.AttestationPending;
                    BridgeLockRecord.Save(record);
                    LogUtility.Log($"[UserBridgeMint] {tag}Collecting attestations for {record.LockId}. Need {required} signatures.", "UserBridgeMintService");
                    return (await CollectValidatorSignatures(record, record.MintNonce, (long)BaseBridgeService.BaseChainId, BaseBridgeService.ContractAddress, required), required);
                },
                () => SubmitMintWithProofAsUser(record, ownerAddress, userBaseAddress));

        /// <summary>
        /// Collects a lock's signatures and submits its mint. When Base refuses the mint before anything is sent, the
        /// signatures are collected afresh and it is submitted again, up to <see cref="MintAttempts"/> times: the
        /// contract counts signatures against its signer lists as they are when the mint runs, so signatures collected
        /// before a change of those lists, or in a form the contract does not read, are replaced by ones it counts.
        /// Only after the last refusal is the lock marked failed.
        /// </summary>
        internal static async Task CollectAndMint(BridgeLockRecord record, string tag,
            Func<Task<(Dictionary<string, string>? Signatures, int Required)>> collect, Func<Task<(MintSend Outcome, string? Reason)>> submit)
        {
            var lockId = record.LockId;
            // One mint of a lock at a time: a second one started meanwhile would send a second transaction.
            if (!MintsUnderWay.TryAdd(lockId, 0))
            {
                LogUtility.Log($"[UserBridgeMint] {tag}A mint of {lockId} is already under way; not started again.", "UserBridgeMintService");
                return;
            }
            try
            {
                // A run that waited behind another finds what that one left: a lock it minted is not collected for again.
                if (BridgeLockRecord.GetByLockId(lockId)?.Status == BridgeLockStatus.Minted) return;
                await CollectAndMintOnce(record, tag, collect, submit);
            }
            finally { MintsUnderWay.TryRemove(lockId, out _); }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MintsUnderWay = new(StringComparer.Ordinal);

        /// <summary>Whether a mint of this lock is being collected for or submitted on this node right now.</summary>
        internal static bool MintIsUnderWay(string? lockId) => !string.IsNullOrEmpty(lockId) && MintsUnderWay.ContainsKey(lockId);

        /// <summary>Tests only: answers whether the Base contract has minted a lock (null: could not be asked).</summary>
        internal static Func<string, Task<bool?>>? LockMintedOnBaseForTests;

        /// <summary>Whether the Base contract has already minted this lock (it mints a lock once); null when no provider could be asked.</summary>
        internal static async Task<bool?> LockMintedOnBaseAsync(string lockId)
        {
            if (LockMintedOnBaseForTests != null) return await LockMintedOnBaseForTests(lockId);
            if (!BaseBridgeService.IsBridgeConfigured) return null;
            foreach (var url in BaseBridgeService.RpcUrlCandidates())
            {
                try
                {
                    var contract = new Web3(url).Eth.GetContract(BaseBridgeService.CONTRACT_ABI, BaseBridgeService.ContractAddress);
                    return await contract.GetFunction("isLockIdUsed").CallAsync<bool>(lockId);
                }
                catch { /* the next provider */ }
            }
            return null;
        }

        /// <summary>Marks the lock minted when the Base contract says it has minted it. True when it has.</summary>
        private static async Task<bool> MintedAfterAll(string lockId, string tag)
        {
            if (await LockMintedOnBaseAsync(lockId) != true) return false;
            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted, errorMessage: "");
            LogUtility.Log($"[UserBridgeMint] {tag}The Base contract has minted lock {lockId}.", "UserBridgeMintService");
            return true;
        }

        private static async Task CollectAndMintOnce(BridgeLockRecord record, string tag,
            Func<Task<(Dictionary<string, string>? Signatures, int Required)>> collect, Func<Task<(MintSend Outcome, string? Reason)>> submit)
        {
            var lockId = record.LockId;
            for (var attempt = 1; ; attempt++)
            {
                var (signatures, requiredSigs) = await collect();
                if (signatures == null || signatures.Count < requiredSigs)
                {
                    var msg = $"Could not collect enough validator signatures ({signatures?.Count ?? 0}/{requiredSigs}).";
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.AttestationPending, errorMessage: msg);
                    LogUtility.Log($"[UserBridgeMint] {tag}{msg} Lock: {lockId}", "UserBridgeMintService");
                    return;
                }

                record.ValidatorSignatures = signatures;
                record.Status = BridgeLockStatus.AttestationReady;
                BridgeLockRecord.Save(record);
                LogUtility.Log($"[UserBridgeMint] {tag}Attestations ready for {lockId}: {signatures.Count}/{requiredSigs}", "UserBridgeMintService");

                var (outcome, reason) = await submit();
                if (outcome != MintSend.Refused) return;
                // Refused because an earlier submission of this lock went through after all: it is minted.
                if (await MintedAfterAll(lockId, tag)) return;
                if (attempt >= MintAttempts)
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: $"Mint submission error: {reason}");
                    return;
                }
                LogUtility.Log($"[UserBridgeMint] {tag}Base refused the mint for {lockId} ({reason}); collecting signatures again ({attempt}/{MintAttempts}).", "UserBridgeMintService");
                await Task.Delay(MintRetryDelayMs);
            }
        }

        /// <summary>
        /// Collect attestation signatures from active validators.
        /// </summary>
        /// <summary>Waits (bounded) until the lock's block is deep enough that validators will sign for it.</summary>
        private static async Task WaitPastReorgWindow(string? lockId)
        {
            var limit = DateTime.UtcNow.AddSeconds((BaseBridgeAttestationService.MintAttestationDepth + 5) * Math.Max(1, Globals.BlockTime / 1000) + 60);
            while (DateTime.UtcNow < limit)
            {
                var row = string.IsNullOrEmpty(lockId) ? null : VBTCBridgeLockState.GetByLockId(lockId);
                if (row == null || !BaseBridgeAttestationService.InsideReorgWindow(row)) return;
                await Task.Delay(5_000);
            }
        }

        /// <summary>
        /// Asks the validators that are on the Base contract (getValidators()) for the form the contract reads
        /// (MESSAGE_VERSION()), and keeps a signature only when it recovers to the validator it was asked of. Signatures
        /// from anyone else are ignored by the contract, so collecting them ended the round short and the mint reverted.
        /// </summary>
        internal static async Task<Dictionary<string, string>?> CollectValidatorSignatures(
            BridgeLockRecord record, long nonce, long chainId, string contractAddress, int requiredSigs)
        {
            var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Validators sign only once the lock's block is past the reorg window (BaseBridgeAttestationService): wait for
            // that here rather than spend the short retry rounds below on refusals.
            await WaitPastReorgWindow(record.LockId);
            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null)
            {
                LogUtility.Log("[UserBridgeMint] Could not read the Base contract's validators. Minting cannot proceed.", "UserBridgeMintService");
                return signatures;
            }
            if (view.MessageVersion is not (3 or VbtcbTypedData.MessageVersion))
            {
                ErrorLogUtility.LogError($"[UserBridgeMint] The Base contract reads message version {view.MessageVersion}, which this node cannot collect for.", "UserBridgeMintService");
                return signatures;
            }
            requiredSigs = Math.Max(requiredSigs, view.RequiredMintSignatures);
            var typed = view.MessageVersion == VbtcbTypedData.MessageVersion;
            var hash = typed
                ? VbtcbTypedData.MintDigest(record.EvmDestination, record.AmountSats, record.LockId, nonce, chainId, contractAddress)
                : BaseBridgeAttestationService.ConstructMintMessageHash(record.EvmDestination, record.AmountSats, record.LockId, nonce, chainId, contractAddress);

            var validators = ContractSigners(view);
            if (!Enough(view, requiredSigs, validators.Select(v => v.BaseAddress)))
            {
                ErrorLogUtility.LogError($"[UserBridgeMint] Of the Base contract's {view.Validators.Count} validators and {view.CasterList.Count} casters, only {validators.Count(v => v.Validator)} and {validators.Count(v => v.Caster)} can be reached on VFX; the contract requires {requiredSigs} and {view.RequiredCasterSignatures}.", "UserBridgeMintService");
                return signatures;
            }

            var body = JsonConvert.SerializeObject(new MintAttestationRequest
            {
                LockId = record.LockId,
                EvmDestination = record.EvmDestination,
                AmountSats = record.AmountSats,
                Nonce = nonce,
                ChainId = chainId,
                ContractAddress = contractAddress,
                SmartContractUID = record.SmartContractUID,
                MessageVersion = view.MessageVersion
            });

            // Retry up to 3 rounds with delay between rounds
            for (int attempt = 0; attempt < 3 && !Enough(view, requiredSigs, signatures.Keys); attempt++)
            {
                if (attempt > 0)
                {
                    LogUtility.Log($"[UserBridgeMint] Retry round {attempt + 1} for attestations. Have {signatures.Count}/{requiredSigs}.", "UserBridgeMintService");
                    await Task.Delay(15_000);
                    // Refresh validator list
                    validators = ContractSigners(view);
                }

                await AskSignersAsync(validators, body, hash, typed, signatures, signed => Enough(view, requiredSigs, signed), record.LockId);
            }

            if (!Enough(view, requiredSigs, signatures.Keys))
            {
                // Short of either group the contract refuses the mint: nothing to send, and the lock is asked for again.
                LogUtility.Log($"[UserBridgeMint] Lock {record.LockId}: {view.Validators.Count(signatures.ContainsKey)}/{requiredSigs} validator and {view.CasterList.Count(signatures.ContainsKey)}/{view.RequiredCasterSignatures} caster signatures.", "UserBridgeMintService");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            return signatures;
        }

        /// <summary>
        /// Asks each signer that has not signed yet for its signature over a mint, all of them at once, and keeps a
        /// signature only when it recovers to the signer it was asked of. Ends as soon as <paramref name="enough"/> holds
        /// for the signers kept (the requests still out are dropped), or when every signer has answered, refused or run
        /// out of time: one that stays silent, or answers with something long, costs the others nothing. With
        /// <paramref name="graceAfterEnough"/> the signers still out are given that long to answer after enough have, and
        /// their signatures are kept too: spare ones cover a signer the contract stops counting before the mint is sent.
        /// </summary>
        internal static async Task AskSignersAsync(IEnumerable<ContractSigner> signers, string body, byte[] hash, bool typed,
            Dictionary<string, string> signatures, Func<IEnumerable<string>, bool> enough, string lockId, TimeSpan graceAfterEnough = default)
        {
            using var stop = new CancellationTokenSource();
            var kept = new object();
            var enoughAt = false;
            var asked = signers.Where(v => !signatures.ContainsKey(v.BaseAddress)).ToList();
            await Task.WhenAll(asked.Select(async v =>
            {
                try
                {
                    var url = $"http://{v.IPAddress.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/Validator/SignMintAttestation";
                    var txt = await VerifiedXCore.Utilities.NodeHttp.PostForShortReplyAsync(url, body, stop.Token);
                    if (txt == null) return;
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(txt);
                    if (jo["success"]?.ToObject<bool>() != true) return;
                    var sig = jo["signature"]?.ToString();
                    if (string.IsNullOrEmpty(sig)) return;
                    var signer = ValidatorEthKeyService.RecoverSigner(hash, sig, personalMessage: !typed);
                    if (!string.Equals(signer, v.BaseAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        LogUtility.Log($"[UserBridgeMint] Validator {v.VfxAddress} returned a signature the contract would not count as its own for lock {lockId}; ignored.", "UserBridgeMintService");
                        return;
                    }
                    lock (kept)
                    {
                        if (stop.IsCancellationRequested) return; // the collection is over
                        signatures[v.BaseAddress] = sig;
                        if (!enoughAt && enough(signatures.Keys))
                        {
                            enoughAt = true;
                            if (graceAfterEnough > TimeSpan.Zero) stop.CancelAfter(graceAfterEnough);
                            else stop.Cancel();
                        }
                    }
                    LogUtility.Log($"[UserBridgeMint] Got attestation from {v.BaseAddress} for lock {lockId}", "UserBridgeMintService");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    LogUtility.Log($"[UserBridgeMint] Failed to get attestation from {v.IPAddress}: {ex.Message}", "UserBridgeMintService");
                }
            }));
        }

        /// <summary>How many caster records before the head are searched for a contract caster no longer in the committee.</summary>
        internal const int EarlierCasterRecords = 200;

        /// <summary>A key the contract counts (on its validators, its casters or both), and where to ask it.</summary>
        internal sealed record ContractSigner(string BaseAddress, string IPAddress, string VfxAddress, bool Validator, bool Caster);

        /// <summary>
        /// Everyone whose signature the contract counts: the registered vBTC validators whose Base address (derived from
        /// their own key: a registration's FrostPublicKey is the validator's own, by consensus rule) is on its validators or
        /// casters, and the members of the caster committee whose Base address is on its casters, at the address their
        /// record gives.
        /// </summary>
        internal static List<ContractSigner> ContractSigners(BaseBridgeService.SignerView view)
        {
            var onValidators = new HashSet<string>(view.Validators, StringComparer.OrdinalIgnoreCase);
            var onCasters = new HashSet<string>(view.CasterList, StringComparer.OrdinalIgnoreCase);
            var found = new Dictionary<string, ContractSigner>(StringComparer.OrdinalIgnoreCase);
            void Add(string? publicKey, string? ip, string vfxAddress)
            {
                var baseAddress = BaseSignerSetPolicy.BaseAddressOf(publicKey);
                if (baseAddress.Length == 0 || !InputValidationHelper.IsPeerIPAddress(ip) || found.ContainsKey(baseAddress)) return;
                if (onValidators.Contains(baseAddress) || onCasters.Contains(baseAddress))
                    found[baseAddress] = new ContractSigner(baseAddress, ip, vfxAddress, onValidators.Contains(baseAddress), onCasters.Contains(baseAddress));
            }
            foreach (var v in VBTCValidatorRegistry.GetActiveValidators()) Add(v.FrostPublicKey, v.IPAddress, v.ValidatorAddress);
            var head = VerifiedXCore.Services.CasterMembershipStore.GetCurrent();
            foreach (var c in head?.Casters ?? new List<VerifiedXCore.Models.CasterInfo>()) Add(c.PublicKey, c.PeerIP, c.Address);
            // A caster that left the committee stays on the contract for two more days (BaseSignerSetPolicy.ComputeCasters)
            // and still signs: its address is in the records before the head.
            for (var seq = (head?.RecordSeq ?? 0) - 1; head != null && seq >= Math.Max(0, head.RecordSeq - EarlierCasterRecords)
                 && onCasters.Any(b => !found.ContainsKey(b)); seq--)
                foreach (var c in VerifiedXCore.Services.CasterMembershipStore.GetBySeq(seq)?.Casters ?? new List<VerifiedXCore.Models.CasterInfo>())
                    Add(c.PublicKey, c.PeerIP, c.Address);
            return found.Values.ToList();
        }

        /// <summary>Whether these signers meet both of the contract's thresholds: its validators', and its casters' (none on V3).</summary>
        internal static bool Enough(BaseBridgeService.SignerView view, int requiredValidators, IEnumerable<string> signedBaseAddresses)
        {
            var signed = signedBaseAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return view.Validators.Count(signed.Contains) >= requiredValidators && view.CasterList.Count(signed.Contains) >= view.RequiredCasterSignatures;
        }

        /// <summary>
        /// Submit mintWithProof on Base using the user's own derived ETH key.
        /// User pays gas.
        /// </summary>
        /// <summary>Tests only: sends the mint in place of Base, returning its transaction hash or throwing as Base's refusal.</summary>
        internal static Func<BridgeLockRecord, Task<string>>? SendMintForTests;
        /// <summary>Tests only: answers for a mint transaction's receipt (true: in a block and succeeded; false: reverted; null: none).</summary>
        internal static Func<string, Task<bool?>>? MintReceiptForTests;
        /// <summary>How many times, and how far apart in milliseconds, a sent mint's receipt is asked for.</summary>
        internal static int MintReceiptPolls = 60;
        internal static int MintReceiptPollMs = 5_000;

        /// <summary>A mint transaction's receipt on Base: true when it is in a block and succeeded, false when it reverted, null when Base has none (or could not be asked).</summary>
        internal static async Task<bool?> MintReceiptAsync(string? txHash)
        {
            if (string.IsNullOrEmpty(txHash)) return null;
            if (MintReceiptForTests != null) return await MintReceiptForTests(txHash);
            try
            {
                var receipt = await new Web3(BaseBridgeService.BaseRpcUrl).Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                return receipt == null ? null : receipt.Status?.Value == 1;
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether a failed send is Base's node answering that it does not take the transaction (an RPC error, a
        /// revert), so that nothing was sent. A timeout or a broken connection is not: the transaction may have reached
        /// the node before the answer was lost.
        /// </summary>
        internal static bool BaseAnsweredNo(Exception ex) =>
            ex is Nethereum.JsonRpc.Client.RpcResponseException or Nethereum.ABI.FunctionEncoding.SmartContractRevertException
            || ex.GetType().Name == "SmartContractCustomErrorRevertException";

        /// <summary>A send whose answer was lost: the lock is neither failed nor submitted again by this run; a retry asks Base first.</summary>
        private static (MintSend, string?) MayHaveBeenSent(string lockId, Exception ex)
        {
            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.ProofSubmitted,
                errorMessage: $"The mint may have been sent to Base; its answer was lost ({ex.Message}). It can be retried.");
            LogUtility.Log($"[UserBridgeMint] mintWithProof for {lockId}: no answer from Base to the send ({ex.Message}); it may have been sent.", "UserBridgeMintService");
            return (MintSend.Sent, null);
        }

        /// <summary>
        /// Sends the lock's mint to Base and follows it into a block. The lock is minted only once Base holds the
        /// transaction in a block and it succeeded: a transaction hash says Base's node took the transaction, not that
        /// a block has it. Until then the lock is <see cref="BridgeLockStatus.ProofSubmitted"/> with the hash, and can be
        /// submitted again if no block ever takes it. A refusal before anything is sent is reported, not recorded as
        /// a failure: the caller collects signatures again.
        /// </summary>
        internal static async Task<(MintSend Outcome, string? Reason)> SubmitMintWithProofAsUser(BridgeLockRecord record, string ownerAddress, string userBaseAddress)
        {
            var lockId = record.LockId;
            string txHash;
            try
            {
                if (SendMintForTests != null)
                {
                    BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.ProofSubmitted);
                    txHash = await SendMintForTests(record);
                }
                else
                {
                    // Get user's ETH private key
                    var account = AccountData.GetSingleAccount(ownerAddress);
                    if (account == null)
                    {
                        BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: "User account not found.");
                        return (MintSend.NotPossible, "User account not found.");
                    }

                    var privHex = account.GetKey;
                    if (string.IsNullOrEmpty(privHex))
                    {
                        BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: "Private key not available.");
                        return (MintSend.NotPossible, "Private key not available.");
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
                        return (MintSend.NotPossible, "RPC or contract not configured.");
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
                    var sigBytes = sigValues.Select(s => VerifiedXCore.Extensions.GenericExtensions.HexToByteArray(
                        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s)).ToList();

                    LogUtility.Log($"[UserBridgeMint] Submitting mintWithProof for lock {lockId}: to={record.EvmDestination}, amount={record.AmountSats}sats, nonce={record.MintNonce}, sigs={sigValues.Count}, payer={userBaseAddress}",
                        "UserBridgeMintService");

                    // Estimate gas. Nothing is sent by this: any failure of it leaves nothing on Base.
                    var gasEstimate = await mintFunction.EstimateGasAsync(
                        record.EvmDestination,
                        new BigInteger(record.AmountSats),
                        record.LockId,
                        new BigInteger(record.MintNonce),
                        sigBytes);

                    // Add 20% buffer
                    var gasLimit = new BigInteger((double)gasEstimate.Value * 1.2);

                    try
                    {
                        txHash = await mintFunction.SendTransactionAsync(
                            ethAccount.Address,
                            new HexBigInteger(gasLimit),
                            null, // gas price (let provider decide)
                            null, // value
                            record.EvmDestination,
                            new BigInteger(record.AmountSats),
                            record.LockId,
                            new BigInteger(record.MintNonce),
                            sigBytes);
                    }
                    catch (Exception ex) when (!BaseAnsweredNo(ex))
                    {
                        return MayHaveBeenSent(lockId, ex);
                    }
                }
            }
            catch (Exception ex) when (SendMintForTests != null && !BaseAnsweredNo(ex))
            {
                return MayHaveBeenSent(lockId, ex);
            }
            catch (Exception ex)
            {
                // Nothing was sent. The caller decides what the lock becomes.
                LogUtility.Log($"[UserBridgeMint] Base refused mintWithProof for {lockId}: {ex.Message}", "UserBridgeMintService");
                return (MintSend.Refused, ex.Message);
            }

            LogUtility.Log($"[UserBridgeMint] mintWithProof TX submitted for lock {lockId}: {txHash}", "UserBridgeMintService");
            // Sent, and not yet in a block: the hash is kept with the lock, which is not minted yet.
            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.ProofSubmitted, baseTxHash: txHash,
                errorMessage: "The mint was sent to Base and is not in a block yet.");

            bool? receipt = null;
            for (var attempts = 0; receipt == null && attempts < MintReceiptPolls; attempts++)
            {
                await Task.Delay(MintReceiptPollMs);
                receipt = await MintReceiptAsync(txHash); // a provider that cannot be asked is asked again
            }

            if (receipt == true)
            {
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted, baseTxHash: txHash, errorMessage: "");
                LogUtility.Log($"[UserBridgeMint] mintWithProof CONFIRMED for lock {lockId}: {txHash}", "UserBridgeMintService");
                return (MintSend.Confirmed, null);
            }
            if (receipt == false)
            {
                // Reverted because another submission of this lock was in a block first: it is minted.
                if (await MintedAfterAll(lockId, "")) return (MintSend.Confirmed, null);
                BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Failed, errorMessage: $"mintWithProof TX reverted: {txHash}");
                LogUtility.Log($"[UserBridgeMint] mintWithProof REVERTED for lock {lockId}: {txHash}", "UserBridgeMintService");
                return (MintSend.Reverted, $"mintWithProof TX reverted: {txHash}");
            }
            var waited = (long)MintReceiptPolls * MintReceiptPollMs / 1000;
            BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.ProofSubmitted, baseTxHash: txHash,
                errorMessage: $"The mint was sent to Base ({txHash}) and was in no block after {waited} s. It can be retried.");
            LogUtility.Log($"[UserBridgeMint] mintWithProof TX {txHash} for lock {lockId} was in no Base block after {waited} s.", "UserBridgeMintService");
            return (MintSend.Sent, null);
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
                // Before anything is written: a retry turned away must not change the lock a mint is working on.
                if (MintIsUnderWay(lockId))
                    return (false, "A mint of this lock is already under way. Check bridge history for status.");
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
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// Collect attestations and submit mintWithProof directly — skips the WaitForBridgeLockInState step.
        /// Used by ForceRetryMintForLock when we already know the lock is confirmed on-chain.
        /// </summary>
        private static Task CollectAndMintDirect(BridgeLockRecord record, string ownerAddress, string userBaseAddress) =>
            CollectAndMint(record, ownerAddress, userBaseAddress, "ForceRetry: ");

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

                if (record.Status == BridgeLockStatus.Minted)
                    return (false, "This lock has already been minted.");
                // A mint that was sent is minted only if Base holds it in a block: asked now, since it may have been taken
                // since. One Base never took (or reverted) can be submitted again; the contract mints a lock once.
                if (MintIsUnderWay(lockId))
                    return (false, "A mint of this lock is already under way. Check bridge status for updates.");
                if (record.Status is BridgeLockStatus.MintedOnBase or BridgeLockStatus.ProofSubmitted && !string.IsNullOrEmpty(record.BaseTxHash))
                {
                    var receipt = await MintReceiptAsync(record.BaseTxHash);
                    if (receipt == true)
                    {
                        BridgeLockRecord.UpdateStatus(lockId, BridgeLockStatus.Minted, baseTxHash: record.BaseTxHash, errorMessage: "");
                        return (false, "This lock has already been minted.");
                    }
                }
                // Whatever this node recorded, the contract knows whether it minted the lock.
                if (await MintedAfterAll(lockId, "Retry: "))
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
                return (false, $"Error: {ApiErrorText.For(ex)}");
            }
        }
    }
}
