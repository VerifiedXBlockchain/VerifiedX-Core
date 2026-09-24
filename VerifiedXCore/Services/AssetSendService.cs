using VerifiedXCore.Utilities;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace VerifiedXCore.Services
{
    public class AssetSendService
    {
        public static ConcurrentDictionary<string, byte[][]> AssetByteArrayDictionary = new ConcurrentDictionary<string, byte[][]>();

        /// <summary>VX-04: thumbnails served over UDP are small; refuse to load anything larger.</summary>
        public const long MaxServedAssetBytes = 25L * 1024 * 1024;

        public static async Task SendAsset(string asset, string scUID, IPEndPoint endPoint, UdpClient udpClient, int ackNum)
        {
            try
            {
                if (ackNum < 0)
                    return;

                var location = NFTAssetFileUtility.NFTAssetPath(asset, scUID, true);
                if (location == "NA")
                    return;

                // VX-04: cache by the canonical, validated path (was the raw request string).
                var dictKey = location;

                if(AssetByteArrayDictionary.TryGetValue(dictKey, out var packetArray)) 
                {
                    if (ackNum < packetArray.Length)
                    {
                        var packet = packetArray[ackNum];
                        if (packet != null)
                            _ = udpClient.SendAsync(packet, packet.Length, endPoint);
                    }
                    else
                    {
                        SCLogUtility.Log($"Packet Ack Num too large. Asset: {asset} | AckNum: {ackNum} | Contract UID : {scUID} | IP: {endPoint.ToString()}", "AssetSendService.SendAsset()");
                    }
                }
                else
                {
                    if (location != "NA" && new FileInfo(location).Length <= MaxServedAssetBytes)
                    {
                        var assetBytes = NFTAssetFileUtility.GetNFTAssetByteArray(location);
                        if (assetBytes != null)
                        {
                            var packets = NFTAssetFileUtility.SplitIntoPackets(assetBytes);

                            AssetByteArrayDictionary.TryAdd(dictKey, packets);

                            if (packets != null)
                            {
                                if (ackNum < packets.Length)
                                {
                                    var packet = packets[ackNum];
                                    if (packet != null)
                                        _ = udpClient.SendAsync(packet, packet.Length, endPoint);
                                }
                                else
                                {
                                    SCLogUtility.Log($"Packet Ack Num too large. Asset: {asset} | AckNum: {ackNum} | Contract UID : {scUID} | IP: {endPoint.ToString()}", "AssetSendService.SendAsset()");
                                }
                            }
                        }
                    }
                    else
                    {
                        //create -1 packet
                        byte[] fileNotFoundPacket = new byte[] { 0xFF };
                        await udpClient.SendAsync(fileNotFoundPacket, fileNotFoundPacket.Length, endPoint);

                    }
                }
            }
            catch(Exception ex) 
            {
                SCLogUtility.Log($"Unknown Error: {ex.ToString()} - Asset: {asset} - ACK Num: {ackNum} - SCUID: {scUID} - IP: {endPoint.ToString()}", "AssetSendService.SendAsset()");
            }
        }
    }
}
