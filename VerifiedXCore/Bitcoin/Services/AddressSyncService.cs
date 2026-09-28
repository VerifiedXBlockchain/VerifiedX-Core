using NBitcoin;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Utilities;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    public class AddressSyncService
    {
        public static async Task SyncAddress(string address)
        {
            // One handshaked client for the whole sync. (The old loop ran the sync even on a client whose handshake
            // had just failed, and saved the failed balance lookup as zero.)
            using var client = await ElectrumServerPool.GetClientAsync();
            if (client == null)
                return;

            await GetBalance(client, address);
            await GetTxHistory(client, address);
            await Getinputs(client, address);
        }
        private static async Task GetBalance(Client client, string address)
        {
            var balance = await client.GetBalance(address, false);
            if (!client.LastCallSucceeded)
                return; // no answer is not a zero balance: keep what is stored
            var btcAccount = BitcoinAccount.GetBitcoin()?.FindOne(x => x.Address == address);
            if (btcAccount != null)
            {
                btcAccount.Balance = balance.Confirmed / 100_000_000M;
                BitcoinAccount.GetBitcoin()?.UpdateSafe(btcAccount);
            }
        }

        public static async Task GetTxHistory(Client client, string address)
        {
            var history = await client.GetHistory(address);
            if (history != null)
            {
                foreach (var bTx in history)
                {
                    if (bTx.Height > 0)
                    {
                        var blockHeader = await client.GetBlockHeaderHex(bTx.Height);
                        if (blockHeader != null)
                        {
                            var timestampHex = blockHeader.Hex.Substring(136, 8);

                            // Reverse the byte order (little-endian to big-endian)
                            var reversedTimestampHex = string.Join("", Enumerable.Range(0, 4).Select(i => timestampHex.Substring(i * 2, 2)).Reverse());

                            // Convert the reversed hex string to Unix timestamp
                            var timestampUnix = TimeUtil.GetTime(reversedTimestampHex);
                            var rawTx = await client.GetRawTx(bTx.TxHash);
                            var tx = Transaction.Parse(rawTx.RawTx, Globals.BTCNetwork);
                            var bitcoinAddress = BitcoinAddress.Create(address, Globals.BTCNetwork);
                            List<BitcoinAddress> outgoingAddrs = new List<BitcoinAddress>();
                            foreach (var input in tx.Inputs)
                            {
                                var result = await InputUtility.GetAddressFromInput(client, input);
                                if (result != null)
                                    outgoingAddrs.Add(result);
                            }

                            bool isOutgoing = outgoingAddrs.Any(inputAddress => inputAddress == bitcoinAddress);

                            var fromAddress = "";
                            var toAddress = "";
                            var amount = 0.0M;
                            foreach (var output in tx.Outputs)
                            {
                                var outputAddress = output.ScriptPubKey.GetDestinationAddress(Globals.BTCNetwork);


                                // Heuristic: if the address is not the sender's address, it's likely a recipient
                                if (outputAddress == bitcoinAddress && isOutgoing)
                                {
                                    fromAddress = address;
                                    toAddress = outputAddress.ToString();
                                }
                                if (outputAddress != bitcoinAddress && isOutgoing)
                                {
                                    amount = output.Value.ToUnit(MoneyUnit.BTC);
                                }

                                if (outputAddress != bitcoinAddress && !isOutgoing)
                                {
                                    fromAddress = outputAddress.ToString();
                                    toAddress = address;
                                }
                                if (outputAddress == bitcoinAddress && !isOutgoing)
                                {
                                    amount = output.Value.ToUnit(MoneyUnit.BTC);
                                }
                            }

                            var totalInputAmount = await InputUtility.CalculateTotalInputAmount(client, tx);
                            var totalOutputAmount = tx.Outputs.Sum(o => o.Value);
                            var fee = totalInputAmount - totalOutputAmount;

                            var nTx = new BitcoinTransaction
                            {
                                Amount = amount,
                                BitcoinUTXOs = new List<BitcoinUTXO>(),
                                Fee = fee.ToUnit(MoneyUnit.BTC),
                                FeeRate = 0,
                                FromAddress = fromAddress,
                                ToAddress = toAddress,
                                Hash = bTx.TxHash,
                                IsConfirmed = true,
                                ConfirmedHeight = bTx.Height,
                                Signature = rawTx.RawTx.ToString(),
                                Timestamp = timestampUnix,
                                TransactionType = isOutgoing ? BTCTransactionType.Send : BTCTransactionType.Receive,
                            };

                            BitcoinTransaction.SaveBitcoinTX(nTx);
                        }
                    }
                }
            }
        }

        private static async Task Getinputs(Client client, string address)
        {
            var transactions = await client.GetListUnspent(address, false);
            if (transactions?.Count > 0)
            {
                var walletUtxoList = BitcoinUTXO.GetUTXOs(address);
                if (walletUtxoList != null)
                {
                    var utxoList = transactions;
                    if (utxoList?.Count > 0)
                    {
                        foreach (var item in utxoList)
                        {
                            var nUTXO = new BitcoinUTXO
                            {
                                Address = address,
                                IsUsed = false,
                                TxId = item.TxHash,
                                Value = (long)item.Value,
                                Vout = (int)item.TxPos
                            };

                            BitcoinUTXO.SaveBitcoinUTXO(nUTXO, true);
                        }
                    }
                }
                else
                {
                    if (transactions?.Count > 0)
                    {
                        foreach (var item in transactions)
                        {
                            var nUTXO = new BitcoinUTXO
                            {
                                Address = address,
                                IsUsed = false,
                                TxId = item.TxHash,
                                Value = (long)item.Value,
                                Vout = (int)item.TxPos
                            };

                            BitcoinUTXO.SaveBitcoinUTXO(nUTXO, true);
                        }
                    }
                }
            }
        }
    }
}
