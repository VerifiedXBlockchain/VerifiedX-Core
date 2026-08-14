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

        /// <summary>
        /// Returns the selectable Electrum servers (FailCount under the threshold) ordered by
        /// least-used first. If every server has exceeded the threshold, the pool is reset
        /// wholesale (mirroring Bitcoin.ElectrumXRun) so a long outage cannot permanently
        /// exhaust it — the old behavior left a node with zero usable servers until restart.
        /// </summary>
        public static List<ClientSettings> GetServerCandidates(ulong failThreshold = 10)
        {
            var candidates = Globals.ClientSettings?.Where(x => x.FailCount < failThreshold).OrderBy(x => x.Count).ToList()
                ?? new List<ClientSettings>();

            if (!candidates.Any() && Globals.ClientSettings?.Any() == true)
            {
                foreach (var server in Globals.ClientSettings)
                    server.FailCount = 0;

                candidates = Globals.ClientSettings.Where(x => x.FailCount < failThreshold).OrderBy(x => x.Count).ToList();
            }

            return candidates;
        }

        /// <summary>
        /// Gets a handshake-verified Electrum client, or null if no server could be reached.
        /// One bounded pass over the candidate pool: each server gets a single handshake attempt
        /// (the old loop re-hammered the same least-used server until its FailCount hit the
        /// threshold, then returned the last BROKEN client object once the pool was exhausted).
        /// </summary>
        public static async Task<Client?> GetElectrumClient()
        {
            var candidates = GetServerCandidates();

            foreach (var electrumServer in candidates)
            {
                Client? client = null;
                try
                {
                    client = new Client(electrumServer.Host, electrumServer.Port, true);
                    var serverVersion = await client.GetServerVersion();

                    // GetServerVersion never returns null; a dead server yields an empty result whose
                    // ProtocolVersion access throws below — the generic catch is the real guard.
                    if (serverVersion == null)
                        throw new Exception("Bad server response or no connection.");

                    if (serverVersion.ProtocolVersion.Major != 1 && serverVersion.ProtocolVersion.Minor < 4)
                        throw new Exception("Bad version.");

                    electrumServer.Count++;
                    return client;
                }
                catch (Exception ex)
                {
                    electrumServer.FailCount++;
                    electrumServer.Count++;
                    client?.Dispose();
                    ErrorLogUtility.LogError($"Electrum handshake failed on {electrumServer.Host}:{electrumServer.Port}: {ex.Message}", "ClientService.GetElectrumClient()");
                    await Task.Delay(1000);
                }
            }

            ErrorLogUtility.LogError($"All {candidates.Count} Electrum server(s) failed handshake — no client available", "ClientService.GetElectrumClient()");
            return null;
        }
    }
}
