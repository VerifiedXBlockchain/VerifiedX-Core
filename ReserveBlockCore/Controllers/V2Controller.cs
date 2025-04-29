using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using System.Security.Principal;

namespace ReserveBlockCore.Controllers
{
    [ActionFilterController]
    [Route("api/[controller]")]
    [Route("api/[controller]/{somePassword?}")]
    [ApiController]
    public class V2Controller : ControllerBase
    {
        /// <summary>
        /// Check Status of API
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public IEnumerable<string> Get()
        {
            return new string[] { "VFX-Wallet", "API V2" };
        }

        /// <summary>
        /// Gets VFX and Token Balances
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetBalances")]
        public async Task<string> GetBalances()
        {
            var output = "Command not recognized."; // this will only display if command not recognized.
            var accounts = AccountData.GetAccounts();
            var rAccounts = ReserveAccount.GetReserveAccounts();

            var scs = SmartContractMain.SmartContractData.GetSCs()
                    .Find(x => x.Features != null &&
                        !x.Features.Where(y => y != null && y.FeatureName == FeatureName.Tokenization).Any())
                    .ToList();

            if (accounts.Count() == 0)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"No Accounts Found" });
            }
            else
            {
                var accountList = accounts.Query().Where(x => true).ToEnumerable();
                List<AccountBalance> accountBalanceList = new List<AccountBalance>();
                foreach (var account in accountList)
                {
                    List<TokenAccount> tokenAccounts = new List<TokenAccount>();
                    var stateAccount = StateData.GetSpecificAccountStateTrei(account.Address);
                    if (stateAccount != null)
                    {
                        AccountBalance accountBalance = new AccountBalance
                        {
                            Address = account.Address,
                            RBXBalance = account.Balance,
                            RBXLockedBalance = account.LockedBalance,
                            TokenAccounts = stateAccount.TokenAccounts?.Count > 0 ? stateAccount.TokenAccounts : tokenAccounts
                        };
                        accountBalanceList.Add(accountBalance);
                    }
                    else
                    {
                        AccountBalance accountBalance = new AccountBalance
                        {
                            Address = account.Address,
                            RBXBalance = 0.0M,
                            RBXLockedBalance = 0.0M,
                            TokenAccounts = tokenAccounts
                        };
                        accountBalanceList.Add(accountBalance);
                    }
                }

                if(rAccounts?.Count > 0)
                {
                    foreach(var rAccount in rAccounts)
                    {
                        List<TokenAccount> tokenAccounts = new List<TokenAccount>();
                        var stateAccount = StateData.GetSpecificAccountStateTrei(rAccount.Address);
                        if (stateAccount != null)
                        {
                            AccountBalance accountBalance = new AccountBalance
                            {
                                Address = rAccount.Address,
                                RBXBalance = rAccount.AvailableBalance,
                                TokenAccounts = stateAccount.TokenAccounts?.Count > 0 ? stateAccount.TokenAccounts : tokenAccounts
                            };
                            accountBalanceList.Add(accountBalance);
                        }
                        else
                        {
                            AccountBalance accountBalance = new AccountBalance
                            {
                                Address = rAccount.Address,
                                RBXBalance = 0.0M,
                                RBXLockedBalance = 0.0M,
                                TokenAccounts = tokenAccounts
                            };
                            accountBalanceList.Add(accountBalance);
                        }
                    }
                }

                if(scs.Any())
                {
                    var tokenList = accountBalanceList.Select(x => x.TokenAccounts).FirstOrDefault();
                    if (tokenList != null)
                    {
                        var scStateTrei = SmartContractStateTrei.GetSCST();
                        if (scStateTrei != null)
                        {
                            foreach (var sc in scs)
                            {
                                try
                                {
                                    if (sc != null)
                                    {
                                        var sToken = tokenList.Where(x => x.SmartContractUID == sc.SmartContractUID).FirstOrDefault();
                                        if (sToken == null)
                                        {
                                            var scState = scStateTrei.FindOne(x => x.SmartContractUID == sc.SmartContractUID);
                                            if (scState != null)
                                            {
                                                var sAccount = accounts.FindOne(x => x.Address == scState.OwnerAddress);
                                                if (sAccount != null)
                                                {
                                                    var scFeature = sc.Features.Where(x => x.FeatureName == FeatureName.Token).FirstOrDefault();
                                                    var scTokenFeature = (TokenFeature)scFeature.FeatureFeatures;
                                                    var tknAcc = new TokenAccount
                                                    {
                                                        Balance = 0.0M,
                                                        DecimalPlaces = scTokenFeature.TokenDecimalPlaces,
                                                        LockedBalance = 0.0M,
                                                        SmartContractUID = sc.SmartContractUID,
                                                        TokenName = scTokenFeature.TokenName,
                                                        TokenTicker = scTokenFeature.TokenTicker
                                                    };

                                                    var nAccBalList = accountBalanceList.Where(x => x.Address == scState.OwnerAddress).FirstOrDefault();
                                                    if (nAccBalList != null)
                                                    {
                                                        if(!nAccBalList.TokenAccounts.Exists(x => x.SmartContractUID == tknAcc.SmartContractUID))
                                                            nAccBalList.TokenAccounts.Add(tknAcc);
                                                    }
                                                }
                                                else
                                                {
                                                    var rAccount = ReserveAccount.GetReserveAccountSingle(scState.OwnerAddress);
                                                    if (rAccount != null)
                                                    {
                                                        var scFeature = sc.Features.Where(x => x.FeatureName == FeatureName.Token).FirstOrDefault();
                                                        var scTokenFeature = (TokenFeature)scFeature.FeatureFeatures;
                                                        var tknAcc = new TokenAccount
                                                        {
                                                            Balance = 0.0M,
                                                            DecimalPlaces = scTokenFeature.TokenDecimalPlaces,
                                                            LockedBalance = 0.0M,
                                                            SmartContractUID = sc.SmartContractUID,
                                                            TokenName = scTokenFeature.TokenName,
                                                            TokenTicker = scTokenFeature.TokenTicker
                                                        };

                                                        var nAccBalList = accountBalanceList.Where(x => x.Address == scState.OwnerAddress).FirstOrDefault();
                                                        if (nAccBalList != null)
                                                        {
                                                            if (!nAccBalList.TokenAccounts.Exists(x => x.SmartContractUID == tknAcc.SmartContractUID))
                                                                nAccBalList.TokenAccounts.Add(tknAcc);
                                                        }
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
                        }
                    }
                }

                return JsonConvert.SerializeObject(new { Success = true, Message = $"Accounts Found", AccountBalances = accountBalanceList });
            }
        }

        /// <summary>
        /// Gets VFX and Token Balances from State for specific address. Local or Remote
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("GetStateBalance/{address}")]
        public async Task<string> GetStateBalance(string address)
        {
            var stateAccount = StateData.GetSpecificAccountStateTrei(address);
            List<TokenAccount> tokenAccounts = new List<TokenAccount>();

            if (stateAccount == null)
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Account not found." });

            AccountBalance accountBalance = new AccountBalance
            {
                Address = stateAccount.Key,
                RBXBalance = stateAccount.Balance,
                RBXLockedBalance= stateAccount.LockedBalance,
                TokenAccounts = stateAccount.TokenAccounts?.Count > 0 ? stateAccount.TokenAccounts : tokenAccounts
            };

            return JsonConvert.SerializeObject(new { Success = true, Message = $"Account Found", AccountBalance = accountBalance });

        }

        /// <summary>
        /// Get Adnr from Address
        /// </summary>
        /// <param name="adnr"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("ResolveAdnr/{**adnr}")]
        public async Task<string> ResolveAdnr(string adnr)
        {
            if(!string.IsNullOrEmpty(adnr))
            {
                if (adnr.ToLower().Contains(".vfx") || adnr.ToLower().Contains(".rbx"))
                {
                    var rbxAdnr = Adnr.GetAddress(adnr.ToLower());
                    if(rbxAdnr.Item1)
                    {
                        return JsonConvert.SerializeObject(new { Success = true, Message = $"ADNR Resolved", Address = rbxAdnr.Item2 });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"ADNR Could not be resolved for VFX" });
                    }
                }

                if(adnr.ToLower().Contains(".btc"))
                {
                    var btcAdnr = BitcoinAdnr.GetAddress(adnr.ToLower());
                    if (btcAdnr.Item1)
                    {
                        return JsonConvert.SerializeObject(new { Success = true, Message = $"ADNR Resolved", Address = btcAdnr.Item2 });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"ADNR Could not be resolved for BTC" });
                    }
                }
            }
            
            return JsonConvert.SerializeObject(new { Success = false, Message = $"ADNR Could not be resolved." });
        }

        /// <summary>
        /// Get adnr from address
        /// </summary>
        /// <param name="address"></param>
        /// <param name="asset"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("ResolveAddressAdnr/{address}/{asset}")]
        public async Task<string> ResolveAddressAdnr(string address, string asset)
        {
            if (!string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(asset))
            {
                if (asset.ToLower() == "rbx")
                {
                    var rbxAdnr = Adnr.GetAdnr(address);
                    if (!string.IsNullOrEmpty(rbxAdnr))
                    {
                        return JsonConvert.SerializeObject(new { Success = true, Message = $"Address Resolved", Adnr = rbxAdnr });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"Address Could not be resolved for VFX" });
                    }
                }

                if (asset.ToLower() == "btc")
                {
                    var btcAdnr = BitcoinAdnr.GetAdnr(address);
                    if (!string.IsNullOrEmpty(btcAdnr))
                    {
                        return JsonConvert.SerializeObject(new { Success = true, Message = $"Address Resolved", Adnr = btcAdnr });
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new { Success = false, Message = $"Address Could not be resolved for BTC" });
                    }
                }
            }

            return JsonConvert.SerializeObject(new { Success = false, Message = $"Address Could not be resolved." });
        }

        /// <summary>
        /// Get Validator Winning Proofs
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetWinningProofs")]
        public async Task<string> GetWinningProofs()
        {
            if(Globals.WinningProofs.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Proofs Found", WinningProofs = Globals.WinningProofs }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = true, Message = $"No Proofs found" });
        }

        /// <summary>
        /// Get Validator Pool
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("ValidatorPool")]
        public async Task<string> ValidatorPool()
        {
            if (Globals.NetworkValidators.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Validators Found", NetworkValidators = Globals.NetworkValidators }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = false, Message = $"No Validators found" });
        }

        /// <summary>
        /// Get ConsensusHeaderQueue Pool
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("ConsensusHeaderQueue")]
        public async Task<string> ConsensusHeaderQueue()
        {
            if (Globals.ConsensusHeaderQueue.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Consensus Queue Found", ConsensusHeaderQueue = Globals.ConsensusHeaderQueue }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = false, Message = $"No Consensus Queue found" });
        }

        /// <summary>
        /// Get ProducerDict Pool
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("Producers")]
        public async Task<string> Producers()
        {
            if (Globals.ProducerDict.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Producers Found", ProducerDict = Globals.ProducerDict }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = false, Message = $"No Producers found" });
        }

        /// <summary>
        /// Get FailedProducerDict Pool
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("FailedProducers")]
        public async Task<string> FailedProducers()
        {
            if (Globals.FailedProducerDict.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Failed Producers Found", FailedProducerDict = Globals.FailedProducerDict }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = false, Message = $"No Failed Producers found" });
        }

        /// <summary>
        /// Get FailedProducers Pool
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("BannedFailedProducers")]
        public async Task<string> BannedFailedProducers()
        {
            if (Globals.FailedProducers.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Failed Producers Found", FailedProducers = Globals.FailedProducers }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = false, Message = $"No Failed Producers found" });
        }

        /// <summary>
        /// Get Validator Backup Proofs
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetBackupProofs")]
        public async Task<string> GetBackupProofs()
        {
            if (Globals.BackupProofs.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Proofs Found", BackupProofs = Globals.BackupProofs }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = true, Message = $"No Proofs found" });
        }

        /// <summary>
        /// Get Validator Winning Proofs
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetFinalizedProofs")]
        public async Task<string> GetFinalizedProofs()
        {
            if (Globals.FinalizedWinner.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Finalized Proofs Found", FinalizedProofs = Globals.FinalizedWinner }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = true, Message = $"No Finalized Proofs found" });
        }

        /// <summary>
        /// Get Next Validator BLock
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetNextValBlock")]
        public async Task<string> GetNextValBlock()
        {
            return JsonConvert.SerializeObject(new { Success = true, Message = $"Block Found", Globals.NextValidatorBlock }, Formatting.Indented);
        }

        /// <summary>
        /// Get Network Block Queue
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetNetworkBlockQueue")]
        public async Task<string> GetNetworkBlockQueue()
        {
            if (Globals.NetworkBlockQueue.Any())
                return JsonConvert.SerializeObject(new { Success = true, Message = $"Blocks Found", FinalizedProofs = Globals.NetworkBlockQueue }, Formatting.Indented);

            return JsonConvert.SerializeObject(new { Success = true, Message = $"No Blocks found" });
        }

        /// <summary>
        /// Get Val List
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetValLists")]
        public async Task<string> GetValLists()
        {
            var networkVals = Globals.NetworkValidators.Values.ToList();
            var valVals = Globals.ValidatorNodes.Values.ToList();

            return JsonConvert.SerializeObject(new { Success = true, Message = $"", NetworkVals = networkVals, ValidatorNodes = valVals }, Formatting.Indented);
        }

        /// <summary>
        /// Get caster round List
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("GetCasterRounds")]
        public async Task<string> GetCasterRounds()
        {
            var rounds = Globals.CasterRoundDict.Values.ToList();

            return JsonConvert.SerializeObject(new { Success = true, Message = $"", CasterRounds = rounds }, Formatting.Indented);
        }


        /// <summary>
        /// Takes compressed base 64 image and decompresses it and returns byte array.
        /// </summary>
        /// <returns></returns>
        [HttpPost("GetImageUncompressedByte")]
        public async Task<string> GetUncompressedByte([FromBody] string jsonData)
        {
            try
            {
                var compressedBase64 = jsonData;

                byte[] compressedBytes = compressedBase64.FromBase64ToByteArray();

                byte[] decompressedBytes = compressedBytes.ToDecompress();

                return JsonConvert.SerializeObject(new { Success = true, Message = "Success", ImageByteArray = decompressedBytes });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex}" });
            }
        }

        /// <summary>
        /// Takes compressed base 64 image and decompresses it and returns base64 string.
        /// </summary>
        /// <returns></returns>
        [HttpPost("GetImageUncompressedBase")]
        public async Task<string> GetImageUncompressedBase([FromBody] string jsonData)
        {
            try
            {
                var compressedBase64 = jsonData;

                byte[] compressedBytes = compressedBase64.FromBase64ToByteArray();

                string decompressedBase64 = compressedBytes.ToDecompress().ToBase64();

                return JsonConvert.SerializeObject(new { Success = true, Message = "Success", ImageBase64 = decompressedBase64 });
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex}" });
            }
        }

    }
}
