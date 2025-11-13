using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Text;

namespace ReserveBlockCore.Controllers
{
    [ActionFilterController]
    [Route("adjapi/[controller]")]
    [Route("adjapi/[controller]/{somePassword?}")]
    [ApiController]
    public class ADJV1Controller : ControllerBase
    {
        /// <summary>
        /// Check Status of API
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "VFX-ADJ", "API" };
        }

        /// <summary>
        /// Returns entire duplicates dictionary
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetDups")]
        public async Task<string> GetDups()
        {
            string output = "";
            try
            {
                var dups = Globals.DuplicatesBroadcastedDict.Values.Select(x => new
                {
                    x.IPAddress,
                    x.Address,
                    x.StopNotify,
                    x.Reason,
                    x.LastNotified,
                    x.LastDetection,
                    x.NotifyCount

                }).ToList();

                output = JsonConvert.SerializeObject(dups);
            }
            catch (Exception ex)
            {
                output = $"Error calling api: {ex.ToString()}";
            }

            return output;
        }


        /// <summary>
        /// Shows the consensus broadcast list of txs
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetConsensusBroadcastTx")]
        public async Task<string> GetConsensusBroadcastTx()
        {
            var output = "";

            try
            {
                var txlist = Globals.ConsensusBroadcastedTrxDict.Values.ToList();

                if (txlist.Count > 0)
                {
                    output = JsonConvert.SerializeObject(txlist);
                }

            }
            catch (Exception ex)
            {
                output = $"Error calling api: {ex.ToString()}";
            }
            
            return output;
        }

        /// <summary>
        /// Shows the fortis pool work broadcast list of txs
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetFortisBroadcastTx")]
        public async Task<string> GetFortisBroadcastTx()
        {
            var output = "";

            try
            {
                var txlist = Globals.BroadcastedTrxDict.Values.ToList();

                if (txlist.Count > 0)
                {
                    output = JsonConvert.SerializeObject(txlist);
                }
            }
            catch(Exception ex)
            {
                output = $"Error calling api: {ex.ToString()}";
            }

            return output;
        }


        /// <summary>
        /// Returns entire fortis pool (Masternode List)
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetMasternodes")]
        public async Task<string> GetMasternodes()
        {
            string output = "";

            try
            {
                var validators = Globals.FortisPool.Values.Select(x => new
                {
                    x.Context.ConnectionId,
                    x.ConnectDate,
                    x.LastAnswerSendDate,
                    x.IpAddress,
                    x.Address,
                    x.UniqueName,
                    x.WalletVersion
                }).ToList();

                output = JsonConvert.SerializeObject(validators);
            }
            catch(Exception ex)
            {
                output = $"Error calling api: {ex.ToString()}";
            }

            return output;
        }

        /// <summary>
        /// Returns master node list that is sent
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetMasternodesSent")]
        public async Task<string> GetMasternodesSent()
        {
            string output = "";

            try
            {
                var fortisPool = Globals.FortisPool.Values.Where(x => x.LastAnswerSendDate != null).Select(x => new
                {
                    x.Context.ConnectionId,
                    x.ConnectDate,
                    x.LastAnswerSendDate,
                    x.IpAddress,
                    x.Address,
                    x.UniqueName,
                    x.WalletVersion
                }).ToList();

                output = JsonConvert.SerializeObject(fortisPool);
            }
            catch(Exception ex)
            {
                output = $"Error calling api: {ex.ToString()}";
            }

            return output;
        }

        /// <summary>
        /// Returns ADJ info
        /// </summary>
        /// <returns></returns>
        [HttpGet("GetAdjInfo")]
        public async Task<string> GetAdjInfo()
        {
            var output = "";

            try
            {
                StringBuilder outputBuilder = new StringBuilder();

                var adjConsensusNodes = Globals.Nodes.Values.ToList();
                var Now = TimeUtil.GetMillisecondTime();
                if (adjConsensusNodes.Count() > 0)
                {
                    outputBuilder.AppendLine("*******************************Consensus Nodes*******************************");
                    foreach (var cNode in adjConsensusNodes)
                    {
                        var line = $"IP: {cNode.NodeIP} | Address: {cNode.Address} | IsConnected? {cNode.IsConnected} ({Now - cNode.LastMethodCodeTime < ConsensusClient.HeartBeatTimeout})";
                        outputBuilder.AppendLine(line);
                    }
                    outputBuilder.AppendLine("******************************************************************************");
                }

                output = outputBuilder.ToString();
            }
            catch(Exception ex) 
            {
                output = $"Error calling api: {ex.ToString()}";
            }
             
            return output;
        }
    }
}
