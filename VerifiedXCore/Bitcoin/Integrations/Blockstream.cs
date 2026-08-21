using System.Net.Http;
using System;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.ElectrumX.Results;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using Microsoft.VisualBasic;

namespace VerifiedXCore.Bitcoin.Integrations
{
    public class Blockstream
    {
        public static async Task GetAddressBalance(string address, string vfxAddress, bool isTokenAddress = false)
        {
            var baseUri = GetBaseURL();
            var uri = $"{baseUri}/address/{address}";

            try
            {
                using (var client = Globals.HttpClientFactory.CreateClient())
                {
                    var httpResponse = await client.GetAsync(uri);
                    if (httpResponse.IsSuccessStatusCode)
                    {
                        var responseContent = await httpResponse.Content.ReadAsStringAsync();
                        if (responseContent != null)
                        {
                            Root? response = JsonConvert.DeserializeObject<Root>(responseContent);
                            if(response != null)
                            {
                                var returnedAddress = response.address;
                                var btcAccount = BitcoinAccount.GetBitcoin()?.FindOne(x => x.Address == returnedAddress);
                                if(btcAccount != null)
                                {
                                    btcAccount.Balance = (response.chain_stats.funded_txo_sum - response.chain_stats.spent_txo_sum) / 100_000_000M;
                                    BitcoinAccount.GetBitcoin()?.UpdateSafe(btcAccount);
                                }
                                if(isTokenAddress)
                                {
                                    var balance = (response.chain_stats.funded_txo_sum - response.chain_stats.spent_txo_sum) / 100_000_000M;
                                    await TokenizedBitcoin.UpdateBalance(returnedAddress, balance, vfxAddress);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }
        }

        public static async Task GetAddressUTXO(string address)
        {
            try
            {
                var utxos = await GetAddressUTXOList(address);
                if (utxos.Count > 0)
                {
                    foreach (var item in utxos)
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

                    //TODO:perform audit and update values as needed.
                    //Remove them from DB saves.
                    //Push them into memory
                    //Perform audit after every tx send
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>
        /// Fetches UTXOs for an address via the blockstream.info Esplora API and returns them in the
        /// ElectrumX result shape consumed by the transaction builder. Does NOT persist to the local
        /// store — use GetAddressUTXO for that. Returns an empty list on any failure.
        /// </summary>
        public static async Task<List<BlockchainScripthashListunspentResult>> GetAddressUTXOList(string address)
        {
            var (results, _) = await TryGetAddressUTXOList(address);
            return results;
        }

        /// <summary>
        /// As <see cref="GetAddressUTXOList"/>, but reports whether the provider actually ANSWERED
        /// (HTTP success) rather than merely failing to throw. Callers that treat an empty list as
        /// authoritative must use this — see the MempoolSpace counterpart for why.
        /// </summary>
        public static async Task<(List<BlockchainScripthashListunspentResult> Utxos, bool Answered)> TryGetAddressUTXOList(string address)
        {
            var results = new List<BlockchainScripthashListunspentResult>();
            var answered = false;
            var baseUri = GetBaseURL();
            var uri = $"{baseUri}/address/{address}/utxo";

            using (var client = Globals.HttpClientFactory.CreateClient())
            {
                var httpResponse = await client.GetAsync(uri);
                answered = httpResponse.IsSuccessStatusCode;
                if (httpResponse.IsSuccessStatusCode)
                {
                    var responseContent = await httpResponse.Content.ReadAsStringAsync();
                    if (!string.IsNullOrEmpty(responseContent))
                    {
                        List<Transaction>? transactions = JsonConvert.DeserializeObject<List<Transaction>>(responseContent);
                        if (transactions != null)
                        {
                            foreach (var item in transactions)
                            {
                                results.Add(BitcoinTransactionService.MapEsploraUtxo(
                                    item.txid,
                                    item.vout,
                                    item.value,
                                    item.status?.confirmed ?? false,
                                    item.status?.block_height ?? 0));
                            }
                        }
                    }
                }
            }

            return (results, answered);
        }

        public static string GetBaseURL()
        {
            if (Globals.BTCNetwork == NBitcoin.Network.Main)
                return "https://blockstream.info/api";

            return "https://blockstream.info/testnet/api";
        }
        public class Status
        {
            public bool confirmed { get; set; }
            public int block_height { get; set; }
            public string block_hash { get; set; }
            public long block_time { get; set; }
        }

        public class Transaction
        {
            public string txid { get; set; }
            public int vout { get; set; }
            public Status status { get; set; }
            public long value { get; set; }
        }
        public class ChainStats
        {
            public int funded_txo_count { get; set; }
            public int funded_txo_sum { get; set; }
            public int spent_txo_count { get; set; }
            public int spent_txo_sum { get; set; }
            public int tx_count { get; set; }
        }

        public class MempoolStats
        {
            public int funded_txo_count { get; set; }
            public int funded_txo_sum { get; set; }
            public int spent_txo_count { get; set; }
            public int spent_txo_sum { get; set; }
            public int tx_count { get; set; }
        }

        public class Root
        {
            public string address { get; set; }
            public ChainStats chain_stats { get; set; }
            public MempoolStats mempool_stats { get; set; }
        }
    }
}
