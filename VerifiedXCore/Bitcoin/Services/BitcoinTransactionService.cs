using NBitcoin;
using NBitcoin.Crypto;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using VerifiedXCore.Bitcoin.Integrations;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Service for creating and signing Bitcoin transactions using FROST threshold signatures
    /// </summary>
    public class BitcoinTransactionService
    {
        public static decimal BTCMultiplier = 100_000_000M;
        public static decimal SatoshiMultiplier = 0.00000001M;

        /// <summary>
        /// Maximum number of DISTINCT Electrum servers to query for UTXOs before falling back to Esplora.
        /// </summary>
        private const int MaxElectrumServersForUTXOQuery = 3;

        /// <summary>
        /// Stable marker embedded in error messages for UTXO lookups that failed because no Bitcoin
        /// data source could be reached (as opposed to a corroborated-empty address). Callers that
        /// take punitive action on failure (e.g. contract blacklisting) must skip it when this marker
        /// is present — the condition is transient and retryable.
        /// </summary>
        public const string TransientUtxoErrorMarker = "[TRANSIENT-UTXO]";

        /// <summary>
        /// Gets an available Electrum client. Delegates to ClientService.GetElectrumClient, which
        /// handshakes (server version), bumps Count on success and FailCount on failure, and rotates
        /// through the pool — unlike the old private selector here, which pinned the same server
        /// forever because it never incremented either counter.
        /// </summary>
        private static async Task<Client?> GetElectrumClient()
        {
            return await ClientService.GetElectrumClient();
        }

        /// <summary>
        /// Returns the selectable Electrum servers (FailCount under the threshold) ordered by least-used
        /// first — same selection rule as GetElectrumClient, but as a full list so callers can fan out
        /// across DISTINCT servers instead of always hitting the single least-used one.
        /// If every server has exceeded the FailCount threshold, the pool is reset wholesale
        /// (mirroring Bitcoin.ElectrumXRun) so a long outage cannot permanently exhaust it.
        /// </summary>
        private static List<ClientSettings> GetElectrumServerCandidates()
        {
            var candidates = Globals.ClientSettings?.Where(x => x.FailCount < 10).OrderBy(x => x.Count).ToList()
                ?? new List<ClientSettings>();

            if (!candidates.Any() && Globals.ClientSettings?.Any() == true)
            {
                foreach (var server in Globals.ClientSettings)
                    server.FailCount = 0;

                candidates = Globals.ClientSettings.Where(x => x.FailCount < 10).OrderBy(x => x.Count).ToList();
            }

            return candidates;
        }

        /// <summary>
        /// Maps an Esplora (mempool.space / blockstream.info) UTXO into the ElectrumX result shape the
        /// transaction builder consumes. The builder re-fetches the raw tx by TxHash to source the
        /// scriptPubKey, so only TxHash / TxPos / Value are required to build the spending Coin; Height
        /// is carried for parity with Electrum listunspent (0 == unconfirmed / mempool).
        /// </summary>
        public static BlockchainScripthashListunspentResult MapEsploraUtxo(string txid, int vout, long value, bool confirmed, int blockHeight)
        {
            return new BlockchainScripthashListunspentResult
            {
                TxHash = txid,
                TxPos = (uint)vout,
                Value = (ulong)value,
                Height = confirmed ? blockHeight : 0
            };
        }

        /// <summary>
        /// Verdict on an "every Electrum server answered empty" outcome.
        /// </summary>
        public enum EmptyCorroborationVerdict
        {
            /// <summary>The empty answer is trustworthy — the address genuinely has no UTXOs.</summary>
            ConfirmedEmpty,
            /// <summary>The empty-answering Electrum servers are behind (a cross-check shows funds) — penalize them and have the caller retry.</summary>
            PenalizeAndRetry,
            /// <summary>Not enough agreeing answers to trust "empty" — treat the lookup as failed/transient.</summary>
            Inconclusive
        }

        /// <summary>
        /// Decides whether an all-empty Electrum answer can be trusted. ElectrumX is the system of
        /// record; the Esplora cross-check is ADVISORY ONLY — it is never a spend source and is never
        /// required:
        /// - Esplora answered and shows funds      -> the empty Electrum servers are behind: penalize
        ///                                            them and retry (rotation picks healthier servers).
        /// - 2+ Electrum servers agreed empty      -> confirmed empty by Electrum quorum (Esplora
        ///                                            silence does not block the conclusion).
        /// - 1 Electrum empty + Esplora agrees     -> confirmed empty.
        /// - anything less                          -> inconclusive; caller must treat as transient.
        /// Pure function (no I/O) so the corroboration logic is unit-testable.
        /// </summary>
        public static EmptyCorroborationVerdict DecideEmptyVerdict(int electrumEmptyAnswers, bool esploraAnswered, bool esploraHasUtxos)
        {
            if (esploraAnswered && esploraHasUtxos)
                return EmptyCorroborationVerdict.PenalizeAndRetry;

            if (electrumEmptyAnswers >= 2)
                return EmptyCorroborationVerdict.ConfirmedEmpty;

            if (electrumEmptyAnswers >= 1 && esploraAnswered)
                return EmptyCorroborationVerdict.ConfirmedEmpty;

            return EmptyCorroborationVerdict.Inconclusive;
        }

        /// <summary>
        /// Cross-checks an address against the Esplora integrations (mempool.space first, then
        /// blockstream.info) and returns the first non-empty UTXO set, plus whether ANY provider
        /// answered at all (an answered-empty is meaningful; an unreachable provider is not).
        /// Both honor mainnet/testnet via their GetBaseURL().
        /// </summary>
        private static async Task<(List<BlockchainScripthashListunspentResult> Utxos, bool Answered)> GetUTXOsFromEsplora(string address)
        {
            bool answered = false;

            try
            {
                var mempoolUtxos = await MempoolSpace.GetAddressUTXOList(address);
                answered = true;
                if (mempoolUtxos != null && mempoolUtxos.Any())
                    return (mempoolUtxos, true);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Esplora (mempool.space) UTXO cross-check failed: {ex.Message}", "BitcoinTransactionService.GetUTXOsFromEsplora()");
            }

            try
            {
                var blockstreamUtxos = await Blockstream.GetAddressUTXOList(address);
                answered = true;
                if (blockstreamUtxos != null && blockstreamUtxos.Any())
                    return (blockstreamUtxos, true);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Esplora (blockstream.info) UTXO cross-check failed: {ex.Message}", "BitcoinTransactionService.GetUTXOsFromEsplora()");
            }

            return (new List<BlockchainScripthashListunspentResult>(), answered);
        }

        /// <summary>
        /// Fetches UTXOs for a Taproot address resiliently, with ElectrumX as the sole system of record.
        ///
        /// The old implementation asked ONE Electrum server once, with no health tracking — a server
        /// that was down, slow, or behind produced an empty answer indistinguishable from an empty
        /// address ("No UTXOs found" on a funded vault). Now:
        ///   1. Query up to MaxElectrumServersForUTXOQuery DISTINCT Electrum servers (least-used first,
        ///      Count/FailCount tracked like every other Electrum consumer); return on the first hit.
        ///   2. If no server returns UTXOs, decide whether "empty" can be trusted via DecideEmptyVerdict:
        ///      an Electrum quorum (2+ servers answering empty) confirms it; a single empty answer needs
        ///      the ADVISORY Esplora cross-check to agree. Esplora is never a spend source and never
        ///      required — if it is unreachable, an Electrum quorum still decides.
        ///   3. If the cross-check shows the address IS funded, the empty-answering servers are behind:
        ///      bump their FailCount so rotation skips them, and return a transient failure so the
        ///      caller retries against healthier servers.
        ///   4. Anything inconclusive is Success=false — a transient, retryable state, never "empty".
        /// </summary>
        public static async Task<UtxoLookupResult> GetTaprootUTXOs(string taprootAddress)
        {
            string scriptHash;
            try
            {
                var bitcoinAddress = BitcoinAddress.Create(taprootAddress, Globals.BTCNetwork);
                scriptHash = Client.GetScriptHash(bitcoinAddress);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Invalid Taproot address '{taprootAddress}': {ex.Message}", "BitcoinTransactionService.GetTaprootUTXOs()");
                return UtxoLookupResult.Failed($"Invalid Taproot address: {ex.Message}", 0);
            }

            var candidates = GetElectrumServerCandidates();
            if (!candidates.Any())
                ErrorLogUtility.LogError("No Electrum server available", "BitcoinTransactionService.GetTaprootUTXOs()");

            // Step 1: query distinct Electrum servers, returning on the first non-empty result.
            var emptyServers = new List<ClientSettings>();
            int serversTried = 0;

            foreach (var server in candidates)
            {
                if (serversTried >= MaxElectrumServersForUTXOQuery)
                    break;
                serversTried++;
                server.Count++;

                try
                {
                    using (var client = new Client(server.Host, server.Port, true))
                    {
                        var utxos = await client.GetListUnspent(scriptHash);
                        if (utxos != null && utxos.Any())
                            return UtxoLookupResult.Found(utxos.ToList(), UtxoSource.Electrum, serversTried);

                        // Empty answer — remember this server so we can penalize it if funds turn out to exist.
                        emptyServers.Add(server);
                    }
                }
                catch (Exception ex)
                {
                    server.FailCount++;
                    ErrorLogUtility.LogError($"Electrum UTXO query failed on {server.Host}:{server.Port}: {ex.Message}", "BitcoinTransactionService.GetTaprootUTXOs()");
                }
            }

            // Step 2: no Electrum server produced UTXOs. If nothing even answered, that is a plain
            // transient failure — no cross-check needed, just retry later against rotated servers.
            if (!emptyServers.Any())
            {
                var noAnswerDetail = $"no Electrum server answered ({serversTried} tried)";
                ErrorLogUtility.LogError($"UTXO lookup failed for {taprootAddress}: {noAnswerDetail}", "BitcoinTransactionService.GetTaprootUTXOs()");
                return UtxoLookupResult.Failed(noAnswerDetail, serversTried);
            }

            // Step 3: at least one Electrum server answered "empty" — decide whether to trust it.
            // The Esplora cross-check is advisory only (single lightweight GET, never a spend source);
            // an Electrum quorum can confirm empty without it.
            var (esploraUtxos, esploraAnswered) = await GetUTXOsFromEsplora(taprootAddress);
            var verdict = DecideEmptyVerdict(emptyServers.Count, esploraAnswered, esploraUtxos.Any());

            switch (verdict)
            {
                case EmptyCorroborationVerdict.PenalizeAndRetry:
                    // Empty-while-funded: these servers are behind/unindexed. Bump FailCount so
                    // rotation skips them, then have the caller retry — the next attempt selects
                    // healthier Electrum servers. We do NOT spend from Esplora data.
                    foreach (var server in emptyServers)
                        server.FailCount++;

                    LogUtility.Log(
                        $"[UTXO] {emptyServers.Count} Electrum server(s) returned empty for {taprootAddress} but a cross-check shows " +
                        $"{esploraUtxos.Count} UTXO(s) exist; penalized those servers — retry will rotate to healthier ones.",
                        "BitcoinTransactionService.GetTaprootUTXOs()");

                    return UtxoLookupResult.Failed(
                        $"{emptyServers.Count} Electrum server(s) answered empty for a funded address (lagging/unindexed); servers penalized, retry", serversTried);

                case EmptyCorroborationVerdict.ConfirmedEmpty:
                    return UtxoLookupResult.ConfirmedEmpty(serversTried);

                default:
                    var detail = $"only {emptyServers.Count} Electrum server(s) answered empty and no second source could corroborate";
                    ErrorLogUtility.LogError($"UTXO lookup inconclusive for {taprootAddress}: {detail}", "BitcoinTransactionService.GetTaprootUTXOs()");
                    return UtxoLookupResult.Failed(detail, serversTried);
            }
        }

        /// <summary>
        /// FIND-028 determinism: full ordering for coin selection — value DESC, then txid, then vout.
        /// Electrum servers return listunspent in server-specific order (and the server rotates per
        /// call), so without a complete tiebreaker equal-value UTXOs make the selection — and thus
        /// every input sighash — non-reproducible across retries, tripping the validators'
        /// per-withdrawal sighash pin.
        /// </summary>
        public static List<BlockchainScripthashListunspentResult> SortUtxosDeterministic(IEnumerable<BlockchainScripthashListunspentResult> utxos)
        {
            return utxos
                .OrderByDescending(u => u.Value)
                .ThenBy(u => u.TxHash, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.TxPos)
                .ToList();
        }

        /// <summary>
        /// Builds an unsigned Taproot transaction.
        /// <paramref name="preferredOutpoints"/> (lowercase "txid:vout"): outpoints to select FIRST,
        /// used when a previously signed-but-unresolved tx may exist for the contract — spending at
        /// least one of its outpoints makes the new tx CONFLICT with it, which is what the
        /// validators' contract pin requires to sign a different withdrawal (and what guarantees at
        /// most one of the two ever confirms).
        /// </summary>
        public static async Task<(bool Success, NBitcoin.Transaction? UnsignedTx, ulong Fee, List<Coin> SpentCoins, List<BlockchainScripthashListunspentResult> UsedUtxos, string ErrorMessage)>
            BuildUnsignedTaprootTransaction(
                string taprootAddress,
                string destinationAddress,
                decimal amountBTC,
                long feeRateSatsPerVByte,
                HashSet<string>? preferredOutpoints = null)
        {
            try
            {
                // Validate addresses
                BitcoinAddress fromAddress;
                BitcoinAddress toAddress;
                
                try
                {
                    fromAddress = BitcoinAddress.Create(taprootAddress, Globals.BTCNetwork);
                    toAddress = BitcoinAddress.Create(destinationAddress, Globals.BTCNetwork);
                }
                catch (Exception ex)
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(), $"Invalid address: {ex.Message}");
                }

                // Get UTXOs
                var utxoLookup = await GetTaprootUTXOs(taprootAddress);
                if (!utxoLookup.Success)
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(),
                        $"UTXO lookup failed for Taproot address: {utxoLookup.Error}. Bitcoin data sources unreachable — try again. {TransientUtxoErrorMarker}");
                }

                var utxos = utxoLookup.Utxos;
                if (!utxos.Any())
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(), "No UTXOs found for Taproot address (confirmed empty by multiple sources)");
                }

                ulong amountToSend = Convert.ToUInt64(amountBTC * BTCMultiplier);
                ulong feeEstimate = 0;
                bool sufficientInputsFound = false;
                List<Coin> unspentCoins = new List<Coin>();
                List<BlockchainScripthashListunspentResult> selectedUtxos = new List<BlockchainScripthashListunspentResult>();
                ulong previousTotalInputAmount = 0;

                // One handshaked client for the whole selection pass — the old code opened a fresh
                // TCP+TLS connection per UTXO.
                using var rawTxClient = await GetElectrumClient();
                if (rawTxClient == null)
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(),
                        $"No Electrum server available to fetch raw transactions. {TransientUtxoErrorMarker}");
                }

                // Select UTXOs to cover the withdrawal amount.
                // The fee is deducted FROM the withdrawal amount (user receives amount - fee).
                while (!sufficientInputsFound)
                {
                    unspentCoins.Clear();
                    selectedUtxos.Clear();
                    ulong totalInputAmount = 0;

                    // Deterministic order (FIND-028) — largest first for efficient selection, full
                    // tiebreaker so retries reproduce the same selection. Preferred outpoints (a
                    // prior unresolved signed tx's inputs) float to the front; OrderByDescending is
                    // stable, so the deterministic order is preserved within each group.
                    var sortedUtxos = SortUtxosDeterministic(utxos);
                    if (preferredOutpoints is { Count: > 0 })
                        sortedUtxos = sortedUtxos
                            .OrderByDescending(u => preferredOutpoints.Contains($"{u.TxHash}:{u.TxPos}".ToLowerInvariant()))
                            .ToList();

                    foreach (var utxo in sortedUtxos)
                    {
                        totalInputAmount += utxo.Value;

                        // Fetch the raw transaction to get the output
                        var rawTx = await rawTxClient.GetRawTx(utxo.TxHash);

                        if (rawTx?.RawTx != null)
                        {
                            var tx = NBitcoin.Transaction.Parse(rawTx.RawTx, Globals.BTCNetwork);
                            var output = tx.Outputs[utxo.TxPos];

                            OutPoint outPoint = new OutPoint(uint256.Parse(utxo.TxHash), utxo.TxPos);
                            Coin coin = new Coin(outPoint, output);

                            unspentCoins.Add(coin);
                            selectedUtxos.Add(utxo);
                        }

                        // Check if we have enough
                        if (totalInputAmount >= previousTotalInputAmount)
                        {
                            int inputCount = unspentCoins.Count;
                            int outputCount = 2; // destination + change

                            // Estimate transaction size for Taproot (smaller than legacy/segwit)
                            // Taproot: ~57.5 vBytes per input, ~43 vBytes per output + 10.5 overhead
                            int estimatedSize = (int)(57.5 * inputCount + 43 * outputCount + 10.5);
                            feeEstimate = (ulong)(estimatedSize * feeRateSatsPerVByte);

                            // Fee is deducted from withdrawal amount, so UTXOs only need to cover the amount
                            ulong totalRequired = amountToSend;

                            if (totalInputAmount >= totalRequired)
                            {
                                sufficientInputsFound = true;
                                break;
                            }
                        }

                        previousTotalInputAmount = totalInputAmount;
                    }

                    if (!sufficientInputsFound && totalInputAmount == previousTotalInputAmount)
                    {
                        return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(), 
                            $"Insufficient funds. Required: {amountToSend * SatoshiMultiplier:F8} BTC, Available: {totalInputAmount * SatoshiMultiplier:F8} BTC");
                    }
                }

                // Safety check: fee must not exceed withdrawal amount
                if (feeEstimate >= amountToSend)
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(),
                        $"Fee ({feeEstimate * SatoshiMultiplier:F8} BTC) exceeds or equals withdrawal amount ({amountToSend * SatoshiMultiplier:F8} BTC). Increase withdrawal amount or decrease fee rate.");
                }

                // Fee is deducted from the withdrawal amount: user receives (amount - fee)
                ulong userReceives = amountToSend - feeEstimate;

                // Build the unsigned transaction
                var txBuilder = Globals.BTCNetwork.CreateTransactionBuilder();
                
                txBuilder.AddCoins(unspentCoins.ToArray());
                txBuilder.Send(toAddress, new Money(userReceives, MoneyUnit.Satoshi));
                txBuilder.SetChange(fromAddress);
                txBuilder.SendFees(new Money(feeEstimate, MoneyUnit.Satoshi));

                var unsignedTx = txBuilder.BuildTransaction(false);

                return (true, unsignedTx, feeEstimate, unspentCoins, selectedUtxos, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error building unsigned Taproot transaction: {ex}", "BitcoinTransactionService.BuildUnsignedTaprootTransaction()");
                return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(), $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Signs a transaction using FROST threshold signatures via validator coordination.
        /// FIND-020 Fix: Computes BIP341 Taproot sighash per input (not txid) and runs
        /// a separate FROST signing ceremony for each input, since each input's sighash
        /// commits to the input index and differs per-input.
        /// </summary>
        public static async Task<(bool Success, string SignedTxHex, string TxHash, string ErrorMessage, FrostCeremonyOutcome? Ceremony)>
            SignTransactionWithFROST(
                NBitcoin.Transaction unsignedTx,
                List<Coin> spentCoins,
                string scUID,
                List<VBTCValidator> validators,
                int threshold,
                string? coordinatorAddress = null,
                string? withdrawalRequestHash = null,
                PreSignedLeaderAuth? preSignedAuth = null)
        {
            try
            {
                // Get the contract to retrieve FROST public key — try local DB first, fall back to State Trei
                var contract = VBTCContractV2.GetContract(scUID);
                string frostGroupPublicKey = contract?.FrostGroupPublicKey;
                string? ceremonyId = null;

                // Always resolve ceremonyId from State Trei — VBTCContractV2 doesn't store it,
                // and validators need it to look up their FROST key packages (stored under the DKG ceremonyId).
                try
                {
                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                    if (scStateTreiRec != null && !string.IsNullOrEmpty(scStateTreiRec.ContractData))
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
                                ceremonyId = tknz.CeremonyId;

                                // Also fill in FrostGroupPublicKey if local DB didn't have it
                                if (string.IsNullOrEmpty(frostGroupPublicKey))
                                    frostGroupPublicKey = tknz.FrostGroupPublicKey;

                                LogUtility.Log($"[FROST] Resolved from State Trei: FrostGroupPublicKey={frostGroupPublicKey}, CeremonyId={ceremonyId ?? "null"}",
                                    "BitcoinTransactionService.SignTransactionWithFROST()");
                            }
                        }
                    }
                }
                catch (Exception decompileEx)
                {
                    LogUtility.Log($"[FROST] State Trei decompile warning (non-blocking): {decompileEx.Message}",
                        "BitcoinTransactionService.SignTransactionWithFROST()");
                }

                if (string.IsNullOrEmpty(frostGroupPublicKey))
                {
                    return (false, string.Empty, string.Empty, "FROST group public key not found in contract or State Trei", null);
                }

                // FIND-020 Fix: Build the spent outputs array in input order for BIP341 sighash.
                // BIP341 requires ALL prevouts to compute the sighash (prevouts commitment).
                var spentOutputs = new TxOut[unsignedTx.Inputs.Count];
                for (int i = 0; i < unsignedTx.Inputs.Count; i++)
                {
                    var prevOut = unsignedTx.Inputs[i].PrevOut;
                    var matchingCoin = spentCoins.FirstOrDefault(c => c.Outpoint == prevOut);
                    if (matchingCoin == null)
                    {
                        return (false, string.Empty, string.Empty,
                            $"Missing prevout data for input {i} (txid: {prevOut.Hash}, vout: {prevOut.N}). Cannot compute BIP341 sighash.", null);
                    }
                    spentOutputs[i] = matchingCoin.TxOut;
                }

                // Precompute shared transaction data for BIP341 sighash (prevouts hash, amounts hash, etc.)
                var precomputedData = unsignedTx.PrecomputeTransactionData(spentOutputs);

                // Precompute the whole transaction's sighash set, input outpoints and txid. The set is
                // announced to validators on every sign/start so they can pin it (a fake "input k"
                // carrying another transaction's sighash gets refused), and the outpoints drive the
                // per-contract conflict rule against parallel double-payout transactions. For Taproot
                // the unsigned txid equals the broadcast txid (witness data is excluded from the txid).
                var allSighashes = new List<string>();
                for (int i = 0; i < unsignedTx.Inputs.Count; i++)
                {
                    var preExecData = new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default };
                    uint256 preSighash = unsignedTx.GetSignatureHashTaproot(precomputedData, preExecData);
                    allSighashes.Add(Convert.ToHexString(preSighash.ToBytes()).ToLowerInvariant());
                }

                var txContext = new FrostSigningTxContext
                {
                    InputCount = unsignedTx.Inputs.Count,
                    AllInputSighashes = allSighashes,
                    TxInputOutpoints = unsignedTx.Inputs.Select(inp => $"{inp.PrevOut.Hash}:{inp.PrevOut.N}".ToLowerInvariant()).ToList(),
                    BtcTxId = unsignedTx.GetHash().ToString()
                };

                // FIND-020 Fix: Sign each input individually with its own BIP341 sighash.
                // Each input has a unique sighash because the input index is part of the BIP341 preimage.
                for (int i = 0; i < unsignedTx.Inputs.Count; i++)
                {
                    // Compute BIP341 signature hash for this specific input (key-path spend, SIGHASH_DEFAULT)
                    var execData = new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default };
                    uint256 sighash = unsignedTx.GetSignatureHashTaproot(precomputedData, execData);
                    // Convert sighash to hex for FROST. ToBytes() returns the raw SHA-256 output bytes
                    // in their natural order (same as Bitcoin Core's internal representation).
                    string sighashHex = allSighashes[i];

                    LogUtility.Log($"[FROST] Computing BIP341 sighash for input {i}/{unsignedTx.Inputs.Count}: {sighashHex}",
                        "BitcoinTransactionService.SignTransactionWithFROST()");

                    // Multi-input withdrawals run one ceremony per input, each with its own session id.
                    // A pre-signed auth (web wallet flow) must therefore carry one signed start message
                    // per input — reusing input 0's session for input 1 collides on every validator.
                    var inputAuth = preSignedAuth?.ForInput(i);
                    if (preSignedAuth != null && inputAuth == null)
                    {
                        var mismatch = FrostCeremonyOutcome.Fail(FrostCeremonyFailureCode.InputCountMismatch,
                            preSignedAuth.SessionId ?? string.Empty,
                            $"pre-signed auth covers fewer inputs than the transaction has ({unsignedTx.Inputs.Count}); call PrepareCompleteWithdrawalRaw again to sign one start message per input",
                            inputIndex: i);
                        return (false, string.Empty, string.Empty, $"FROST signing failed for input {i}: {mismatch.Describe()}", mismatch);
                    }

                    // Coordinate FROST signing ceremony for this input's sighash
                    var ceremonyOutcome = await FrostMPCService.CoordinateSigningCeremony(
                        sighashHex,
                        scUID,
                        validators,
                        threshold,
                        ceremonyId,
                        coordinatorAddress,
                        withdrawalRequestHash,
                        inputAuth,
                        inputIndex: i,
                        txContext: txContext);

                    if (!ceremonyOutcome.Success)
                    {
                        // Keep the exact legacy prefix ("FROST signing failed for input {i}") so
                        // explorer-side string matching survives; everything after the colon is the
                        // new diagnostic detail.
                        return (false, string.Empty, string.Empty, $"FROST signing failed for input {i}: {ceremonyOutcome.Describe()}", ceremonyOutcome);
                    }

                    var signingResult = ceremonyOutcome.Result!;

                    // The aggregate signature from FROST is a 64-byte Schnorr signature
                    var aggregateSignatureBytes = Convert.FromHexString(signingResult.SchnorrSignature);

                    if (aggregateSignatureBytes.Length != 64)
                    {
                        return (false, string.Empty, string.Empty,
                            $"Invalid Schnorr signature length for input {i}: expected 64 bytes, got {aggregateSignatureBytes.Length}", ceremonyOutcome);
                    }

                    // Pre-broadcast validation (non-blocking): The FROST native library already
                    // verifies the signature internally during frost::aggregate. This local check
                    // uses NBitcoin's VerifySignature which may extract uint256 bytes differently
                    // than the big-endian order used by FROST and Bitcoin Core. Log the result
                    // but don't block the broadcast — the Bitcoin network is the authoritative verifier.
                    try
                    {
                        var groupPubKeyBytes = Convert.FromHexString(frostGroupPublicKey);
                        if (groupPubKeyBytes.Length == 33 && (groupPubKeyBytes[0] == 0x02 || groupPubKeyBytes[0] == 0x03))
                        {
                            groupPubKeyBytes = groupPubKeyBytes[1..];
                        }

                        var taprootPubKey = new TaprootPubKey(groupPubKeyBytes);
                        var schnorrSig = new SchnorrSignature(aggregateSignatureBytes);

                        bool localVerifyResult = taprootPubKey.VerifySignature(sighash, schnorrSig);
                        LogUtility.Log(
                            $"[FROST] Pre-broadcast local verify for input {i}: {(localVerifyResult ? "PASS" : "SKIPPED (NBitcoin uint256 byte order may differ)")}. " +
                            $"Sighash: {sighashHex}, GroupPubKey: {frostGroupPublicKey}",
                            "BitcoinTransactionService.SignTransactionWithFROST()");
                    }
                    catch (Exception verifyEx)
                    {
                        LogUtility.Log($"[FROST] Pre-broadcast verify exception for input {i} (non-blocking): {verifyEx.Message}",
                            "BitcoinTransactionService.SignTransactionWithFROST()");
                    }

                    // Create Taproot key-path witness: just the 64-byte Schnorr signature (SIGHASH_DEFAULT omits the byte)
                    var witness = new WitScript(Op.GetPushOp(aggregateSignatureBytes));
                    unsignedTx.Inputs[i].WitScript = witness;
                }

                string signedTxHex = unsignedTx.ToHex();
                string txHash = unsignedTx.GetHash().ToString();

                LogUtility.Log($"[FROST] All {unsignedTx.Inputs.Count} inputs signed and verified successfully. TxHash: {txHash}",
                    "BitcoinTransactionService.SignTransactionWithFROST()");

                return (true, signedTxHex, txHash, string.Empty, null);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error signing transaction with FROST: {ex}", "BitcoinTransactionService.SignTransactionWithFROST()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Broadcasts a signed Bitcoin transaction to the network
        /// </summary>
        public static async Task<(bool Success, string TxHash, string ErrorMessage)> BroadcastTransaction(NBitcoin.Transaction signedTx)
        {
            try
            {
                string txHex = signedTx.ToHex();
                string txHash = signedTx.GetHash().ToString();

                LogUtility.Log($"[FROST] Bitcoin transaction broadcast SUCCESS HEX: {txHex}",
                            "BitcoinTransactionService.BroadcastTransaction()");

                var client = await GetElectrumClient();
                if (client == null)
                {
                    return (false, string.Empty, "No Electrum server available");
                }

                using (client)
                {
                    var broadcastResult = await client.Broadcast(txHex);

                    if (!string.IsNullOrEmpty(broadcastResult?.TxHash))
                    {
                        LogUtility.Log($"[FROST] Bitcoin transaction broadcast SUCCESS: {broadcastResult.TxHash}",
                            "BitcoinTransactionService.BroadcastTransaction()");
                        return (true, broadcastResult.TxHash, string.Empty);
                    }
                    else
                    {
                        var errorDetail = !string.IsNullOrEmpty(broadcastResult?.ErrorMessage)
                            ? $"Electrum rejected: {broadcastResult.ErrorMessage}"
                            : "Broadcast failed - no result returned (empty response or connection issue)";
                        ErrorLogUtility.LogError($"Bitcoin broadcast failed. TxHex length: {txHex.Length}, Error: {errorDetail}",
                            "BitcoinTransactionService.BroadcastTransaction()");
                        return (false, string.Empty, errorDetail);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error broadcasting transaction: {ex}", "BitcoinTransactionService.BroadcastTransaction()");
                return (false, string.Empty, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitors Bitcoin transaction confirmations
        /// </summary>
        public static async Task<int> GetTransactionConfirmations(string txHash)
        {
            try
            {
                var client = await GetElectrumClient();
                if (client == null)
                    return 0;

                using (client)
                {
                    var confirmations = await client.GetConfirms(txHash);
                    return confirmations;
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error getting transaction confirmations: {ex}", "BitcoinTransactionService.GetTransactionConfirmations()");
                return 0;
            }
        }

        /// <summary>
        /// Complete workflow: Build, sign with FROST, and broadcast a Bitcoin withdrawal transaction.
        /// FIND-028 retry determinism: when <paramref name="pinnedUnsignedTxHex"/>/<paramref name="pinnedCoins"/>
        /// are supplied (a previous attempt already announced a tx to the validators), that exact tx
        /// is re-signed instead of rebuilding — validators allow idempotent same-sighash re-signs, so
        /// retries always succeed where a rebuild is a coin flip. On a fresh build,
        /// <paramref name="persistPinnedBuild"/> is invoked BEFORE the first announce so the pin
        /// exists durably before any validator sees the tx.
        /// </summary>
        public static async Task<(bool Success, string TxHash, string SignedTxHex, string ErrorMessage, FrostCeremonyOutcome? Ceremony)>
            ExecuteFROSTWithdrawal(
                string taprootAddress,
                string destinationAddress,
                decimal amountBTC,
                long feeRateSatsPerVByte,
                string scUID,
                List<VBTCValidator> validators,
                int threshold,
                bool broadcast = true,
                string? coordinatorAddress = null,
                string? withdrawalRequestHash = null,
                PreSignedLeaderAuth? preSignedAuth = null,
                string? pinnedUnsignedTxHex = null,
                List<PinnedWithdrawalCoin>? pinnedCoins = null,
                Action<string, List<PinnedWithdrawalCoin>>? persistPinnedBuild = null,
                HashSet<string>? preferredOutpoints = null)
        {
            try
            {
                NBitcoin.Transaction unsignedTx;
                List<Coin> spentCoins;

                if (!string.IsNullOrEmpty(pinnedUnsignedTxHex) && pinnedCoins is { Count: > 0 })
                {
                    // Step 1 (retry): reuse the exact tx first announced for this withdrawal.
                    unsignedTx = NBitcoin.Transaction.Parse(pinnedUnsignedTxHex, Globals.BTCNetwork);
                    spentCoins = pinnedCoins.Select(c => c.ToCoin()).ToList();
                    LogUtility.Log($"[FROST] Reusing pinned unsigned tx for withdrawal {withdrawalRequestHash} ({spentCoins.Count} input(s)) — FIND-028 retry determinism.",
                        "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                }
                else
                {
                    // Step 1: Build unsigned transaction
                    var buildResult = await BuildUnsignedTaprootTransaction(
                        taprootAddress,
                        destinationAddress,
                        amountBTC,
                        feeRateSatsPerVByte,
                        preferredOutpoints);

                    if (!buildResult.Success || buildResult.UnsignedTx == null)
                    {
                        return (false, string.Empty, string.Empty, $"Failed to build transaction: {buildResult.ErrorMessage}", null);
                    }

                    unsignedTx = buildResult.UnsignedTx;
                    spentCoins = buildResult.SpentCoins;

                    // Pin the build BEFORE the first announce — once validators pin the sighash set,
                    // only this exact tx can ever complete this withdrawal.
                    if (persistPinnedBuild != null)
                    {
                        try
                        {
                            persistPinnedBuild(unsignedTx.ToHex(), spentCoins.Select(PinnedWithdrawalCoin.FromCoin).ToList());
                        }
                        catch (Exception pex)
                        {
                            ErrorLogUtility.LogError($"Failed to persist pinned build for withdrawal {withdrawalRequestHash}: {pex.Message}",
                                "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                        }
                    }
                }

                // Step 2: Sign with FROST (FIND-020: pass spent coins for BIP341 sighash computation)
                var signingResult = await SignTransactionWithFROST(
                    unsignedTx,
                    spentCoins,
                    scUID,
                    validators,
                    threshold,
                    coordinatorAddress,
                    withdrawalRequestHash,
                    preSignedAuth);

                if (!signingResult.Success)
                {
                    return (false, string.Empty, string.Empty, $"Failed to sign transaction: {signingResult.ErrorMessage}", signingResult.Ceremony);
                }

                // Step 3: Optionally broadcast signed transaction to Bitcoin network
                if (!broadcast)
                {
                    // signOnly mode: return the signed TX hex without broadcasting
                    LogUtility.Log($"[FROST] Sign-only mode: returning signed TX hex without broadcasting. TxHash: {signingResult.TxHash}",
                        "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                    return (true, signingResult.TxHash, signingResult.SignedTxHex, string.Empty, null);
                }

                // Parse the FROST-signed tx hex back into a Transaction object for broadcast
                var btcNetwork = Globals.BTCNetwork;
                var signedTx = NBitcoin.Transaction.Parse(signingResult.SignedTxHex, btcNetwork);
                var broadcastResult = await BroadcastTransaction(signedTx);

                if (!broadcastResult.Success)
                {
                    // Broadcast failed but we have the signed hex — return it so caller can retry
                    return (false, string.Empty, signingResult.SignedTxHex, $"Failed to broadcast transaction: {broadcastResult.ErrorMessage}", null);
                }

                return (true, broadcastResult.TxHash, signingResult.SignedTxHex, string.Empty, null);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error executing FROST withdrawal: {ex}", "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}", null);
            }
        }
    }
}
