﻿using System.Collections.Concurrent;

namespace ReserveBlockCore.Bitcoin.Integrations
{
    public class Explorers
    {
        public static ConcurrentDictionary<ExplorersEnum, int> ExplorerDictionary { get; set; }
        public static async Task GetAddressInfo(string address, string vfxAddress, bool isTokenAddress = false)
        {
            //get balance
            var explorer = ExplorerDictionary.OrderBy(x => x.Value).FirstOrDefault();
            
            if(explorer.Key == ExplorersEnum.MempoolSpace)
            {
                await MempoolSpace.GetAddressBalance(address, vfxAddress, isTokenAddress);
                ExplorerDictionary.TryUpdate(explorer.Key, (explorer.Value + 1), explorer.Value);
            }
            else if(explorer.Key == ExplorersEnum.Blockstream)
            {
                await Blockstream.GetAddressBalance(address, vfxAddress, isTokenAddress);
                ExplorerDictionary.TryUpdate(explorer.Key, (explorer.Value + 1), explorer.Value);
            }
            else if (explorer.Key == ExplorersEnum.MempoolSpaceTestnet4)
            {
                await MempoolSpaceTestnet4.GetAddressBalance(address, vfxAddress, isTokenAddress);
                ExplorerDictionary.TryUpdate(explorer.Key, (explorer.Value + 1), explorer.Value);
            }
            else
            {
                //no explorers are available, log error.
            }

            
            //get utxo spend
            var explorerUTXO = ExplorerDictionary.OrderBy(x => x.Value).FirstOrDefault();
            if (explorerUTXO.Key == ExplorersEnum.MempoolSpace)
            {
                await MempoolSpace.GetAddressUTXO(address);
                ExplorerDictionary.TryUpdate(explorerUTXO.Key, (explorerUTXO.Value + 1), explorerUTXO.Value);
            }
            else if (explorerUTXO.Key == ExplorersEnum.Blockstream)
            {
                await Blockstream.GetAddressUTXO(address);
                ExplorerDictionary.TryUpdate(explorerUTXO.Key, (explorerUTXO.Value + 1), explorerUTXO.Value);
            }
            else if (explorerUTXO.Key == ExplorersEnum.MempoolSpaceTestnet4)
            {
                await MempoolSpaceTestnet4.GetAddressUTXO(address);
                ExplorerDictionary.TryUpdate(explorerUTXO.Key, (explorerUTXO.Value + 1), explorerUTXO.Value);
            }
            else
            {
                //no explorers are available, log error.
            }
            
        }

        public enum ExplorersEnum
        {
            Blockstream,
            MempoolSpace,
            MempoolSpaceTestnet4
        }

        public static void PopulateExplorerDictionary()
        {
            ExplorerDictionary = new ConcurrentDictionary<ExplorersEnum, int>();

            if(!Globals.IsTestNet)
            {
                ExplorerDictionary.TryAdd(ExplorersEnum.Blockstream, 0);
                ExplorerDictionary.TryAdd(ExplorersEnum.MempoolSpace, 0);
            }
            else
            {
                if (Globals.BTCNetwork == NBitcoin.Network.TestNet4)
                {
                    ExplorerDictionary.TryAdd(ExplorersEnum.MempoolSpaceTestnet4, 0);
                }
                else
                {
                    ExplorerDictionary.TryAdd(ExplorersEnum.Blockstream, 0);
                    ExplorerDictionary.TryAdd(ExplorersEnum.MempoolSpace, 0);
                }
                
            }
            
        }
    }
}
