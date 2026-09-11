using NBitcoin;
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

                var wrh = request.WithdrawalRequestHash.Trim();
                if (wrh.StartsWith(BtcExitPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var exit = ResolveBtcExit(wrh, request.SmartContractUID);
                    if (exit == null) return (false, "No on-chain EXIT_TO_BTC record matches this bridge exit signing");
                    if (exit.IsComplete) return (false, "Bridge exit already completed");
                    destination = exit.BtcDestination;
                    maxDestinationSats = exit.AmountSats > 0 ? exit.AmountSats : (long)(exit.Amount * 100_000_000M);
                }
                else
                {
                    var wr = VBTCWithdrawalRequest.GetByTransactionHash(wrh);
                    if (wr == null) return (false, "Withdrawal request not found in consensus state");
                    if (!string.Equals(wr.SmartContractUID, request.SmartContractUID, StringComparison.Ordinal))
                        return (false, "Withdrawal request belongs to a different contract");
                    if (wr.IsCompleted || wr.Status == VBTCWithdrawalStatus.Completed) return (false, "Withdrawal request already completed");
                    if (wr.Status == VBTCWithdrawalStatus.Cancelled) return (false, "Withdrawal request was cancelled");
                    if (string.IsNullOrWhiteSpace(wr.BTCDestination) || wr.Amount <= 0) return (false, "Withdrawal request has no destination/amount");
                    destination = wr.BTCDestination;
                    maxDestinationSats = (long)(wr.Amount * 100_000_000M);
                    leaderIsOwner = string.Equals(wr.RequestorAddress, request.LeaderAddress, StringComparison.Ordinal);
                }

                Script destinationScript;
                try { destinationScript = BitcoinAddress.Create(destination, Globals.BTCNetwork).ScriptPubKey; }
                catch (Exception ex) { return (false, $"Authorized destination invalid: {ex.Message}"); }

                // ── 4. Outputs: destination (bounded) or change to the vault, nothing else ─────
                long destinationSats = 0;
                foreach (var o in tx.Outputs)
                {
                    if (o.ScriptPubKey == destinationScript) destinationSats += o.Value.Satoshi;
                    else if (o.ScriptPubKey == depositScript) continue;
                    else return (false, "Transaction pays an output that is neither the authorized destination nor vault change");
                }
                if (destinationSats <= 0) return (false, "Transaction does not pay the authorized destination");
                if (destinationSats > maxDestinationSats) return (false, "Transaction pays more than the authorized amount");

                // ── 5. Leader must be the owner, a registered validator, or a known caster ────
                if (!leaderIsOwner && !IsRegisteredValidator(request.LeaderAddress) && !IsKnownCaster(request.LeaderAddress))
                    return (false, "Leader is not the withdrawal owner, a registered validator, or a known caster");

                return (true, "");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FrostSigningAuthorization error: {ex}", "FrostSigningAuthorization.Authorize");
                return (false, $"Authorization error: {ex.Message}");
            }
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
        /// Bridge exit signings use a synthetic hash "btcexit_{burnHash[..16]}_{scUID[..8]}"
        /// (see BurnExitConsensusService.ExecuteBtcExit). Match it to the consensus-recorded exit.
        /// </summary>
        private static VBTCBridgeBtcExitState? ResolveBtcExit(string syntheticHash, string scUID)
        {
            var parts = syntheticHash.Substring(BtcExitPrefix.Length).Split('_');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) return null;
            var burnPrefix = parts[0];
            var scPrefix = parts.Length > 1 ? parts[1] : "";
            if (!string.IsNullOrEmpty(scPrefix) && !scUID.StartsWith(scPrefix, StringComparison.Ordinal)) return null;

            var candidates = VBTCBridgeBtcExitState.FindByBurnHashPrefix(burnPrefix);
            if (candidates.Count != 1) return null; // ambiguous or missing — refuse
            return candidates[0];
        }

        private static bool IsRegisteredValidator(string address)
        {
            try
            {
                return VBTCValidatorRegistry.GetActiveValidators()
                    .Any(v => string.Equals(v.ValidatorAddress, address, StringComparison.Ordinal));
            }
            catch { return false; }
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
