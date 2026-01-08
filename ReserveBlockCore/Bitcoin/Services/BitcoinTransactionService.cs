using NBitcoin;
using NBitcoin.Crypto;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Bitcoin.ElectrumX.Results;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.FROST.Models;
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
        public static async Task<(bool Success, NBitcoin.Transaction? UnsignedTx, ulong Fee, List<BlockchainScripthashListunspentResult> UsedUtxos, string ErrorMessage)> 
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
                    return (false, null, 0, new List<BlockchainScripthashListunspentResult>(), $"Invalid address: {ex.Message}");
                }

                // Get UTXOs
                var utxos = await GetTaprootUTXOs(taprootAddress);
                if (!utxos.Any())
                {
                    return (false, null, 0, new List<BlockchainScripthashListunspentResult>(), "No UTXOs found for Taproot address");
                }

                ulong amountToSend = Convert.ToUInt64(amountBTC * BTCMultiplier);
                ulong feeEstimate = 0;
                bool sufficientInputsFound = false;
                List<Coin> unspentCoins = new List<Coin>();
                List<BlockchainScripthashListunspentResult> selectedUtxos = new List<BlockchainScripthashListunspentResult>();
                ulong previousTotalInputAmount = 0;

                // Select UTXOs to cover amount + fees
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

                            ulong totalRequired = amountToSend + feeEstimate;

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
                        return (false, null, 0, new List<BlockchainScripthashListunspentResult>(), 
                            $"Insufficient funds. Required: {(amountToSend + feeEstimate) * SatoshiMultiplier:F8} BTC, Available: {totalInputAmount * SatoshiMultiplier:F8} BTC");
                    }
                }

                // Build the unsigned transaction
                var txBuilder = Globals.BTCNetwork.CreateTransactionBuilder();
                
                txBuilder.AddCoins(unspentCoins.ToArray());
                txBuilder.Send(toAddress, new Money(amountToSend, MoneyUnit.Satoshi));
                txBuilder.SetChange(fromAddress);
                txBuilder.SendFees(new Money(feeEstimate, MoneyUnit.Satoshi));

                var unsignedTx = txBuilder.BuildTransaction(false);

                return (true, unsignedTx, feeEstimate, selectedUtxos, string.Empty);
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error building unsigned Taproot transaction: {ex}", "BitcoinTransactionService.BuildUnsignedTaprootTransaction()");
                return (false, null, 0, new List<BlockchainScripthashListunspentResult>(), $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Signs a transaction using FROST threshold signatures via validator coordination
        /// </summary>
        public static async Task<(bool Success, string SignedTxHex, string TxHash, string ErrorMessage)> 
            SignTransactionWithFROST(
                NBitcoin.Transaction unsignedTx,
                string scUID,
                List<VBTCValidator> validators,
                int threshold)
        {
            try
            {
                // Get the contract to retrieve FROST public key
                var contract = VBTCContractV2.GetContract(scUID);
                if (contract == null)
                {
                    return (false, string.Empty, string.Empty, "Contract not found");
                }

                if (string.IsNullOrEmpty(contract.FrostGroupPublicKey))
                {
                    return (false, string.Empty, string.Empty, "FROST group public key not found in contract");
                }

                // Prepare the message to sign (transaction sighash)
                // For Taproot, we use BIP 341 signature hash
                // Use the transaction ID as the message to sign (simplified for now)
                string messageToSign = unsignedTx.GetHash().ToString();

                // Coordinate FROST signing ceremony
                var signingResult = await FrostMPCService.CoordinateSigningCeremony(
                    messageToSign,
                    scUID,
                    validators,
                    threshold);

                if (signingResult == null || !signingResult.SignatureValid)
                {
                    return (false, string.Empty, string.Empty, "FROST signing failed");
                }

                // The aggregate signature from FROST is a Schnorr signature
                // Convert it to Bitcoin witness format for Taproot
                var aggregateSignatureBytes = Convert.FromHexString(signingResult.SchnorrSignature);
                
                // Create witness for Taproot key path spend
                // Taproot witness is just the signature (64 bytes for Schnorr)
                var witness = new WitScript(Op.GetPushOp(aggregateSignatureBytes));

                // Attach witness to all inputs (they all spend from same Taproot address)
                for (int i = 0; i < unsignedTx.Inputs.Count; i++)
                {
                    unsignedTx.Inputs[i].WitScript = witness;
                }

                string signedTxHex = unsignedTx.ToHex();
                string txHash = unsignedTx.GetHash().ToString();

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
                        return (true, broadcastResult.TxHash, string.Empty);
                    }
                    else
                    {
                        return (false, string.Empty, "Broadcast failed - no result returned");
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

                // Step 2: Sign with FROST
                var signingResult = await SignTransactionWithFROST(
                    buildResult.UnsignedTx, 
                    scUID, 
                    validators,
                    threshold);

                if (!signingResult.Success)
                {
                    return (false, string.Empty, $"Failed to sign transaction: {signingResult.ErrorMessage}");
                }

                // Step 3: Broadcast to Bitcoin network
                var broadcastResult = await BroadcastTransaction(buildResult.UnsignedTx);

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
