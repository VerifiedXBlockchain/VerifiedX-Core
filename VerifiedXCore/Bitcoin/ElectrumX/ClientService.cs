using Elmah.ContentSyndication;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.ElectrumX
{
    public class ClientService
    {
        public static async Task UpdateUTXO(string address)
        {
            //var client = await GetElectrumClient();
            try
            {
                using (var client = await GetElectrumClient())
                {
                    if (client == null)
                        return;

                    var transactions = await client.GetListUnspent(address, false);

                    // A failed call returns an empty list; reconciling against it deleted every cached UTXO.
                    if (transactions == null || !client.LastCallSucceeded)
                        return;

                    var walletUtxoList = BitcoinUTXO.GetUTXOs(address);

                    // An empty-but-non-null Electrum answer would fall through to the reconcile
                    // branch below and DELETE every cached UTXO for the address. A single behind or
                    // pruned server can answer empty for a funded address, so corroborate an
                    // empty-while-cached answer with a SECOND, different Electrum server before wiping the cache.
                    if (transactions.Count == 0 && walletUtxoList.Count() > 0)
                    {
                        try
                        {
                            var others = ElectrumServerPool.GetCandidates().Where(s => s.Label != client.ServerLabel).ToList();
                            var secondAnswer = await ElectrumServerPool.FirstSuccessAsync(others,
                                s => ElectrumServerPool.AttemptAsync(s, c => c.GetListUnspent(address, false)));
                            if (secondAnswer == null || secondAnswer.Value.Value.Count > 0)
                                return; // Unreachable or disagrees — keep the cache intact.
                        }
                        catch
                        {
                            return;
                        }
                    }

                    if (walletUtxoList.Count() == 0)
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
                    else
                    {
                        foreach (var localUtxo in walletUtxoList)
                        {
                            var tx = transactions.Where(x => x.TxHash == localUtxo.TxId).FirstOrDefault();
                            if (tx != null)
                            {
                                var nUTXO = new BitcoinUTXO
                                {
                                    Address = address,
                                    IsUsed = false,
                                    TxId = tx.TxHash,
                                    Value = (long)tx.Value,
                                    Vout = (int)tx.TxPos
                                };

                                BitcoinUTXO.SaveBitcoinUTXO(nUTXO, true);
                            }
                            else
                            {
                                await BitcoinUTXO.DeleteBitcoinUTXO(localUtxo);
                            }
                        }
                    }
                    client.Dispose();
                }
            }
            catch (Exception ex)
            {

            }
        }

        /// <summary>Servers a caller may use now, best first (see ElectrumServerPool).</summary>
        public static List<ClientSettings> GetServerCandidates()
        {
            return ElectrumServerPool.GetCandidates();
        }

        /// <summary>A handshake-verified Electrum client, or null if no server answered (see ElectrumServerPool).</summary>
        public static Task<Client?> GetElectrumClient()
        {
            return ElectrumServerPool.GetClientAsync();
        }
    }
}
