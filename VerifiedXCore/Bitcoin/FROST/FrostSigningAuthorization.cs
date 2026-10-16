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

        internal const string NonCanonicalReference = "WithdrawalRequestHash must be written exactly as the chain records the payment";

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
                catch (Exception ex) { return (false, $"UnsignedTxHex unparseable: {ApiErrorText.For(ex)}"); }

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

                var (derivedTxId, derivedOutpoints) = DeriveTxBinding(tx);
                if (request.TxInputOutpoints != null && request.TxInputOutpoints.Count > 0)
                {
                    var announced = request.TxInputOutpoints.Select(o => (o ?? "").Trim().ToLowerInvariant()).ToList();
                    if (announced.Count != derivedOutpoints.Count || !announced.SequenceEqual(derivedOutpoints))
                        return (false, "TxInputOutpoints do not match the transaction");
                }

                // The contract-level outpoint pin (the non-expiring double-payout backstop) and the pin
                // reconciler key off these two fields. They are DERIVED from the transaction here so a
                // leader cannot disable the pin by simply omitting them.
                request.TxInputOutpoints = derivedOutpoints;
                request.BtcTxId = derivedTxId;

                // ── 2. Every input must spend the contract's own deposit address ───────────────
                // A vault the chain watches can have more than one key (plan 8.8): the session names the key it signs
                // with by that key's UID (the vault's own UID for its first key), every input must be held under that
                // key, and everything else below is judged for the vault the key belongs to.
                var watchedKey = Attestation.BtcVaultKeys.KeyRow(request.SmartContractUID);
                var vaultUid = watchedKey?.SmartContractUID ?? request.SmartContractUID;
                var depositAddress = watchedKey?.DepositAddress ?? ResolveDepositAddress(request.SmartContractUID);
                if (string.IsNullOrWhiteSpace(depositAddress)) return (false, "Contract deposit address could not be resolved");

                Script depositScript;
                try { depositScript = BitcoinAddress.Create(depositAddress, Globals.BTCNetwork).ScriptPubKey; }
                catch (Exception ex) { return (false, $"Contract deposit address invalid: {ApiErrorText.For(ex)}"); }

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
                bool leaderIsVaultSignerOfPinned = false;
                bool pinnedRelease = false;
                string? pinnedDestinationScriptHex = null; // a watched vault: the script state holds for the pinned payment

                // The tracker, the durable evidence, the session and the pins all key on this string as sent, so it
                // must be the one spelling the chain names the payment by: one payment, one key.
                var wrh = request.WithdrawalRequestHash;
                if (wrh != wrh.Trim()) return (false, NonCanonicalReference);
                if (wrh.StartsWith(BtcExitPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    isBridgeExit = true;
                    var (exit, capSats, exitReason) = ResolveBtcExitForContract(wrh, vaultUid);
                    if (exit == null) return (false, exitReason);
                    if (exit.IsComplete) return (false, "Bridge exit already completed");
                    if (!string.Equals(wrh, Attestation.BtcWithdrawals.ExitSigningReference(exit.BaseBurnTxHash, vaultUid), StringComparison.Ordinal))
                        return (false, NonCanonicalReference);
                    destination = exit.BtcDestination;
                    // Cap at THIS contract's share of the exit, never the whole exit amount.
                    maxDestinationSats = capSats;

                    // A vault the chain watches: only the transaction consensus pinned for this vault's part of the exit
                    // is signed, and any signer of the vault may lead (as for a withdrawal, below).
                    if (watchedKey != null)
                    {
                        var (pinned, pinReason) = Attestation.BtcWithdrawals.MaySignExit(exit, vaultUid, txId);
                        if (!pinned) return (false, pinReason);
                        pinnedDestinationScriptHex = Attestation.BtcWithdrawals.ExitDestinationScriptOf(exit, vaultUid);
                        leaderIsVaultSignerOfPinned = Attestation.BtcWithdrawalSigner.SignersOf(request.SmartContractUID)
                            .Contains(request.LeaderAddress, StringComparer.Ordinal);
                    }
                }
                else if (Attestation.BtcWithdrawals.IsSweepReference(wrh))
                {
                    // A sweep: a replaced key's coins moving to the vault's current key, fixed by rule. Any holder of the
                    // key being signed with may lead; the transaction is the pinned one and pays only the vault.
                    if (watchedKey == null) return (false, "A sweep is signed only for a vault the chain watches");
                    var (pinned, pinReason) = Attestation.BtcWithdrawals.MaySignSweep(wrh, vaultUid, txId);
                    if (!pinned) return (false, pinReason);
                    var sweep = Attestation.BtcAttestStore.Withdrawal(wrh)!;
                    if (!string.Equals(wrh, sweep.Ref, StringComparison.Ordinal)) return (false, NonCanonicalReference);
                    pinnedDestinationScriptHex = sweep.DestinationScriptHex;
                    destination = Attestation.BtcAttestStore.ScriptRow(sweep.DestinationScriptHex)?.DepositAddress ?? "";
                    maxDestinationSats = sweep.AmountSats;
                    leaderIsVaultSignerOfPinned = Attestation.BtcWithdrawalSigner.SignersOf(request.SmartContractUID)
                        .Contains(request.LeaderAddress, StringComparer.Ordinal);
                }
                else
                {
                    // Contract-scoped: a multi-contract withdrawal opens one row per vault under
                    // one REQUEST hash, and this ceremony signs exactly one vault's transaction.
                    // The hash alone would return an arbitrary sibling (wrong amount cap, and
                    // IsWithdrawalSignable would reject on the contract mismatch), and a
                    // cancellation filed against another vault's share would block this one.
                    if (WithdrawalNoLongerSignable(wrh, vaultUid) is { } notNow) return (false, notNow);
                    var wr = VBTCWithdrawalRequest.GetByTransactionHash(wrh, vaultUid);
                    var pendingCancellation = VBTCWithdrawalCancellation.HasPendingCancellation(
                        wrh, VerifiedXCore.Utilities.TimeUtil.GetTime(), vaultUid);
                    var (signable, signableReason) = IsWithdrawalSignable(wr, vaultUid, pendingCancellation);
                    if (!signable) return (false, signableReason);
                    if (!string.Equals(wrh, wr!.TransactionHash, StringComparison.Ordinal)) return (false, NonCanonicalReference);
                    destination = wr.BTCDestination;
                    maxDestinationSats = (long)(wr.Amount * 100_000_000M);
                    leaderIsOwner = string.Equals(wr.RequestorAddress, request.LeaderAddress, StringComparison.Ordinal);

                    // A vault the chain watches: consensus has fixed the one transaction that pays this withdrawal,
                    // before anyone signed. A share is given for that transaction and for no other, so "nothing is
                    // pinned" means "no signature exists", which is what expiry and cancellation rely on. And since
                    // no other transaction can be signed, any signer of the vault may lead the ceremony.
                    if (Attestation.BtcAttestBalance.IsAttestedVault(wr.SmartContractUID))
                    {
                        var (pinned, pinReason) = Attestation.BtcWithdrawals.MaySign(wrh, wr.SmartContractUID, txId);
                        if (!pinned) return (false, pinReason);
                        // The requester gave the withdrawal up: consensus fixed a transaction that returns its coins to
                        // the vault and pays nobody.
                        pinnedRelease = Attestation.BtcWithdrawals.IsPinnedRelease(wrh, wr.SmartContractUID, txId);
                        pinnedDestinationScriptHex = Attestation.BtcWithdrawals.DestinationScriptOf(wrh, wr.SmartContractUID);
                        leaderIsVaultSignerOfPinned = Attestation.BtcWithdrawalSigner.SignersOf(request.SmartContractUID)
                            .Contains(request.LeaderAddress, StringComparer.Ordinal);
                    }
                }

                // On a watched vault the destination is the script consensus built the pinned transaction with, so
                // this check and the transaction cannot disagree over how an address is read.
                Script destinationScript;
                try
                {
                    destinationScript = pinnedDestinationScriptHex != null
                        ? Script.FromHex(pinnedDestinationScriptHex)
                        : BitcoinAddress.Create(destination, Globals.BTCNetwork).ScriptPubKey;
                }
                catch (Exception ex) { return (false, $"Authorized destination invalid: {ApiErrorText.For(ex)}"); }

                // ── 4. Outputs: destination (bounded) or change to the vault, nothing else ─────
                //      AND the vault's total cost (destination + miner fee) must fit the authorization.
                // Back to the vault means to any of its keys on a watched vault: change of a payment fixed before a
                // re-key goes to the key it was fixed under, a release after it to the current one.
                var vaultScripts = watchedKey != null
                    ? Attestation.BtcAttestStore.ScriptRowsForVault(vaultUid).Select(k => Script.FromHex(k.ScriptHex)).ToList()
                    : new List<Script> { depositScript };
                long destinationSats = 0, changeSats = 0;
                foreach (var o in tx.Outputs)
                {
                    if (o.ScriptPubKey == destinationScript) destinationSats += o.Value.Satoshi;
                    else if (vaultScripts.Contains(o.ScriptPubKey)) changeSats += o.Value.Satoshi;
                    else return (false, "Transaction pays an output that is neither the authorized destination nor vault change");
                }
                long inputSats = 0;
                foreach (var so in spentOutputs) inputSats += so.Value.Satoshi;

                var (boundsOk, boundsReason) = CheckSpendBounds(inputSats, destinationSats, changeSats, maxDestinationSats, pinnedRelease);
                if (!boundsOk) return (false, boundsReason);

                // ── 5. Leader: the withdrawal owner for user withdrawals; a committee caster for exits ─
                var (leaderOk, leaderReason) = IsLeaderAllowed(isBridgeExit, leaderIsOwner || leaderIsVaultSignerOfPinned,
                    IsKnownCaster(request.LeaderAddress) || (isBridgeExit && leaderIsVaultSignerOfPinned));
                if (!leaderOk) return (false, leaderReason);

                return (true, "");
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"FrostSigningAuthorization error: {ex}", "FrostSigningAuthorization.Authorize");
                return (false, $"Authorization error: {ApiErrorText.For(ex)}");
            }
        }

        /// <summary>
        /// The transaction a withdrawal's signing record is kept under on this validator
        /// (<see cref="Services.FrostWithdrawalSigningTracker"/>). On a vault the chain does not watch there is none:
        /// the validator itself holds each withdrawal to one Bitcoin transaction. On a watched vault consensus fixes
        /// which transactions may be signed, and a fee bump pins a second one on the same coins; the signer keeps one
        /// record per pinned transaction, so the replacement can be signed and two ceremonies do not disturb each other.
        /// </summary>
        public static string? SigningScope(string? scUID, string? btcTxId) =>
            !string.IsNullOrEmpty(btcTxId) && Attestation.BtcVaultKeys.KeyRow(scUID) != null ? btcTxId.Trim().ToLowerInvariant() : null;

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
        /// Why this validator is not to give a share now for a vault the chain does not watch, or null. What it signs is
        /// judged from its own withdrawal store, cancellations and exits, which are only as current as its chain: after a
        /// snapshot restore, a resync, a fork recovery (its download phase too) or while far behind, a withdrawal refunded
        /// since reads open. So it signs only with its chain synced, not resyncing or recovering, and its tip recent. (Not "no block being downloaded": that is held every time a
        /// peer is a block ahead, and a share refused at Round 2 fails the whole ceremony. A few blocks behind, a
        /// cancellation being voted on is already seen, and refused.) A watched vault's shares are for transactions
        /// consensus pinned, which spend the same coins.
        /// </summary>
        public static string? NotAtNetworkHeightToSign(string? scUID)
        {
            if (string.IsNullOrEmpty(scUID) || Attestation.BtcVaultKeys.KeyRow(scUID) != null || Attestation.BtcAttestBalance.IsAttestedVault(scUID))
                return null;
            string? why = null;
            var tip = Globals.LastBlock;
            if (!Globals.IsChainSynced) why = "chain not synced";
            else if (Globals.IsResyncing) why = "node is resyncing";
            else if (VerifiedXCore.Utilities.ForkRecoveryUtility.IsRecoveryInProgress) why = "fork recovery in progress";
            else if (Globals.StopAllTimers) why = "timers stopped";
            else if (tip == null || tip.Height <= 0) why = "no tip block";
            else if (VerifiedXCore.Utilities.TimeUtil.GetTime() - tip.Timestamp > VBTCCancellationVoteService.MAX_TIP_AGE_SECONDS)
                why = $"tip block {tip.Height} is {VerifiedXCore.Utilities.TimeUtil.GetTime() - tip.Timestamp}s old";
            return why == null ? null : $"This validator is not at the network's height ({why}); signing resumes when it is";
        }

        /// <summary>
        /// Why a withdrawal (not a bridge exit or a sweep) is not to have a share given for it now, or null: the withdrawal
        /// store is being rebuilt (a row put back reads "requested" until the rebuild brings it to how its withdrawal
        /// ended), its cancellation was approved (it was refunded, whatever its row reads), or its row is closed. Asked
        /// when a ceremony starts and again just before Round 2 gives the share: a signing session lives longer than a
        /// cancellation vote waits for a ceremony to end.
        /// </summary>
        public static string? WithdrawalNoLongerSignable(string wrh, string vaultUid)
        {
            if (VBTCWithdrawalStoreRebuildService.IsRunning)
                return "The withdrawal store is being rebuilt; signing resumes when it finishes";
            if (VBTCWithdrawalCancellation.GetCancellationsFor(wrh, vaultUid).Any(c => c.IsProcessed && c.IsApproved))
                return "Withdrawal request was cancelled and refunded";
            var row = VBTCWithdrawalRequest.GetByTransactionHash(wrh, vaultUid);
            if (row != null && (row.IsCompleted || row.Status == VBTCWithdrawalStatus.Completed || row.Status == VBTCWithdrawalStatus.Cancelled))
                return IsWithdrawalSignable(row, vaultUid).Reason;
            return null;
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

        /// <param name="releaseToVault">The transaction is a release fixed by consensus for a watched vault: it pays
        /// nobody and returns everything but its fee to the vault. The fee is still held within the authorized amount.</param>
        public static (bool Ok, string Reason) CheckSpendBounds(long inputSats, long destinationSats, long changeSats, long maxSats, bool releaseToVault = false)
        {
            if (maxSats <= 0) return (false, "No authorized amount");
            if (inputSats <= 0) return (false, "Transaction has no input value");
            if (releaseToVault && destinationSats != 0) return (false, "A release pays nobody; this transaction pays the destination");
            if (!releaseToVault && destinationSats <= 0) return (false, "Transaction does not pay the authorized destination");
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
        public static bool VerifyLeaderHeaders(Microsoft.AspNetCore.Http.HttpContext context, SigningSession session, out string reason, string? round2Body = null)
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
            if (!string.IsNullOrEmpty(session.StartContentSignature))
            {
                // A watched vault's session: from any address, but Round 2 carries the leader's signature over its own body
                // (the nonce set it commits this signer's share to). Reading shares changes nothing.
                if (round2Body == null) return true;
                var bodySig = context.Request.Headers[FrostDkgLeaderAuth.SigningContentHeader].ToString();
                if (string.IsNullOrEmpty(bodySig) || !VerifiedXCore.Services.SignatureService.VerifySignature(leader, FrostDkgLeaderAuth.SigningRound2Message(session.SessionId, round2Body), bodySig))
                { reason = "This session's Round 2 is signed by its leader"; return false; }
                return true;
            }
            if (!SameLeaderEndpoint(session.LeaderRemoteIp, NormalizeRemoteIp(context.Connection.RemoteIpAddress)))
            { reason = "Round message did not come from the endpoint that started the session"; return false; }
            return true;
        }

        /// <summary>Canonical string form of a remote address (IPv4-mapped IPv6 collapsed); "" when unknown.</summary>
        public static string NormalizeRemoteIp(System.Net.IPAddress? ip)
        {
            if (ip == null) return "";
            try { return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(); }
            catch { return ip.ToString(); }
        }

        /// <summary>
        /// Round/abort messages must come from the endpoint that started the session. Exact match on
        /// the normalized address; two unknown endpoints (in-process test host) compare equal.
        /// </summary>
        public static bool SameLeaderEndpoint(string? recorded, string? current) =>
            string.Equals(recorded ?? "", current ?? "", StringComparison.Ordinal);

        public static string? ResolveDepositAddressForContract(string scUID) => ResolveDepositAddress(scUID);

        private static string? ResolveDepositAddress(string scUID)
        {
            try
            {
                // A later key of a watched vault (plan 8.8) is no contract: its address is recorded with it.
                var key = Attestation.BtcVaultKeys.KeyRow(scUID);
                if (key != null && key.Generation > 0 && string.Equals(key.KeyUID, scUID, StringComparison.Ordinal))
                    return key.DepositAddress;

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

        /// <summary>Txid and lowercase "txid:vout" outpoints of every input, in input order.</summary>
        public static (string TxId, List<string> Outpoints) DeriveTxBinding(NBitcoin.Transaction tx)
        {
            var outpoints = tx.Inputs.Select(x => $"{x.PrevOut.Hash}:{x.PrevOut.N}".ToLowerInvariant()).ToList();
            return (tx.GetHash().ToString().ToLowerInvariant(), outpoints);
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
        /// How many sats of an exit are drawn from <paramref name="scUID"/>, from the allocation plan
        /// recorded at apply time. Returns 0 when the contract is not part of the exit or the record
        /// has no plan (pre-upgrade record; refused).
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

            // Records written before the allocation plan was recorded cannot be bound to a contract:
            // the lock list only says WHICH locks, not how much of each this exit drew, so any cap
            // derived from it over-pays a partially drawn contract. Refuse (fail closed). Operators:
            // such an in-flight exit is recovered through the stuck-exit FAIL + re-exit path, which
            // writes a record with a plan.
            return 0;
        }


        internal static bool IsKnownCaster(string address)
        {
            if (Globals.BootstrapCasterAddresses.Contains(address)) return true;
            if (Globals.BlockCasters.Any(p => string.Equals(p.ValidatorAddress, address, StringComparison.Ordinal))) return true;
            lock (Globals.KnownCastersLock) // the lock the list is rebuilt under (round 25)
            {
                return Globals.KnownCasters.Any(c => string.Equals(c.Address, address, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Whether a signing ceremony on a key the chain watches is led by neither a holder of that key nor a caster: a
        /// withdrawal's requester, whose transaction the holders sign without it. An older vault's withdrawal has its owner
        /// as its only leader, and is not counted here: in the requester's share four addresses could keep every older
        /// vault's owner out until those vaults freeze (fifth review, round 25).
        /// </summary>
        internal static bool LedByRequester(string? leaderAddress, string? keyUid) =>
            !string.IsNullOrEmpty(leaderAddress) && keyUid != null && Attestation.BtcVaultKeys.KeyRow(keyUid) != null
            && !(keyUid != null && Attestation.BtcWithdrawalSigner.SignersOf(keyUid).Contains(leaderAddress, StringComparer.Ordinal))
            && !IsKnownCaster(leaderAddress);
    }
}
