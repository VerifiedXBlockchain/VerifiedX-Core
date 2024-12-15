﻿using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Utilities;
using System.Numerics;
using ReserveBlockCore.EllipticCurve;
using System.Globalization;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using LiteDB;
using System.Linq;
using ReserveBlockCore.Beacon;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;

namespace ReserveBlockCore.Services
{
    public class ValidatorService
    {
        static SemaphoreSlim ValidatorMonitorServiceLock = new SemaphoreSlim(1, 1);
        static SemaphoreSlim ValidatorCountServiceLock = new SemaphoreSlim(1, 1);

        public static async Task StartValidatorServer()
        {
            try
            {
                string url = "http://*:" + Globals.ValPort;

                if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    var builder = Host.CreateDefaultBuilder()
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseKestrel(options =>
                        {
                            // Configure Kestrel with specific limits
                            options.Limits.MaxConcurrentConnections = 100;
                            options.Limits.MaxConcurrentUpgradedConnections = 100;
                            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
                            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

                            // Configure connection timeouts
                            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
                            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

                            // Configure backpressure
                            options.Limits.MinRequestBodyDataRate = null;
                            options.Limits.MinResponseDataRate = null;

                            // Listen settings
                            options.ListenAnyIP(Globals.ValPort, listenOptions =>
                            {
                                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                                listenOptions.UseConnectionLogging();
                            });
                        })
                        .UseStartup<StartupP2PValidator>()
                        .UseUrls(url)
                        .ConfigureLogging(loggingBuilder =>
                        {
                            loggingBuilder.ClearProviders();
                        });
                        
                    });

                    _ = builder.RunConsoleAsync();

                    //var app = builder.Build();
                    //_ = app.RunAsync();

                    //await Task.Delay(-1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        public static async Task StartupValidatorProcess()
        {
            if (!string.IsNullOrEmpty(Globals.ValidatorAddress))
            {
                while (!Globals.IsChainSynced)
                {
                    await Task.Delay(1000);
                }
                _ = Task.Run(() => { StartValidatorServer(); });
                //_ = ValidatorService.StartValidatorServer();
                _ = StartupValidators();
                _ = Task.Run(BlockHeightCheckLoop);
            }
        }

        internal static async Task StartupValidators()
        {
            //wait 25 seconds
            await Task.Delay(new TimeSpan(0,0,5));
            while (true)
            {
                if (string.IsNullOrEmpty(Globals.ValidatorAddress))
                    return;

                var startupCount = Globals.ValidatorNodes.Count / 2 + 1;
                var delay = Globals.ValidatorNodes.Count < startupCount ? new TimeSpan(0,0,10) : new TimeSpan(0,1,0);

                try
                {
                    var ConnectedCount = Globals.ValidatorNodes.Values.Where(x => x.IsConnected).Count();
                    if (ConnectedCount < Globals.MaxValPeers)
                        await P2PValidatorClient.ConnectToValidators();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }

                await Task.Delay(delay);
            }
        }
        public static async void DoValidate()
        {
            try
            {
                Console.Clear();
                var accountList = AccountData.GetPossibleValidatorAccounts();
                var accountNumberList = new Dictionary<string, Account>();

                if (accountList.Count() > 0)
                {
                    int count = 1;
                    accountList.ToList().ForEach(x => {
                        accountNumberList.Add(count.ToString(), x);
                        Console.WriteLine("********************************************************************");
                        Console.WriteLine("Please choose an address below to be a validator by typing its # and pressing enter.");

                        Console.WriteLine("\n #" + count.ToString());
                        Console.WriteLine("\nAddress :\n{0}", x.Address);
                        Console.WriteLine("\nAccount Balance:\n{0}", x.Balance);
                        count++;
                    });

                    var walletChoice = await ReadLineUtility.ReadLine();
                    while (walletChoice == "")
                    {
                        Console.WriteLine("You must choose a wallet please. Type a number from above and press enter please.");
                        walletChoice = await ReadLineUtility.ReadLine();
                    }
                    var account = accountNumberList[walletChoice];
                    Console.WriteLine("********************************************************************");
                    Console.WriteLine("The chosen validator address is:");
                    string validatorAddress = account.Address;
                    Console.WriteLine(validatorAddress);
                    Console.WriteLine("Are you sure you want to activate this address as a validator? (Type 'y' for yes and 'n' for no.)");
                    var confirmChoice = await ReadLineUtility.ReadLine();

                    if (confirmChoice == null)
                    {
                        Console.WriteLine("You must only type 'y' or 'n'. Please choose the correct option. (Type 'y' for yes and 'n' for no.)");
                        Console.WriteLine("Returning you to main menu...");
                        await Task.Delay(5000);
                        StartupService.MainMenu();
                    }
                    else if (confirmChoice.ToLower() == "n")
                    {
                        Console.WriteLine("Returning you to main menu in 3 seconds...");
                        await Task.Delay(3000);
                        StartupService.MainMenu();
                    }
                    else if (confirmChoice.ToLower() == "y")
                    {
                        Console.Clear();
                        Console.WriteLine("Please type a unique name for your node to be known by. If you do not want a name leave this blank and one will be assigned. (Ex. NodeSwarm_1, TexasNodes, Node1337, AaronsNode, etc.)");
                        var nodeName = await ReadLineUtility.ReadLine();

                        if (!string.IsNullOrWhiteSpace(nodeName))
                        {
                            var nodeNameCheck = UniqueNameCheck(nodeName);

                            while (nodeNameCheck == false)
                            {
                                Console.WriteLine("Please choose another name as we show that as taken. (Ex. NodeSwarm_1, TexasNodes, Node1337, AaronsNode, etc.)");
                                nodeName = await ReadLineUtility.ReadLine();
                                nodeNameCheck = UniqueNameCheck(nodeName);
                            }

                            var result = await StartValidating(account, nodeName);
                            StartupService.MainMenu();
                            Console.WriteLine(result);
                            Console.WriteLine("Returned to main menu.");
                        }
                    }
                    else
                    {
                        StartupService.MainMenu();
                        Console.WriteLine("Unexpected input detected.");
                        Console.WriteLine("Returned to main menu.");
                    }

                }
                else
                {
                    StartupService.MainMenu();
                    Console.WriteLine("********************************************************************");
                    Console.WriteLine("Insufficient balance to validate.");
                    Console.WriteLine("Returned to main menu.");
                }
            }
            catch (Exception ex) { }
        }
        public static async Task<string> StartValidating(Account account, string uName = "", bool argsPassed = false)
        {
            string output = "";
            Validators validator = new Validators();

            if(Globals.V4Height == Globals.LastBlock.Height + 1)
            {
                await GenesisValidatorStart(account, uName);
                return "Account found and activated as a validator! Thank you for service to the network!";
            }

            var valCount = Globals.Nodes.Values.Where(x => x.IsValidator).Count();
            valCount = valCount == 0 ? Globals.ValidatorNodes.Count : valCount;
            if(valCount == 0)
            {
                return "Validator connection count is currently zero. Please stop and restart wallet to attempt to reconnect to validators."; 
            }    

            if (account == null) 
            {
                return "Account not found locally. Please ensure the account specified is stored locally.";
            }
            else
            {
                var sTreiAcct = StateData.GetSpecificAccountStateTrei(account.Address);

                if (sTreiAcct == null)
                {
                    return "Account not found in the State Trei. Please send funds to desired account and wait for at least 1 confirm.";
                }
                if (sTreiAcct != null && sTreiAcct.Balance < ValidatorRequiredAmount())
                {
                    return $"Account Found, but does not meet the minimum of {ValidatorRequiredAmount()} RBX. Please send funds to get account balance to {Globals.ValidatorRequiredRBX} RBX.";
                }
                if (!string.IsNullOrWhiteSpace(uName) && UniqueNameCheck(uName) == false)
                {
                    return "Unique name has already been taken. Please choose another.";
                }
                if (sTreiAcct != null && sTreiAcct.Balance >= ValidatorRequiredAmount())
                {
                    //validate account with signature check
                    var signature = SignatureService.CreateSignature(account.Address, AccountData.GetPrivateKey(account), account.PublicKey);

                    var verifySig = SignatureService.VerifySignature(account.Address, account.Address, signature);

                    if (verifySig == false)
                    {
                        return "Signature check has failed. Please provide correct private key for public address: " + account.Address;
                    }

                    //need to request validator list from someone. 

                    var accounts = AccountData.GetAccounts();
                    var IsThereValidator = accounts.FindOne(x => x.IsValidating == true);
                    if (IsThereValidator != null)
                    {
                        return "This wallet already has a validator active on it. You can only have 1 validator active per wallet: " + IsThereValidator.Address;
                    }

                    var validatorTable = Validators.Validator.GetAll();

                    var validatorCount = validatorTable.Query().Where(x => x.NodeIP != "SELF").Count();
                    if (validatorCount > 0)
                    {
                        return "Account is already a validator";
                    }
                    else
                    {

                        //add total num of validators to block
                        validator.NodeIP = "SELF"; //this is as new as other users will fill this in once connected
                        validator.Amount = account.Balance;
                        validator.Address = account.Address;
                        validator.EligibleBlockStart = -1;
                        validator.UniqueName = uName == "" ? Guid.NewGuid().ToString() : uName;
                        validator.IsActive = true;
                        validator.Signature = signature;
                        validator.FailCount = 0;
                        validator.Position = validatorTable.FindAll().Count() + 1;
                        validator.NodeReferenceId = BlockchainData.ChainRef;
                        validator.WalletVersion = Globals.CLIVersion;
                        validator.LastChecked = DateTime.UtcNow;

                        validatorTable.InsertSafe(validator);

                        account.IsValidating = true;
                        var accountTable = AccountData.GetAccounts();
                        var saveResult = accountTable.UpdateSafe(account);

                        Globals.ValidatorAddress = validator.Address;
                        Globals.ValidatorPublicKey = account.PublicKey;

                        output = "Account found and activated as a validator! Thank you for service to the network!";

                        if (!argsPassed)
                        {
                            _ = StartValidatorServer();
                            _ = StartupValidators();
                        }

                        //TODO: start performing some looped actions
                    }
                }
                else
                {
                    return "Insufficient balance to validate.";
                }
            }

            return output;
        }

        public static async Task DoMasterNodeStop()
        {
            try
            {
                var accounts = AccountData.GetAccounts();
                var myAccounts = accounts.FindAll().ToList();

                if (myAccounts.Count() > 0)
                {
                    myAccounts.ForEach(x => {
                        x.IsValidating = false;
                    });

                    accounts.UpdateSafe(myAccounts);
                }

                var validators = Validators.Validator.GetAll();
                validators.DeleteAllSafe();

                await P2PValidatorClient.DisconnectValidators();


                Console.WriteLine("Validator database records have been reset.");
            }
            catch (Exception ex)
            {                
                ErrorLogUtility.LogError($"Error Clearing Validator Info. Error message: {ex.ToString()}", "ValidatorService.DoMasterNodeStop()");
            }
        }

        public static async Task<string?> SuspendMasterNode()
        {
            try
            {
                var accounts = AccountData.GetAccounts();
                var valAccount = accounts.Query().Where(x => x.IsValidating == true).FirstOrDefault();

                if (valAccount != null)
                {
                    valAccount.IsValidating = false;
                    accounts.UpdateSafe(valAccount);
                    Globals.ValidatorAddress = "";
                    Globals.ValidatorPublicKey = "";
                    return valAccount.Address;
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error Clearing Validator Info. Error message: {ex.ToString()}", "ValidatorService.DoMasterNodeStop()");
            }

            return null;
        }

        public static async Task UpdateBlockMemory(long height)
        {
            try
            {
                Globals.BlockQueueBroadcasted.TryRemove(height, out _);
                Globals.NetworkBlockQueue.TryRemove(height, out _);
                Globals.BackupProofs.TryRemove(height, out _);
                Globals.WinningProofs.TryRemove(height, out _);
                Globals.FinalizedWinner.TryRemove(height, out _);
            }
            catch(Exception ex) { ErrorLogUtility.LogError($"Error: {ex}", "ValidatorService.UpdateBlockMemory()"); }
        }

        public static async Task UpdateProofBlockHashDictionary(long currentHeight, string hash)
        {
            if(currentHeight % 7 == 0)
            {
                Globals.ProofBlockHashDict.TryAdd(currentHeight + 7, hash);
                if (Globals.ProofBlockHashDict.Count() > 3)
                {
                    var oldestRecord = Globals.ProofBlockHashDict.OrderBy(kv => kv.Key).FirstOrDefault();
                    Globals.ProofBlockHashDict.TryRemove(oldestRecord.Key, out _);
                }
            }
        }

        public static async Task PerformErrorCountCheck()
        {
            if (Globals.AdjNodes.Values.Any(x => x.LastTaskErrorCount > 3))
            {
                var adjNodesWithErrors = Globals.AdjNodes.Values.Where(x => x.LastTaskErrorCount > 3).ToList();
                foreach (var adjNode in adjNodesWithErrors)
                {
                    var result = await ResetAdjConnection(adjNode);
                    if(!result)
                    {
                        ErrorLogUtility.LogError($"Failed to reset Adj connection to: {adjNode.Address} on IP: {adjNode.IpAddress}. See exception above.", "ValidatorService.PerformErrorCountCheck()");
                    }
                }
            }
        }

        public static async Task<bool> ValidatorErrorReset()
        {
            //Disconnect from adj
            try
            {
                await P2PClient.DisconnectAdjudicators();
                //Do a block check to ensure all blocks are present.
                await BlockDownloadService.GetAllBlocks();
                await Task.Delay(500);
                //Reset validator variable.
                StartupService.SetValidator();

                return true;
            }
            catch(Exception ex)
            {
                ErrorLogUtility.LogError($"Error Running ValidatorErrorReset(). Error: {ex.ToString()}", "ValidatorService.ValidatorErrorReset()");
            }

            return false;
        }

        public static async Task<bool> ResetAdjConnection(AdjNodeInfo adjInfo)
        {
            //Disconnect from adj
            try
            {
                await adjInfo.Connection.DisposeAsync();
                Globals.AdjNodes[adjInfo.IpAddress].LastTaskErrorCount = 0;
                Globals.AdjNodes[adjInfo.IpAddress].LastTaskError = false;
                Globals.AdjNodes[adjInfo.IpAddress].LastWinningTaskError = false;
                //Do a block check to ensure all blocks are present.
                await BlockDownloadService.GetAllBlocks();
                await Task.Delay(500);

                return true;
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Error Running ResetAdjConnection(). Error: {ex.ToString()}", "ValidatorService.ResetAdjConnection()");
            }

            return false;
        }


        public static bool ValidateTheValidator(Validators validator)
        {
            bool result = false;
            var sTreiAcct = StateData.GetSpecificAccountStateTrei(validator.Address);

            if (sTreiAcct == null)
            {
                //output = "Account not found in the State Trei. Please send funds to desired account and wait for at least 1 confirm.";
                return result;
            }
            if (sTreiAcct != null && sTreiAcct.Balance < ValidatorService.ValidatorRequiredAmount())
            {
                return result;
            }
            if (!string.IsNullOrWhiteSpace(validator.UniqueName) && UniqueNameCheck(validator.UniqueName) == false)
            {
                //output = "Unique name has already been taken. Please choose another.";
                return result;
            }
            if (sTreiAcct != null && sTreiAcct.Balance >= ValidatorService.ValidatorRequiredAmount())
            {
                result = true; //success
            }

            return result;
        }

        public static async void StopValidating(Validators validator)
        {           
            var accounts = AccountData.GetAccounts();
            var myAccount = accounts.FindOne(x => x.Address == validator.Address);

            myAccount.IsValidating = false;
            accounts.UpdateSafe(myAccount);

            var validators = Validators.Validator.GetAll();
            validators.Delete(validator.Id);

            await P2PValidatorClient.DisconnectValidators();

            ValidatorLogUtility.Log($"Validating has stopped.", "ValidatorService.StopValidating()");
        }

        public static int ValidatorRequiredAmount()
        {
            if(Globals.LastBlock.Height < Globals.V1ValHeight)
            {
                return 1000;
            }
            else if(Globals.LastBlock.Height < Globals.V2ValHeight)
            {
                return 12_000;
            }
            else
            {
                return 50_000;
            }
        }

        public static async void ClearOldValidator()
        {
            try
            {
                var validators = Validators.Validator.GetOldAll();
                var validatorList = validators.FindAll().ToList();

                if (validatorList.Count() > 0)
                {
                    var accounts = AccountData.GetAccounts();
                    var myAccounts = accounts.FindAll().ToList();

                    if (myAccounts.Count() > 0)
                    {
                        myAccounts.ForEach(x => {
                            x.IsValidating = false;
                        });

                        accounts.UpdateSafe(myAccounts);
                    }

                    validators.DeleteAllSafe();

                    Globals.ValidatorAddress = "";
                    Globals.ValidatorPublicKey = "";

                    await P2PClient.DisconnectAdjudicators();
                }

            }
            catch (Exception ex)
            {                
            }
        }

        public static async void ClearDuplicates()
        {
            try
            {
                var validators = Validators.Validator.GetAll();
                var validatorList = validators.FindAll().ToList();

                if(validatorList.Count() > 0)
                {
                    List<Validators> dups = validatorList.GroupBy(x => new {
                        x.Address,
                        x.NodeIP
                    })
                    .Where(x => x.Count() > 1)
                    .Select(x => x.First())
                    .ToList();

                    if (dups.Count() > 0)
                    {
                        dups.ForEach(x =>
                        {
                            var dupList = validatorList.Where(y => y.Address == x.Address && y.NodeIP == x.NodeIP).ToList();
                            if (dupList.Exists(z => z.IsActive == true))
                            {
                                var dupsDel = dupList.Where(z => z.IsActive == false).ToList();
                                validators.DeleteManySafe(z => z.Address == x.Address && z.IsActive == false);
                            }
                            else
                            {
                                var countRem = dupList.Count() - 1;
                                var dupsDel = dupList.Take(countRem);
                                dupsDel.ToList().ForEach(d =>
                                {
                                    validators.DeleteManySafe(p => p.Id == d.Id);
                                });
                            }
                        });
                    }
                }
                
            }
            catch (Exception ex)
            {                
            }
        }

        public static bool UniqueNameCheck(string uName)
        {
            bool output = false;
            var validatorTable = Validators.Validator.GetAll();
            var uNameCount = validatorTable.FindAll().Where(x => x.UniqueName.ToLower() == uName.ToLower()).Count();

            if (uNameCount == 0)
                output = true;

            return output;

        }

        public static async Task ValidatingMonitorService()
        {
            if (Globals.AdjudicateAccount != null)
                return;

            while(true)
            {
                var delay = Task.Delay(120000);

                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }

                if(string.IsNullOrEmpty(Globals.ValidatorAddress))
                {
                    await delay;
                    continue;
                }

                if(Globals.AdjNodes.Count == 0)
                {
                    await delay;
                    continue;
                }

                await ValidatorMonitorServiceLock.WaitAsync();
                try
                {
                    if(Globals.ValidatorLastBlockHeight != 0)
                    {
                        if(Globals.LastBlock.Height - Globals.ValidatorLastBlockHeight <= 2)
                        {
                            //potentiall issue
                            Globals.ValidatorIssueCount += 1;
                            Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Block Heights are behind.");
                        }
                        else
                        {
                            Globals.ValidatorLastBlockHeight = Globals.LastBlock.Height;
                            Globals.ValidatorErrorMessages.RemoveAll(x => x.Contains("Block Heights are behind."));
                        }

                        var valAccount = AccountData.GetSingleAccount(Globals.ValidatorAddress);
                        if(valAccount != null)
                        {
                            if(valAccount.Balance < ValidatorService.ValidatorRequiredAmount())
                            {
                                Globals.ValidatorIssueCount += 1;
                                Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Balance Error. Please ensure you have proper amount.");
                                Globals.ValidatorBalanceGood = false;
                                await DoMasterNodeStop();
                            }
                            else
                            {
                                Globals.ValidatorBalanceGood = true;
                            }
                        }
                        else
                        {
                            Globals.ValidatorIssueCount += 1;
                            Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Validator Account Missing");
                        }

                        if(Globals.TimeSyncError)
                        {
                            Globals.ValidatorIssueCount += 1;
                            Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Node system time is out of sync. Please correct.");
                        }

                        var adjNodes = Globals.AdjNodes.Values.Where(x => x.IsConnected).ToList();
                        if(adjNodes.Count < 1)
                        {
                            Globals.ValidatorIssueCount += 1;
                            Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} ADJ Connections are 1 or less.");
                        }
                        else
                        {
                            Globals.ValidatorErrorMessages.RemoveAll(x => x.Contains("ADJ Connections are 1 or less."));
                            var currentTime = DateTime.Now.AddMinutes(-2);

                            var lastSendTime = adjNodes.Max(x => x.LastTaskSentTime);
                            var lastReceiveTime = adjNodes.Max(x => x.LastTaskResultTime);

                            if(lastSendTime != null && lastReceiveTime != null)
                            {
                                if (lastSendTime < currentTime) 
                                { 
                                    Globals.ValidatorSending = false; 
                                    Globals.ValidatorIssueCount += 1; 
                                    Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Behind on sending Task to ADJs."); 
                                }
                                else 
                                { 
                                    Globals.ValidatorSending = true; 
                                    Globals.ValidatorErrorMessages.RemoveAll(x => x.Contains("Behind on sending Task to ADJs.")); 
                                }

                                if (lastReceiveTime < currentTime) 
                                { 
                                    Globals.ValidatorReceiving = false; 
                                    Globals.ValidatorIssueCount += 1;
                                    Globals.ValidatorErrorMessages.Add($"Time: {DateTime.Now} Behind on receiving Task Answers from ADJs.");
                                }
                                else 
                                { 
                                    Globals.ValidatorReceiving = true; 
                                    Globals.ValidatorErrorMessages.RemoveAll(x => x.Contains("Behind on receiving Task Answers from ADJs.")); 
                                }
                            }
                            else
                            {
                                if (lastSendTime == null)
                                    Globals.ValidatorSending = false;
                                if (lastReceiveTime == null)
                                    Globals.ValidatorReceiving = false;
                                //send variable to false that sending and receiving is not happening.
                                Globals.ValidatorIssueCount += 1;
                            }
                        }
                    }
                    else
                    {
                        Globals.ValidatorLastBlockHeight = Globals.LastBlock.Height;
                    }

                    if(Globals.ValidatorIssueCount >= 10)
                    {
                        ConsoleWriterService.OutputMarked("[red]Validator has had the following issues to report. Please ensure node is operating correctly[/]");
                        foreach (var issue in Globals.ValidatorErrorMessages)
                        {
                            ConsoleWriterService.OutputMarked($"[yellow]{issue}[/]");
                        }

                        Globals.ValidatorIssueCount = 0;
                    }
                }
                finally
                {
                    ValidatorMonitorServiceLock.Release();
                }

                await delay;
            }
        }

        public static async Task ValidatorCountRun()
        {
            while (true)
            {
                var delay = Task.Delay(new TimeSpan(0,10,0));
                if (Globals.StopAllTimers && !Globals.IsChainSynced)
                {
                    await delay;
                    continue;
                }
                await ValidatorCountServiceLock.WaitAsync();
                try
                {
                    var startDate = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
                    var valsToRemove = Globals.ActiveValidatorDict.Where(x => x.Value < startDate).Select(x => x.Key).ToList();
                    if(valsToRemove.Any())
                    {
                        foreach (var val in valsToRemove)
                        {
                            Globals.ActiveValidatorDict.TryRemove(val, out _);
                        }
                    }
                }
                finally
                {
                    ValidatorCountServiceLock.Release();
                }

                await delay;
            }
        }

        public static async Task GetActiveValidators()
        {
            var startDate = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
            var blockSub = 3456 * 30;
            var lastHeight = Globals.LastBlock.Height;
            var startHeight = lastHeight - blockSub;

            //temporarily removed due to memory constraints. 
            //var startBlock = BlockchainData.GetBlocks().Find(Query.All(Query.Descending)).Where(x => x.Timestamp <= startDate).FirstOrDefault();

            if(startHeight > 0)
            {
                var currentHeight = lastHeight;
                for (long i = startHeight; i < currentHeight; i++)
                {
                    var block = BlockchainData.GetBlocks().Query().Where(x => x.Height == i).FirstOrDefault();
                    if (block != null)
                    {
                        if (!Globals.ActiveValidatorDict.ContainsKey(block.Validator))
                            Globals.ActiveValidatorDict.TryAdd(block.Validator, block.Timestamp);
                        else
                            Globals.ActiveValidatorDict[block.Validator] = block.Timestamp;
                    }
                }
            }
        }

        public static async Task UpdateActiveValidators(Block? block)
        {
            try
            {
                if (block != null)
                {
                    if (!Globals.ActiveValidatorDict.ContainsKey(block.Validator))
                        Globals.ActiveValidatorDict.TryAdd(block.Validator, block.Timestamp);
                    else
                        Globals.ActiveValidatorDict[block.Validator] = block.Timestamp;
                }
            }
            catch(Exception ex) { ErrorLogUtility.LogError($"Error: {ex}", "ValidatorService.UpdateActiveValidators()"); }
            
        }

        private static async Task<string> GenesisValidatorStart(Account account, string uName = "")
        {
            string output = "";
            Validators validator = new Validators();

            if (account == null)
            {
                return "Account not found locally. Please ensure the account specified is stored locally.";
            }
            else
            {
                var sTreiAcct = StateData.GetSpecificAccountStateTrei(account.Address);

                if (sTreiAcct == null)
                {
                    return "Account not found in the State Trei. Please send funds to desired account and wait for at least 1 confirm.";
                }
                if (sTreiAcct != null && sTreiAcct.Balance < ValidatorRequiredAmount())
                {
                    return $"Account Found, but does not meet the minimum of {ValidatorRequiredAmount()} RBX. Please send funds to get account balance to {Globals.ValidatorRequiredRBX} RBX.";
                }
                if (!string.IsNullOrWhiteSpace(uName) && UniqueNameCheck(uName) == false)
                {
                    return "Unique name has already been taken. Please choose another.";
                }
                if (sTreiAcct != null && sTreiAcct.Balance >= ValidatorRequiredAmount())
                {
                    //validate account with signature check
                    var signature = SignatureService.CreateSignature(account.Address, AccountData.GetPrivateKey(account), account.PublicKey);

                    var verifySig = SignatureService.VerifySignature(account.Address, account.Address, signature);

                    if (verifySig == false)
                    {
                        return "Signature check has failed. Please provide correct private key for public address: " + account.Address;
                    }

                    //need to request validator list from someone. 

                    var accounts = AccountData.GetAccounts();
                    var IsThereValidator = accounts.FindOne(x => x.IsValidating == true);
                    if (IsThereValidator != null)
                    {
                        return "This wallet already has a validator active on it. You can only have 1 validator active per wallet: " + IsThereValidator.Address;
                    }

                    var validatorTable = Validators.Validator.GetAll();

                    var validatorCount = validatorTable.Query().Where(x => x.NodeIP != "SELF").Count();
                    if (validatorCount > 0)
                    {
                        return "Account is already a validator";
                    }
                    else
                    {

                        //add total num of validators to block
                        validator.NodeIP = "SELF"; //this is as new as other users will fill this in once connected
                        validator.Amount = account.Balance;
                        validator.Address = account.Address;
                        validator.EligibleBlockStart = -1;
                        validator.UniqueName = uName == "" ? Guid.NewGuid().ToString() : uName;
                        validator.IsActive = true;
                        validator.Signature = signature;
                        validator.FailCount = 0;
                        validator.Position = validatorTable.FindAll().Count() + 1;
                        validator.NodeReferenceId = BlockchainData.ChainRef;
                        validator.WalletVersion = Globals.CLIVersion;
                        validator.LastChecked = DateTime.UtcNow;

                        validatorTable.InsertSafe(validator);

                        account.IsValidating = true;
                        var accountTable = AccountData.GetAccounts();
                        var saveResult = accountTable.UpdateSafe(account);

                        Globals.ValidatorAddress = validator.Address;
                        Globals.ValidatorPublicKey = account.PublicKey;

                        output = "Account found and activated as a validator! Thank you for service to the network!";

                        _ = StartValidatorServer();
                        _ = StartupValidators();

                    }
                }
                else
                {
                    return "Insufficient balance to validate.";
                }
            }

            return output;
        }

        #region Block Height Check
        public static async Task BlockHeightCheckLoop()
        {
            while (true)
            {
                try
                {
                    while (!Globals.ValidatorNodes.Any())
                        await Task.Delay(20);

                    await P2PValidatorClient.UpdateNodeHeights();

                    var maxHeight = Globals.ValidatorNodes.Values.Select(x => x.NodeHeight).OrderByDescending(x => x).FirstOrDefault();
                    if (maxHeight > Globals.LastBlock.Height)
                    {
                        P2PValidatorClient.UpdateMaxHeight(maxHeight);
                        _ = BlockDownloadService.GetAllBlocks();
                    }
                    else
                        P2PValidatorClient.UpdateMaxHeight(maxHeight);

                    var MaxHeight = P2PValidatorClient.MaxHeight();

                    foreach (var node in Globals.ValidatorNodes.Values)
                    {
                        if (node.NodeHeight < MaxHeight - 3)
                            await P2PValidatorClient.RemoveNode(node);
                    }

                }
                catch { }

                await Task.Delay(10000);
            }
        }

        #endregion

    }
}
