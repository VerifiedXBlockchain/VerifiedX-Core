using NBitcoin;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.FROST
{
    /// <summary>
    /// Validator-side authorization for a FROST signing ceremony start.
    ///
    /// A validator must never contribute a signature share over a sighash it cannot tie to a
    /// legitimate, consensus-recorded spend. This class rebuilds the BIP341 sighash from the
    /// unsigned transaction and prevouts the coordinator supplies, and checks that:
    ///   1. the supplied MessageHash / AllInputSighashes are exactly what that transaction hashes to;
    ///   2. every input spends the contract's own Taproot deposit address;
    ///   3. every output pays either the authorized destination (bounded by the authorized amount)
    ///      or change back to the deposit address — nothing else;
    ///   4. the authorized destination/amount come from a consensus record: the VFX withdrawal
    ///      request (user withdrawals) or the VFX EXIT_TO_BTC record (bridge exits);
    ///   5. the leader is the withdrawal owner, a registered vBTC validator, or a known caster.
    ///
    /// Prevout values/scripts are self-enforcing: if the coordinator lies about them, the
    /// resulting signature is invalid for the real UTXO and the transaction can never confirm.
    /// </summary>
    public static class FrostSigningAuthorization
    {
        public const string BtcExitPrefix = "btcexit_";

        public static (bool Ok, string Reason) Authorize(FrostSigningStartRequest request)
        {
            try
            {
                if (request == null) return (false, "Empty request");
                if (string.IsNullOrWhiteSpace(request.SmartContractUID)) return (false, "SmartContractUID required");
                if (string.IsNullOrWhiteSpace(request.WithdrawalRequestHash)) return (false, "WithdrawalRequestHash required");
                if (string.IsNullOrWhiteSpace(request.MessageHash)) return (false, "MessageHash required");
                if (string.IsNullOrWhiteSpace(request.UnsignedTxHex)) return (false, "UnsignedTxHex required");
                if (request.Prevouts == null || request.Prevouts.Count == 0) return (false, "Prevouts required");
                if (request.AllInputSighashes == null || request.AllInputSighashes.Count == 0) return (false, "AllInputSighashes required");
                if (string.IsNullOrWhiteSpace(request.LeaderAddress)) return (false, "LeaderAddress required");

                // ── 1. Parse the transaction and bind the announced sighashes to it ────────────
                NBitcoin.Transaction tx;
                try { tx = NBitcoin.Transaction.Parse(request.UnsignedTxHex, Globals.BTCNetwork); }
                catch (Exception ex) { return (false, $"UnsignedTxHex unparseable: {ex.Message}"); }

                var inputCount = tx.Inputs.Count;
                if (inputCount == 0 || tx.Outputs.Count == 0) return (false, "Transaction must have inputs and outputs");
                if (request.Prevouts.Count != inputCount) return (false, "Prevouts count does not match transaction inputs");
                if (request.AllInputSighashes.Count != inputCount) return (false, "AllInputSighashes count does not match transaction inputs");
                if (request.InputCount > 0 && request.InputCount != inputCount) return (false, "InputCount does not match transaction inputs");
                if (request.InputIndex < 0 || request.InputIndex >= inputCount) return (false, "InputIndex out of range");

                var spentOutputs = new TxOut[inputCount];
                for (int i = 0; i < inputCount; i++)
                {
                    var p = request.Prevouts[i];
                    if (p == null || string.IsNullOrWhiteSpace(p.TxId) || string.IsNullOrWhiteSpace(p.ScriptPubKeyHex) || p.ValueSats == 0)
                        return (false, $"Prevout {i} incomplete");

                    var inp = tx.Inputs[i].PrevOut;
                    if (!string.Equals(inp.Hash.ToString(), p.TxId, StringComparison.OrdinalIgnoreCase) || inp.N != p.Vout)
                        return (false, $"Prevout {i} does not match transaction input outpoint");

                    spentOutputs[i] = new TxOut(Money.Satoshis((long)p.ValueSats), Script.FromHex(p.ScriptPubKeyHex));
                }

                var precomputed = tx.PrecomputeTransactionData(spentOutputs);
                for (int i = 0; i < inputCount; i++)
                {
                    var execData = new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default };
                    var sighash = Convert.ToHexString(tx.GetSignatureHashTaproot(precomputed, execData).ToBytes()).ToLowerInvariant();
                    if (!string.Equals(sighash, request.AllInputSighashes[i]?.Trim(), StringComparison.OrdinalIgnoreCase))
                        return (false, $"AllInputSighashes[{i}] does not match the transaction");
                }

                if (!string.Equals(request.AllInputSighashes[request.InputIndex], request.MessageHash.Trim(), StringComparison.OrdinalIgnoreCase))
                    return (false, "MessageHash does not match the sighash of the requested input");

                var txId = tx.GetHash().ToString();
                if (!string.IsNullOrWhiteSpace(request.BtcTxId) && !string.Equals(request.BtcTxId.Trim(), txId, StringComparison.OrdinalIgnoreCase))
                    return (false, "BtcTxId does not match the transaction");

                if (request.TxInputOutpoints != null && request.TxInputOutpoints.Count > 0)
                {
                    var actual = tx.Inputs.Select(x => $"{x.PrevOut.Hash}:{x.PrevOut.N}".ToLowerInvariant()).ToList();
                    var announced = request.TxInputOutpoints.Select(o => (o ?? "").Trim().ToLowerInvariant()).ToList();
                    if (announced.Count != actual.Count || !announced.SequenceEqual(actual))
                        return (false, "TxInputOutpoints do not match the transaction");
                }

                // ── 2. Every input must spend the contract's own deposit address ───────────────
                var depositAddress = ResolveDepositAddress(request.SmartContractUID);
                if (string.IsNullOrWhiteSpace(depositAddress)) return (false, "Contract deposit address could not be resolved");

                Script depositScript;
                try { depositScript = BitcoinAddress.Create(depositAddress, Globals.BTCNetwork).ScriptPubKey; }
                catch (Exception ex) { return (false, $"Contract deposit address invalid: {ex.Message}"); }

                for (int i = 0; i < inputCount; i++)
                {
                    if (spentOutputs[i].ScriptPubKey != depositScript)
                        return (false, $"Input {i} does not spend the contract deposit address");
                }

                // ── 3. Resolve the authorized spend intent from consensus state ────────────────
                string destination;
                long maxDestinationSats;
                bool leaderIsOwner = false;
                bool isBridgeExit = false;

                var wrh = request.WithdrawalRequestHash.Trim();
                if (wrh.StartsWith(BtcExitPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    isBridgeExit = true;
                    var (exit, capSats, exitReason) = ResolveBtcExitForContract(wrh, request.SmartContractUID);
                    if (exit == null) return (false, exitReason);
                    if (exit.IsComplete) return (false, "Bridge exit already completed");
                    destination = exit.BtcDestination;
                    // Cap at THIS contract's share of the exit, never the whole exit amount.
                    maxDestinationSats = capSats;
                }
                else
                {
                    var wr = VBTCWithdrawalRequest.GetByTransactionHash(wrh);
                    var pendingCancellation = VBTCWithdrawalCancellation.HasPendingCancellation(wrh);
                    var (signable, signableReason) = IsWithdrawalSignable(wr, request.SmartContractUID, pendingCancellation);
                    if (!signable) return (false, signableReason);
                    destination = wr!.BTCDestination;
                    maxDestinationSats = (long)(wr.Amount * 100_000_000M);
                    leaderIsOwner = string.Equals(wr.RequestorAddress, request.LeaderAddress, StringComparison.Ordinal);
                }

                Script destinationScript;
                try { destinationScript = BitcoinAddress.Create(destination, Globals.BTCNetwork).ScriptPubKey; }
                catch (Exception ex) { return (false, $"Authorized destination invalid: {ex.Message}"); }

                // ── 4. Outputs: destination (bounded) or change to the vault, nothing else ─────
                //      AND the vault's total cost (destination + miner fee) must fit the authorization.
                long destinationSats = 0, changeSats = 0;
                foreach (var o in tx.Outputs)
                {
                    if (o.ScriptPubKey == destinationScript) destinationSats += o.Value.Satoshi;
                    else if (o.ScriptPubKey == depositScript) changeSats += o.Value.Satoshi;
                    else return (false, "Transaction pays an output that is neither the authorized destination nor vault change");
                }
                long inputSats = 0;
                foreach (var so in spentOutputs) inputSats += so.Value.Satoshi;

                var (boundsOk, boundsReason) = CheckSpendBounds(inputSats, destinationSats, changeSats, maxDestinationSats);
                if (!boundsOk) return (false, boundsReason);

                // ── 5. Leader: the withdrawal owner for user withdrawals; a committee caster for exits ─
                var (leaderOk, leaderReason) = IsLeaderAllowed(isBridgeExit, leaderIsOwner, IsKnownCaster(request.LeaderAddress));
                if (!leaderOk) return (false, leaderReason);

                return (true, "");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FrostSigningAuthorization error: {ex}", "FrostSigningAuthorization.Authorize");
                return (false, $"Authorization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Who may lead a ceremony. A USER withdrawal is led only by its owner: every legitimate
        /// coordinator path sets the requester as leader, and letting any registered validator lead
        /// would let them sign-and-withhold a competing transaction, pinning the contract (FIND-028)
        /// and stalling the owner's withdrawal indefinitely. A BRIDGE EXIT is led by the handler
        /// caster, so the leader must be a committee caster.
        /// </summary>
        public static (bool Ok, string Reason) IsLeaderAllowed(bool isBridgeExit, bool leaderIsOwner, bool leaderIsCommitteeCaster)
        {
            if (isBridgeExit)
                return leaderIsCommitteeCaster ? (true, "") : (false, "Bridge exit signing must be led by a committee caster");
            return leaderIsOwner ? (true, "") : (false, "Withdrawal signing must be led by the withdrawal owner");
        }

        /// <summary>
        /// A withdrawal may be signed only while it is plainly open: not completed, not cancelled,
        /// and with NO cancellation vote in progress. Signing during a pending cancellation is the
        /// "sign, cancel, then broadcast" double-pay: vBTC is only burned at completion, so a held
        /// signed transaction plus an approved cancellation pays the user twice.
        /// </summary>
        public static (bool Ok, string Reason) IsWithdrawalSignable(VBTCWithdrawalRequest? wr, string scUID, bool hasPendingCancellation = false)
        {
            if (wr == null) return (false, "Withdrawal request not found in consensus state");
            if (!string.Equals(wr.SmartContractUID, scUID, StringComparison.Ordinal))
                return (false, "Withdrawal request belongs to a different contract");
            if (wr.IsCompleted || wr.Status == VBTCWithdrawalStatus.Completed) return (false, "Withdrawal request already completed");
            if (wr.Status == VBTCWithdrawalStatus.Cancelled) return (false, "Withdrawal request was cancelled");
            // The cancel tx marks Cancellation_Requested on the owner's LOCAL contract record, not on
            // this consensus row, so the row status alone is not enough — the consensus cancellation
            // record (present on every node) is the authoritative signal.
            if (hasPendingCancellation || wr.Status == VBTCWithdrawalStatus.Cancellation_Requested)
                return (false, "Withdrawal request has a cancellation vote in progress; refusing to sign");
            if (string.IsNullOrWhiteSpace(wr.BTCDestination) || wr.Amount <= 0) return (false, "Withdrawal request has no destination/amount");
            return (true, "");
        }

        /// <summary>
        /// The vault's total cost for a transaction is inputs minus change back to the vault, which
        /// equals destination + miner fee. That WHOLE cost must fit inside the authorized amount.
        /// Checking only the destination output would let a tiny authorized payout burn the entire
        /// vault as fee (inputs = whole vault, 1-sat payout, no change).
        /// </summary>
        /// <summary>
        /// Bitcoin dust limit for a P2TR change output. The transaction builder folds sub-dust change
        /// into the fee, so a legitimate withdrawal whose vault UTXOs exceed the amount by less than
        /// dust has vault cost = amount + that remainder. Allow exactly that much slack and no more.
        /// </summary>
        public const long DUST_TOLERANCE_SATS = 546;

        public static (bool Ok, string Reason) CheckSpendBounds(long inputSats, long destinationSats, long changeSats, long maxSats)
        {
            if (maxSats <= 0) return (false, "No authorized amount");
            if (inputSats <= 0) return (false, "Transaction has no input value");
            if (destinationSats <= 0) return (false, "Transaction does not pay the authorized destination");
            if (changeSats < 0) return (false, "Negative change");

            var fee = inputSats - destinationSats - changeSats;
            if (fee < 0) return (false, "Transaction outputs exceed inputs");
            if (destinationSats > maxSats) return (false, "Transaction pays more than the authorized amount");

            var vaultCost = inputSats - changeSats; // destination + fee
            if (vaultCost > maxSats + DUST_TOLERANCE_SATS)
                return (false, $"Transaction spends {vaultCost} sats from the vault (destination {destinationSats} + fee {fee}) but only {maxSats} sats are authorized");

            return (true, "");
        }

        /// <summary>
        /// Verifies a round message carries the session leader's replayed start signature.
        /// Reads X-Frost-Leader / X-Frost-Timestamp / X-Frost-Signature headers.
        /// </summary>
        public static bool VerifyLeaderHeaders(Microsoft.AspNetCore.Http.HttpContext context, SigningSession session, out string reason)
        {
            reason = "";
            var leader = context.Request.Headers["X-Frost-Leader"].ToString();
            var tsRaw = context.Request.Headers["X-Frost-Timestamp"].ToString();
            var sig = context.Request.Headers["X-Frost-Signature"].ToString();

            if (string.IsNullOrWhiteSpace(leader) || string.IsNullOrWhiteSpace(tsRaw) || string.IsNullOrWhiteSpace(sig))
            { reason = "Leader auth headers required"; return false; }
            if (!long.TryParse(tsRaw, out var ts)) { reason = "Invalid X-Frost-Timestamp"; return false; }
            if (!string.Equals(leader, session.LeaderAddress, StringComparison.Ordinal)) { reason = "Leader mismatch for session"; return false; }
            if (ts != session.LeaderStartTimestamp) { reason = "Timestamp does not match session start"; return false; }

            var msg = $"{session.SessionId}.{leader}.{ts}";
            if (!VerifiedXCore.Services.SignatureService.VerifySignature(leader, msg, sig)) { reason = "Invalid leader signature"; return false; }
            return true;
        }

        public static string? ResolveDepositAddressForContract(string scUID) => ResolveDepositAddress(scUID);

        private static string? ResolveDepositAddress(string scUID)
        {
            try
            {
                var local = VBTCContractV2.GetContract(scUID);
                if (local != null && !string.IsNullOrWhiteSpace(local.DepositAddress))
                    return local.DepositAddress;

                var st = SmartContractStateTrei.GetSmartContractState(scUID);
                if (st == null || string.IsNullOrEmpty(st.ContractData)) return null;
                var sc = SmartContractMain.GenerateSmartContractInMemory(st.ContractData);
                var feature = sc?.Features?
                    .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                    .Select(x => x.FeatureFeatures)
                    .FirstOrDefault();
                return feature is TokenizationV2Feature t ? t.DepositAddress : null;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"ResolveDepositAddress({scUID}) failed: {ex.Message}", "FrostSigningAuthorization");
                return null;
            }
        }

        /// <summary>
        /// Bridge exit signings use a synthetic reference "btcexit_{burnHash[..16]}_{scUID[..8]}"
        /// (see BurnExitConsensusService.ExecuteBtcExit). Resolve it to the consensus-recorded exit
        /// AND bind it to the declared contract: the contract must be one the exit actually draws
        /// on, and the signing is capped at that contract's own allocation. Without this, a leader
        /// could point contract B's validators at contract A's pending exit and drain B's vault to
        /// the exit destination.
        /// </summary>
        public static (VBTCBridgeBtcExitState? Exit, long CapSats, string Reason) ResolveBtcExitForContract(string syntheticHash, string scUID)
        {
            if (string.IsNullOrWhiteSpace(scUID)) return (null, 0, "SmartContractUID required for bridge exit signing");
            if (string.IsNullOrWhiteSpace(syntheticHash) || !syntheticHash.StartsWith(BtcExitPrefix, StringComparison.OrdinalIgnoreCase))
                return (null, 0, "Not a bridge exit reference");

            var parts = syntheticHash.Substring(BtcExitPrefix.Length).Split('_');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                return (null, 0, "Bridge exit reference must be btcexit_{burnPrefix}_{contractPrefix}");

            var burnPrefix = parts[0];
            var scPrefix = parts[1];
            if (!scUID.StartsWith(scPrefix, StringComparison.Ordinal))
                return (null, 0, "Bridge exit reference contract prefix does not match the declared contract");

            var candidates = VBTCBridgeBtcExitState.FindByBurnHashPrefix(burnPrefix);
            if (candidates.Count == 0) return (null, 0, "No on-chain EXIT_TO_BTC record matches this bridge exit signing");
            if (candidates.Count > 1) return (null, 0, "Ambiguous bridge exit reference (multiple exits share the burn prefix)");

            var exit = candidates[0];
            var cap = ContractAllocationSats(exit, scUID);
            if (cap <= 0) return (null, 0, $"EXIT_TO_BTC record does not allocate anything from contract {scUID}");
            return (exit, cap, "");
        }

        /// <summary>
        /// How many sats of an exit are drawn from <paramref name="scUID"/>. Uses the allocation plan
        /// recorded at apply time; for records written before that field existed, falls back to the
        /// lock list (bounded by the locks' amounts for the contract and by the exit total).
        /// Returns 0 when the contract is not part of the exit.
        /// </summary>
        public static long ContractAllocationSats(VBTCBridgeBtcExitState exit, string scUID)
        {
            if (exit == null || string.IsNullOrWhiteSpace(scUID)) return 0;
            var exitSats = exit.AmountSats > 0 ? exit.AmountSats : (long)(exit.Amount * 100_000_000M);

            if (!string.IsNullOrWhiteSpace(exit.AllocationsJson))
            {
                List<PoolUnlockAllocation>? allocs;
                try { allocs = JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(exit.AllocationsJson); }
                catch { return 0; }
                if (allocs == null) return 0;

                decimal sum = 0M;
                foreach (var a in allocs)
                {
                    if (a == null || a.UnlockAmount <= 0M) continue;
                    var allocContract = a.SmartContractUID;
                    if (string.IsNullOrEmpty(allocContract) && !string.IsNullOrEmpty(a.LockId))
                        allocContract = VBTCBridgeLockState.GetByLockId(a.LockId)?.SmartContractUID ?? "";
                    if (string.Equals(allocContract, scUID, StringComparison.Ordinal))
                        sum += a.UnlockAmount;
                }
                var sats = (long)(sum * 100_000_000M);
                return Math.Min(sats, exitSats);
            }

            // Legacy record: derive from the lock list.
            var lockIds = (exit.LockId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
            decimal lockSum = 0M;
            var any = false;
            foreach (var id in lockIds)
            {
                var rec = VBTCBridgeLockState.GetByLockId(id);
                if (rec == null) continue;
                if (string.Equals(rec.SmartContractUID, scUID, StringComparison.Ordinal))
                {
                    any = true;
                    lockSum += rec.Amount;
                }
            }
            if (!any) return 0;
            return Math.Min((long)(lockSum * 100_000_000M), exitSats);
        }


        private static bool IsKnownCaster(string address)
        {
            if (Globals.BootstrapCasterAddresses.Contains(address)) return true;
            if (Globals.BlockCasters.Any(p => string.Equals(p.ValidatorAddress, address, StringComparison.Ordinal))) return true;
            lock (Globals.KnownCasters)
            {
                return Globals.KnownCasters.Any(c => string.Equals(c.Address, address, StringComparison.Ordinal));
            }
        }
    }
}
