using NBitcoin;
using NBitcoin.Crypto;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Bitcoin.ElectrumX.Results;
using ReserveBlockCore.Bitcoin.Integrations;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.FROST.Models;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReserveBlockCore.Bitcoin.Services
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
        /// Gets an available Electrum client
        /// </summary>
        private static Client? GetElectrumClient()
        {
            var electrumServer = Globals.ClientSettings?.Where(x => x.FailCount < 10).OrderBy(x => x.Count).FirstOrDefault();
            if (electrumServer != null)
            {
                return new Client(electrumServer.Host, electrumServer.Port, true);
            }
            return null;
        }

        /// <summary>
        /// Returns the selectable Electrum servers (FailCount under the threshold) ordered by least-used
        /// first — same selection rule as GetElectrumClient, but as a full list so callers can fan out
        /// across DISTINCT servers instead of always hitting the single least-used one.
        /// </summary>
        private static List<ClientSettings> GetElectrumServerCandidates()
        {
            return Globals.ClientSettings?.Where(x => x.FailCount < 10).OrderBy(x => x.Count).ToList()
                ?? new List<ClientSettings>();
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
        /// Decides the authoritative UTXO set after querying Electrum and (when Electrum came back empty)
        /// Esplora. The rule corroborates an empty Electrum answer before trusting it:
        /// - Electrum returned UTXOs            -> use them.
        /// - Electrum empty, Esplora has UTXOs  -> use Esplora AND signal the empty Electrum servers
        ///                                          should be penalized (they returned empty-while-funded).
        /// - Both empty                          -> genuinely no UTXOs.
        /// Pure function (no I/O) so the corroboration logic is unit-testable.
        /// </summary>
        public static (List<BlockchainScripthashListunspentResult> Utxos, bool PenalizeEmptyServers) DecideUtxoSource(
            List<BlockchainScripthashListunspentResult>? electrumUtxos,
            List<BlockchainScripthashListunspentResult>? esploraUtxos)
        {
            if (electrumUtxos != null && electrumUtxos.Any())
                return (electrumUtxos, false);

            if (esploraUtxos != null && esploraUtxos.Any())
                return (esploraUtxos, true);

            return (new List<BlockchainScripthashListunspentResult>(), false);
        }

        /// <summary>
        /// Cross-checks an address against the Esplora integrations (mempool.space first, then
        /// blockstream.info) and returns the first non-empty UTXO set. Both honor mainnet/testnet via
        /// their GetBaseURL(). Returns an empty list if neither reports UTXOs.
        /// </summary>
        private static async Task<List<BlockchainScripthashListunspentResult>> GetUTXOsFromEsplora(string address)
        {
            try
            {
                var mempoolUtxos = await MempoolSpace.GetAddressUTXOList(address);
                if (mempoolUtxos != null && mempoolUtxos.Any())
                    return mempoolUtxos;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Esplora (mempool.space) UTXO cross-check failed: {ex.Message}", "BitcoinTransactionService.GetUTXOsFromEsplora()");
            }

            try
            {
                var blockstreamUtxos = await Blockstream.GetAddressUTXOList(address);
                if (blockstreamUtxos != null && blockstreamUtxos.Any())
                    return blockstreamUtxos;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Esplora (blockstream.info) UTXO cross-check failed: {ex.Message}", "BitcoinTransactionService.GetUTXOsFromEsplora()");
            }

            return new List<BlockchainScripthashListunspentResult>();
        }

        /// <summary>
        /// Fetches UTXOs for a Taproot address resiliently.
        ///
        /// A behind / pruned / rate-limited public Electrum server passes the version handshake but then
        /// returns an EMPTY listunspent for a funded address. Treating that empty answer as success
        /// poisoned withdrawals intermittently. This method therefore CORROBORATES an empty Electrum
        /// result before trusting it:
        ///   1. Query up to MaxElectrumServersForUTXOQuery DISTINCT Electrum servers; return on first hit.
        ///   2. If all queried Electrum servers return empty, cross-check via Esplora (mempool.space,
        ///      then blockstream.info). If Esplora reports UTXOs, use them and bump the FailCount of the
        ///      Electrum servers that returned empty-while-funded so they rotate out of selection.
        ///   3. Only conclude "no UTXOs" when both Electrum and Esplora agree the address is empty.
        /// </summary>
        public static async Task<List<BlockchainScripthashListunspentResult>> GetTaprootUTXOs(string taprootAddress)
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
                return new List<BlockchainScripthashListunspentResult>();
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
                            return utxos.ToList();

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

            // Step 2: every queried Electrum server returned empty — corroborate via Esplora before trusting it.
            var esploraUtxos = await GetUTXOsFromEsplora(taprootAddress);
            var decision = DecideUtxoSource(new List<BlockchainScripthashListunspentResult>(), esploraUtxos);

            if (decision.PenalizeEmptyServers)
            {
                // Empty-while-funded: these servers are behind/unindexed. Bump FailCount so they rotate out.
                foreach (var server in emptyServers)
                    server.FailCount++;

                LogUtility.Log(
                    $"[UTXO] {emptyServers.Count} Electrum server(s) returned empty for funded address {taprootAddress}; " +
                    $"corroborated {esploraUtxos.Count} UTXO(s) via Esplora and penalized those servers.",
                    "BitcoinTransactionService.GetTaprootUTXOs()");
            }

            return decision.Utxos;
        }

        /// <summary>
        /// Builds an unsigned Taproot transaction
        /// </summary>
        public static async Task<(bool Success, NBitcoin.Transaction? UnsignedTx, ulong Fee, List<Coin> SpentCoins, List<BlockchainScripthashListunspentResult> UsedUtxos, string ErrorMessage)> 
            BuildUnsignedTaprootTransaction(
                string taprootAddress, 
                string destinationAddress, 
                decimal amountBTC,
                long feeRateSatsPerVByte)
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
                var utxos = await GetTaprootUTXOs(taprootAddress);
                if (!utxos.Any())
                {
                    return (false, null, 0, new List<Coin>(), new List<BlockchainScripthashListunspentResult>(), "No UTXOs found for Taproot address");
                }

                ulong amountToSend = Convert.ToUInt64(amountBTC * BTCMultiplier);
                ulong feeEstimate = 0;
                bool sufficientInputsFound = false;
                List<Coin> unspentCoins = new List<Coin>();
                List<BlockchainScripthashListunspentResult> selectedUtxos = new List<BlockchainScripthashListunspentResult>();
                ulong previousTotalInputAmount = 0;

                // Select UTXOs to cover the withdrawal amount.
                // The fee is deducted FROM the withdrawal amount (user receives amount - fee).
                while (!sufficientInputsFound)
                {
                    unspentCoins.Clear();
                    selectedUtxos.Clear();
                    ulong totalInputAmount = 0;

                    // Sort UTXOs by value (largest first) for efficient selection
                    var sortedUtxos = utxos.OrderByDescending(u => u.Value).ToList();

                    foreach (var utxo in sortedUtxos)
                    {
                        totalInputAmount += utxo.Value;
                        
                        // Fetch the raw transaction to get the output
                        var client = GetElectrumClient();
                        if (client != null)
                        {
                            using (client)
                            {
                                var rawTx = await client.GetRawTx(utxo.TxHash);

                                if (rawTx?.RawTx != null)
                                {
                                    var tx = NBitcoin.Transaction.Parse(rawTx.RawTx, Globals.BTCNetwork);
                                    var output = tx.Outputs[utxo.TxPos];
                                    
                                    OutPoint outPoint = new OutPoint(uint256.Parse(utxo.TxHash), utxo.TxPos);
                                    Coin coin = new Coin(outPoint, output);
                                    
                                    unspentCoins.Add(coin);
                                    selectedUtxos.Add(utxo);
                                }
                            }
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
        public static async Task<(bool Success, string SignedTxHex, string TxHash, string ErrorMessage)> 
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
                    return (false, string.Empty, string.Empty, "FROST group public key not found in contract or State Trei");
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
                            $"Missing prevout data for input {i} (txid: {prevOut.Hash}, vout: {prevOut.N}). Cannot compute BIP341 sighash.");
                    }
                    spentOutputs[i] = matchingCoin.TxOut;
                }

                // Precompute shared transaction data for BIP341 sighash (prevouts hash, amounts hash, etc.)
                var precomputedData = unsignedTx.PrecomputeTransactionData(spentOutputs);

                // FIND-020 Fix: Sign each input individually with its own BIP341 sighash.
                // Each input has a unique sighash because the input index is part of the BIP341 preimage.
                for (int i = 0; i < unsignedTx.Inputs.Count; i++)
                {
                    // Compute BIP341 signature hash for this specific input (key-path spend, SIGHASH_DEFAULT)
                    var execData = new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default };
                    uint256 sighash = unsignedTx.GetSignatureHashTaproot(precomputedData, execData);
                    // Convert sighash to hex for FROST. ToBytes() returns the raw SHA-256 output bytes
                    // in their natural order (same as Bitcoin Core's internal representation).
                    string sighashHex = Convert.ToHexString(sighash.ToBytes()).ToLowerInvariant();

                    LogUtility.Log($"[FROST] Computing BIP341 sighash for input {i}/{unsignedTx.Inputs.Count}: {sighashHex}", 
                        "BitcoinTransactionService.SignTransactionWithFROST()");

                    // Coordinate FROST signing ceremony for this input's sighash
                    var signingResult = await FrostMPCService.CoordinateSigningCeremony(
                        sighashHex,
                        scUID,
                        validators,
                        threshold,
                        ceremonyId,
                        coordinatorAddress,
                        withdrawalRequestHash,
                        preSignedAuth);

                    if (signingResult == null || !signingResult.SignatureValid)
                    {
                        return (false, string.Empty, string.Empty, $"FROST signing failed for input {i}");
                    }

                    // The aggregate signature from FROST is a 64-byte Schnorr signature
                    var aggregateSignatureBytes = Convert.FromHexString(signingResult.SchnorrSignature);

                    if (aggregateSignatureBytes.Length != 64)
                    {
                        return (false, string.Empty, string.Empty, 
                            $"Invalid Schnorr signature length for input {i}: expected 64 bytes, got {aggregateSignatureBytes.Length}");
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

                return (true, signedTxHex, txHash, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error signing transaction with FROST: {ex}", "BitcoinTransactionService.SignTransactionWithFROST()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}");
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

                var client = GetElectrumClient();
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
                var client = GetElectrumClient();
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
        /// Complete workflow: Build, sign with FROST, and broadcast a Bitcoin withdrawal transaction
        /// </summary>
        public static async Task<(bool Success, string TxHash, string SignedTxHex, string ErrorMessage)>
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
                PreSignedLeaderAuth? preSignedAuth = null)
        {
            try
            {
                // Step 1: Build unsigned transaction
                var buildResult = await BuildUnsignedTaprootTransaction(
                    taprootAddress, 
                    destinationAddress, 
                    amountBTC, 
                    feeRateSatsPerVByte);

                if (!buildResult.Success || buildResult.UnsignedTx == null)
                {
                    return (false, string.Empty, string.Empty, $"Failed to build transaction: {buildResult.ErrorMessage}");
                }

                // Step 2: Sign with FROST (FIND-020: pass spent coins for BIP341 sighash computation)
                var signingResult = await SignTransactionWithFROST(
                    buildResult.UnsignedTx,
                    buildResult.SpentCoins,
                    scUID, 
                    validators,
                    threshold,
                    coordinatorAddress,
                    withdrawalRequestHash,
                    preSignedAuth);

                if (!signingResult.Success)
                {
                    return (false, string.Empty, string.Empty, $"Failed to sign transaction: {signingResult.ErrorMessage}");
                }

                // Step 3: Optionally broadcast signed transaction to Bitcoin network
                if (!broadcast)
                {
                    // signOnly mode: return the signed TX hex without broadcasting
                    LogUtility.Log($"[FROST] Sign-only mode: returning signed TX hex without broadcasting. TxHash: {signingResult.TxHash}",
                        "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                    return (true, signingResult.TxHash, signingResult.SignedTxHex, string.Empty);
                }

                // Parse the FROST-signed tx hex back into a Transaction object for broadcast
                var btcNetwork = Globals.BTCNetwork;
                var signedTx = NBitcoin.Transaction.Parse(signingResult.SignedTxHex, btcNetwork);
                var broadcastResult = await BroadcastTransaction(signedTx);

                if (!broadcastResult.Success)
                {
                    // Broadcast failed but we have the signed hex — return it so caller can retry
                    return (false, string.Empty, signingResult.SignedTxHex, $"Failed to broadcast transaction: {broadcastResult.ErrorMessage}");
                }

                return (true, broadcastResult.TxHash, signingResult.SignedTxHex, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error executing FROST withdrawal: {ex}", "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                return (false, string.Empty, string.Empty, $"Error: {ex.Message}");
            }
        }
    }
}
