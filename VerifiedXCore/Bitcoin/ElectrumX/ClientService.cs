using Elmah.ContentSyndication;
using VerifiedXCore.Bitcoin.Models;

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

                    if (transactions == null)
                        return;

                    var walletUtxoList = BitcoinUTXO.GetUTXOs(address);

                    // An empty-but-non-null Electrum answer would fall through to the reconcile
                    // branch below and DELETE every cached UTXO for the address. A single behind or
                    // pruned server can answer empty for a funded address, so corroborate an
                    // empty-while-cached answer with a SECOND Electrum server before wiping the cache.
                    if (transactions.Count == 0 && walletUtxoList.Count() > 0)
                    {
                        try
                        {
                            using (var secondClient = await GetElectrumClient())
                            {
                                if (secondClient == null)
                                    return; // No second server reachable — do not trust the empty answer.

                                var secondAnswer = await secondClient.GetListUnspent(address, false);
                                if (secondAnswer == null || secondAnswer.Count > 0)
                                    return; // Unreachable or disagrees — keep the cache intact.
                            }
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

        public static async Task<Client?> GetElectrumClient()
        {
            Client? client = null;
            bool electrumServerFound = false;

            while (!electrumServerFound)
            {
                var electrumServer = Globals.ClientSettings.Where(x => x.FailCount < 10).OrderBy(x => x.Count).FirstOrDefault();
                if (electrumServer != null)
                {
                    try
                    {
                        client = new Client(electrumServer.Host, electrumServer.Port, true);
                        var serverVersion = await client.GetServerVersion();

                        if (serverVersion == null)
                            throw new Exception("Bad server response or no connection.");

                        if (serverVersion.ProtocolVersion.Major != 1 && serverVersion.ProtocolVersion.Minor < 4)
                            throw new Exception("Bad version.");

                        electrumServerFound = true;
                        electrumServer.Count++;
                    }
                    catch (Exception ex)
                    {
                        //TODO: ADD LOGS
                        electrumServer.FailCount++;
                        electrumServer.Count++;
                        await Task.Delay(1000);
                    }

                }
                else
                {
                    //no servers found
                    break;
                }
                //TODO: ADD LOGS
            }
            return client;
        }
    }
}
