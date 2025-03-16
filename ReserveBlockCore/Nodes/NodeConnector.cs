﻿using ReserveBlockCore.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Nodes
{
    public class NodeConnector
    {
        public static async Task StartNodeConnecting()
        {
            //var nodeIp = SeedNodeService.PingSeedNode();
            //SeedNodeService.GetSeedNodePeers("");
            int successfulConnect = 0;
            bool alreadyCalled = false;

            List<Peers> peers = new List<Peers>();
            peers = Peers.GetAll().Find(x => true, 0, 9).ToList();

            if(peers.Count == 0)
            {
                SeedNodeService.SeedNodes();
                var nodeIp = await SeedNodeService.PingSeedNode();
                if (Globals.IsTestNet == false)
                    await SeedNodeService.GetSeedNodePeers(nodeIp);
                alreadyCalled = true;
            }

            if(Globals.IsTestNet)
                await SeedNodeService.GetSeedNodePeersTestnet();

            if(!Globals.IsTestNet)
                await SeedNodeService.GetSeedNodePeers();

            peers = Peers.GetAll().FindAll().ToList();
            if(peers.Count <= 8 && !Globals.IsTestNet)
            {
                //request peers from other nodes
                //Then request from seeds
                if(!alreadyCalled)
                {
                    var nodeIp = await SeedNodeService.PingSeedNode();
                    await SeedNodeService.GetSeedNodePeers(nodeIp);
                }

            }
            else
            {
                //No peers available. You will have to manually add them.
            }
        }       
    }
}
