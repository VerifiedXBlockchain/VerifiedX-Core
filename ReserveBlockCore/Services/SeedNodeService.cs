﻿using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    public class SeedNodeService
    {
        public static List<SeedNode> SeedNodeList { get; set; }
        public static async Task<string> PingSeedNode()
        {
            bool nodeFound = false;
            var url = "NA";
            foreach(var node in SeedNodeList)
            {
                try
                {
                    using (HttpClient client = new HttpClient())
                    {

                        string endpoint = node.NodeUrl;
                        using (var Response = await client.GetAsync(endpoint))
                        {
                            if (Response.StatusCode == System.Net.HttpStatusCode.OK)
                            {
                                string data = await Response.Content.ReadAsStringAsync();

                                var _response = data.TrimStart('[').TrimEnd(']').Replace("\"", "").Split(',');
                                var status = _response[1];
                                if(status == "Online")
                                {
                                    nodeFound = true;
                                    url = node.NodeUrl;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {

                }
            }

            return url;

        }

        public static async void GetSeedNodePeers(string url)
        {
            using (HttpClient client = new HttpClient())
            {

                string endpoint = url;
                using (var Response = await client.GetAsync(endpoint + "/api/V1/GetNodes"))
                {
                    if (Response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        string data = await Response.Content.ReadAsStringAsync();
                        var peers = data.TrimStart('[').TrimEnd(']').Replace("\"", "").Split(',');
                        var peerCount = peers.Count() - 1;
                        for(var i = 0 ; i <= peerCount; i++)
                        {
                            var peer = peers[i];
                            Peers nPeer = new Peers { 
                                IsIncoming = false,
                                IsOutgoing = true,
                                PeerIP = peer,
                                FailCount = 0
                            };

                            var dbPeers = Peers.GetAll();
                            var peerExist = dbPeers.FindOne(x => x.PeerIP == peer);
                            if(peerExist == null)
                                dbPeers.Insert(nPeer);
                        }
                    }
                    else
                    {

                    }
                }
            }
        }

        public static List<SeedNode> SeedNodes()
        {
            SeedNodeList = new List<SeedNode>();

            List<SeedNode> seedNodes = new List<SeedNode>();

            seedNodes.Add(new SeedNode
            {
                NodeUrl = "https://seed1.rbx.network"
            });

            seedNodes.Add(new SeedNode
            {
                NodeUrl = "https://seed2.rbx.network"
            });

            SeedNodeList.AddRange(seedNodes);

            return seedNodes;
        }

    }
}
