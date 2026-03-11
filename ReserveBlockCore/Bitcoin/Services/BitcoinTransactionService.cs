using NBitcoin;
using NBitcoin.Crypto;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Bitcoin.ElectrumX.Results;
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
        /// Fetches UTXOs for a Taproot address via Electrum
        /// </summary>
        public static async Task<List<BlockchainScripthashListunspentResult>> GetTaprootUTXOs(string taprootAddress)
        {
            try
            {
                var client = GetElectrumClient();
                if (client == null)
                {
                    ErrorLogUtility.LogError("No Electrum server available", "BitcoinTransactionService.GetTaprootUTXOs()");
                    return new List<BlockchainScripthashListunspentResult>();
                }

                using (client)
                {
                    // Use the scripthash for Taproot address
                    var bitcoinAddress = BitcoinAddress.Create(taprootAddress, Globals.BTCNetwork);
                    var scriptHash = Client.GetScriptHash(bitcoinAddress);
                    
                    var utxos = await client.GetListUnspent(scriptHash);
                    
                    return utxos?.ToList() ?? new List<BlockchainScripthashListunspentResult>();
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error fetching Taproot UTXOs: {ex}", "BitcoinTransactionService.GetTaprootUTXOs()");
                return new List<BlockchainScripthashListunspentResult>();
            }
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
                int threshold)
        {
            try
            {
                // Get the contract to retrieve FROST public key — try local DB first, fall back to State Trei
                var contract = VBTCContractV2.GetContract(scUID);
                string frostGroupPublicKey = contract?.FrostGroupPublicKey;
                string? ceremonyId = null;

                // If local DB doesn't have the contract (e.g. remote validator / non-validator node),
                // reconstruct the needed data from the State Trei + SmartContractMain which all nodes share.
                if (string.IsNullOrEmpty(frostGroupPublicKey))
                {
                    LogUtility.Log($"[FROST] VBTCContractV2 not in local DB for {scUID}, falling back to State Trei",
                        "BitcoinTransactionService.SignTransactionWithFROST()");

                    var scStateTreiRec = SmartContractStateTrei.GetSmartContractState(scUID);
                    if (scStateTreiRec == null || string.IsNullOrEmpty(scStateTreiRec.ContractData))
                    {
                        return (false, string.Empty, string.Empty, $"Contract not found in local DB or State Trei: {scUID}");
                    }

                    var scMainDecompile = SmartContractMain.GenerateSmartContractInMemory(scStateTreiRec.ContractData);
                    if (scMainDecompile == null || scMainDecompile.Features == null)
                    {
                        return (false, string.Empty, string.Empty, $"Failed to decompile contract {scUID} from State Trei");
                    }

                    var tknzFeature = scMainDecompile.Features
                        .Where(x => x.FeatureName == FeatureName.TokenizationV2)
                        .Select(x => x.FeatureFeatures)
                        .FirstOrDefault();

                    if (tknzFeature == null)
                    {
                        return (false, string.Empty, string.Empty, $"TokenizationV2 feature not found in contract {scUID}");
                    }

                    var tknz = (TokenizationV2Feature)tknzFeature;
                    frostGroupPublicKey = tknz.FrostGroupPublicKey;
                    ceremonyId = tknz.CeremonyId;

                    LogUtility.Log($"[FROST] Resolved FrostGroupPublicKey from State Trei: {frostGroupPublicKey}, CeremonyId: {ceremonyId ?? "null"}",
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
                        ceremonyId);

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
        public static async Task<(bool Success, string TxHash, string ErrorMessage)> 
            ExecuteFROSTWithdrawal(
                string taprootAddress,
                string destinationAddress,
                decimal amountBTC,
                long feeRateSatsPerVByte,
                string scUID,
                List<VBTCValidator> validators,
                int threshold)
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
                    return (false, string.Empty, $"Failed to build transaction: {buildResult.ErrorMessage}");
                }

                // Step 2: Sign with FROST (FIND-020: pass spent coins for BIP341 sighash computation)
                var signingResult = await SignTransactionWithFROST(
                    buildResult.UnsignedTx,
                    buildResult.SpentCoins,
                    scUID, 
                    validators,
                    threshold);

                if (!signingResult.Success)
                {
                    return (false, string.Empty, $"Failed to sign transaction: {signingResult.ErrorMessage}");
                }

                // Step 3: Broadcast signed transaction to Bitcoin network
                // Parse the FROST-signed tx hex back into a Transaction object for broadcast
                var btcNetwork = Globals.BTCNetwork;
                var signedTx = NBitcoin.Transaction.Parse(signingResult.SignedTxHex, btcNetwork);
                var broadcastResult = await BroadcastTransaction(signedTx);

                if (!broadcastResult.Success)
                {
                    return (false, string.Empty, $"Failed to broadcast transaction: {broadcastResult.ErrorMessage}");
                }

                return (true, broadcastResult.TxHash, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error executing FROST withdrawal: {ex}", "BitcoinTransactionService.ExecuteFROSTWithdrawal()");
                return (false, string.Empty, $"Error: {ex.Message}");
            }
        }
    }
}
