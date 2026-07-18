using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System.Text.RegularExpressions;
using BtcServices = ReserveBlockCore.Bitcoin.Services;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    /// <summary>
    /// Bitcoin wallet + tokenized BTC (vBTC v1 arbiter model) REST API.
    /// Parallel reimplementation of Bitcoin/Controllers/BTCV2Controller.cs over the
    /// same service/data layer.
    /// </summary>
    public class BitcoinController : RestBaseController
    {
        #region Accounts

        /// <summary>
        /// Create a new Bitcoin address (returns the new private key material)
        /// </summary>
        [HttpPost("accounts")]
        public IActionResult CreateAccount()
        {
            var account = BitcoinAccount.CreateAddress();

            LogUtility.Log("New Address Created: " + account.Address, "BitcoinController.CreateAccount()");

            return Created(new
            {
                Message = "New Address Added",
                account.Address,
                account.PrivateKey,
                account.WifKey
            });
        }

        /// <summary>
        /// Import a Bitcoin address from a private key (hex or WIF)
        /// </summary>
        [HttpPost("accounts/import")]
        public IActionResult ImportBtcPrivateKey([FromBody] ImportBtcKeyRequest request)
        {
            var scriptPubKeyType = request.AddressFormat?.ToLowerInvariant() switch
            {
                "segwitp2sh" => ScriptPubKeyType.SegwitP2SH,
                "taproot" => ScriptPubKeyType.TaprootBIP86,
                _ => ScriptPubKeyType.Segwit
            };

            // hex key vs WIF (same length heuristic as v1)
            if (request.PrivateKey.Length > 58)
                BitcoinAccount.ImportPrivateKey(request.PrivateKey, scriptPubKeyType);
            else
                BitcoinAccount.ImportPrivateKeyWIF(request.PrivateKey, scriptPubKeyType);

            LogUtility.Log("Key Import Successful.", "BitcoinController.ImportBtcPrivateKey()");

            return Created(new { Message = "New address has been imported." });
        }

        /// <summary>
        /// List Bitcoin accounts. Keys are omitted unless omitKeys=false is passed explicitly
        /// (v1 defaults to including keys; v2 is safe-by-default).
        /// </summary>
        [HttpGet("accounts")]
        public IActionResult GetAccounts([FromQuery] bool omitKeys = true)
        {
            var btcAccounts = BitcoinAccount.GetBitcoinAccounts();

            if (btcAccounts == null)
                return Ok(new { Message = "No accounts found.", BitcoinAccounts = new List<BitcoinAccount>() });

            if (omitKeys)
            {
                btcAccounts.ForEach(x =>
                {
                    x.PrivateKey = "REMOVED";
                    x.WifKey = "REMOVED";
                });
            }

            return Ok(new { BitcoinAccounts = btcAccounts });
        }

        /// <summary>
        /// Get a Bitcoin account by address. Keys omitted unless omitKeys=false.
        /// </summary>
        [HttpGet("accounts/{address}")]
        public IActionResult GetAccount(string address, [FromQuery] bool omitKeys = true)
        {
            var btcAccount = BitcoinAccount.GetBitcoinAccount(address);

            if (btcAccount == null)
                return Fail("NOT_FOUND", "No account found for this address.", 404);

            if (omitKeys)
            {
                btcAccount.PrivateKey = "REMOVED";
                btcAccount.WifKey = "REMOVED";
            }

            return Ok(new { BitcoinAccount = btcAccount });
        }

        /// <summary>
        /// Reset Bitcoin accounts and their UTXOs (rate-limited to once per 5 minutes,
        /// shared with the v1 endpoint via Globals.LastRanBTCReset)
        /// </summary>
        [HttpPost("accounts/reset")]
        public IActionResult ResetAccounts()
        {
            var now = DateTime.Now;
            var nextRun = Globals.LastRanBTCReset.AddMinutes(5);
            if (now < nextRun)
                return Fail("RATE_LIMITED", $"Last ran at: {Globals.LastRanBTCReset}. Cannot reset again until: {nextRun}", 429);

            var btcUtxoDb = BitcoinUTXO.GetBitcoinUTXO();
            if (btcUtxoDb != null)
                btcUtxoDb.DeleteAllSafe();

            var btcAccounts = BitcoinAccount.GetBitcoinAccounts();

            if (btcAccounts?.Count() > 0)
            {
                var btcADb = BitcoinAccount.GetBitcoin();
                if (btcADb != null)
                {
                    foreach (var btcAccount in btcAccounts)
                    {
                        btcAccount.Balance = 0.0M;
                        btcADb.UpdateSafe(btcAccount);
                    }
                }
            }

            _ = ReserveBlockCore.Bitcoin.Bitcoin.AccountCheck();

            Globals.LastRanBTCReset = DateTime.Now;

            return Ok(new { Message = "Bitcoin accounts reset." });
        }

        #endregion

        #region Addresses / transactions (read)

        /// <summary>
        /// UTXO list for an address
        /// </summary>
        [HttpGet("addresses/{address}/utxos")]
        public IActionResult GetAddressUtxos(string address)
        {
            var utxoList = BitcoinUTXO.GetUTXOs(address);

            if (utxoList == null || utxoList.Count == 0)
                return Ok(new { Message = "No UTXOs found for this address.", UTXOs = new List<BitcoinUTXO>() });

            return Ok(new { UTXOs = utxoList });
        }

        /// <summary>
        /// Transaction list for an address
        /// </summary>
        [HttpGet("addresses/{address}/transactions")]
        public IActionResult GetAddressTransactions(string address)
        {
            var txList = BitcoinTransaction.GetTXs(address);

            if (txList?.Count() == 0)
                return Ok(new { Message = "No TXs found for this address.", TXs = new List<BitcoinTransaction>() });

            return Ok(new { TXs = txList });
        }

        /// <summary>
        /// All local Bitcoin transactions.
        /// Parity note: v1's includeTokens=false branch computes a filtered list and then
        /// discards it — every call returns all TXs. Mirrored; the parameter is accepted
        /// for interface parity only.
        /// </summary>
        [HttpGet("transactions")]
        public IActionResult GetTransactions([FromQuery] bool? includeTokens)
        {
            var btcList = BitcoinTransaction.GetAllTXs();

            if (btcList?.Count() == 0)
                return Ok(new { Message = "No TXs found.", TXs = new List<BitcoinTransaction>() });

            return Ok(new { TXs = btcList });
        }

        /// <summary>
        /// Last account sync time and next scheduled check
        /// </summary>
        [HttpGet("sync/last")]
        public IActionResult GetLastAccountSync()
        {
            var nextCheckTime = Globals.BTCAccountLastCheckedDate.AddMinutes(4);
            return Ok(new { LastChecked = Globals.BTCAccountLastCheckedDate, NextCheck = nextCheckTime });
        }

        /// <summary>
        /// BTC chain sync status
        /// </summary>
        [HttpGet("sync/status")]
        public IActionResult GetSyncStatus()
        {
            return Ok(new { BTCSyncing = Globals.BTCSyncing });
        }

        /// <summary>
        /// ElectrumX connection state
        /// </summary>
        [HttpGet("electrumx/state")]
        public IActionResult GetElectrumXState()
        {
            return Ok(new { LastCommunication = Globals.ElectrumXLastCommunication, IsConnected = Globals.ElectrumXConnected });
        }

        #endregion

        #region Transactions (send / fee / broadcast)

        /// <summary>
        /// Send a Bitcoin transaction
        /// </summary>
        [HttpPost("transactions/send")]
        public async Task<IActionResult> SendBtcTransaction([FromBody] BtcSendTransactionRequest request)
        {
            var result = await BtcServices.TransactionService.SendTransaction(
                request.FromAddress, request.ToAddress, request.Amount, request.FeeRate, request.OverrideInternalSend);

            if (!result.Item1)
                return Fail("TX_FAILED", result.Item2);

            return Created(new { Message = result.Item2 });
        }

        /// <summary>
        /// Calculate the fee for a prospective transaction
        /// </summary>
        [HttpGet("transactions/fee")]
        public async Task<IActionResult> CalculateFee([FromQuery] string from, [FromQuery] string to, [FromQuery] decimal amount, [FromQuery] int feeRate)
        {
            var result = await BtcServices.TransactionService.CalcuateFee(from, to, amount, feeRate);

            if (!result.Item1)
                return Fail("FEE_CALC_FAILED", result.Item2);

            return Ok(new { Message = "Fee Calculated", Fee = result.Item2 });
        }

        /// <summary>
        /// Replace a transaction by fee (RBF)
        /// </summary>
        [HttpPost("transactions/{txid}/replace")]
        public async Task<IActionResult> ReplaceBtcByFee(string txid, [FromBody] BtcReplaceByFeeRequest request)
        {
            var result = await BtcServices.TransactionService.ReplaceByFeeTransaction(txid, request.FeeRate);
            return FromLegacyJson(result, "RBF_FAILED", 201);
        }

        /// <summary>
        /// Broadcast a signed transaction hex
        /// </summary>
        [HttpPost("transactions/broadcast")]
        public IActionResult Broadcast([FromBody] BtcBroadcastRequest request)
        {
            var btcTran = NBitcoin.Transaction.Parse(request.Hex, Globals.BTCNetwork);

            if (btcTran == null)
                return Fail("PARSE_FAILED", "Failed to parse tx from hex signature.");

            _ = BtcServices.BroadcastService.BroadcastTx(btcTran);

            return Ok(new { Message = "Broadcasting Transaction." });
        }

        /// <summary>
        /// Rebroadcast a locally known transaction by txid
        /// </summary>
        [HttpPost("transactions/{txid}/rebroadcast")]
        public async Task<IActionResult> Rebroadcast(string txid)
        {
            var btcTransaction = await BitcoinTransaction.GetTX(txid);

            if (btcTransaction == null)
                return Fail("NOT_FOUND", "Could not find transaction locally.", 404);

            if (btcTransaction.Signature == null)
                return Fail("NO_SIGNATURE", "Local TX found, but signed transaction missing.");

            var btcTran = NBitcoin.Transaction.Parse(btcTransaction.Signature, Globals.BTCNetwork);

            if (btcTran == null)
                return Fail("PARSE_FAILED", "Local TX found, but failed to parse tx from hex signature.");

            _ = BtcServices.BroadcastService.BroadcastTx(btcTran);

            return Ok(new { Message = "Broadcasting Transaction Again.", btcTransaction.Hash });
        }

        #endregion

        #region BTC ADNR

        /// <summary>
        /// Create a BTC ADNR and associate it to an address
        /// </summary>
        [HttpPost("adnr")]
        public async Task<IActionResult> CreateAdnr([FromBody] BtcCreateAdnrRequest request)
        {
            var wallet = AccountData.GetSingleAccount(request.Address);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account with address: {request.Address} was not found.", 404);

            var adnr = BitcoinAdnr.GetBitcoinAdnr();
            if (adnr == null)
                return Fail("DB_UNAVAILABLE", "BTC ADNR database unavailable.", 500);

            var adnrAddressCheck = adnr.FindOne(x => x.BTCAddress == request.BtcAddress);
            if (adnrAddressCheck != null)
                return Fail("ADNR_EXISTS", $"This address already has a DNR associated with it: {adnrAddressCheck.Name}", 409);

            var name = request.Name.ToLower();

            var limit = Globals.ADNRLimit;
            if (name.Length > limit)
                return Fail("VALIDATION", "A DNR may only be a max of 65 characters");

            var nameCharCheck = Regex.IsMatch(name, @"^[a-zA-Z0-9]+$");
            if (!nameCharCheck)
                return Fail("VALIDATION", "A DNR may only contain letters and numbers.");

            var nameRBX = name + ".btc";
            var nameCheck = adnr.FindOne(x => x.Name == nameRBX);
            if (nameCheck != null)
                return Fail("ADNR_EXISTS", $"This name already has a DNR associated with it: {nameCheck.Name}", 409);

            var result = await BitcoinAdnr.CreateAdnrTx(request.Address, name, request.BtcAddress);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed to broadcast. Error: {result.Item2}");

            return Created(new { Message = "Transaction has been broadcasted.", Hash = result.Item1.Hash });
        }

        /// <summary>
        /// Transfer a BTC ADNR to another address
        /// </summary>
        [HttpPost("adnr/transfer")]
        public async Task<IActionResult> TransferAdnr([FromBody] BtcTransferAdnrRequest request)
        {
            var adnr = BitcoinAdnr.GetBitcoinAdnr();
            if (adnr == null)
                return Fail("DB_UNAVAILABLE", "BTC ADNR database unavailable.", 500);

            var adnrCheck = adnr.FindOne(x => x.BTCAddress == request.BtcFromAddress);
            if (adnrCheck == null)
                return Fail("NOT_FOUND", "This address does not have a DNR associated with it.", 404);

            var wallet = AccountData.GetSingleAccount(adnrCheck.RBXAddress);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account with address: {adnrCheck.RBXAddress} was not found.", 404);

            var addrVerify = AddressValidateUtility.ValidateAddress(request.ToAddress);
            if (!addrVerify)
                return Fail("INVALID_ADDRESS", "To Address is not a valid VFX address.");

            var toAddrAdnr = adnr.FindOne(x => x.BTCAddress == request.BtcToAddress);
            if (toAddrAdnr != null)
                return Fail("ADNR_EXISTS", "To Address already has adnr associated to it.", 409);

            var result = await BitcoinAdnr.TransferAdnrTx(wallet.Address, request.ToAddress, request.BtcToAddress, request.BtcFromAddress);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed to broadcast. Error: {result.Item2}");

            return Created(new { Message = "Transaction has been broadcasted.", Hash = result.Item1.Hash });
        }

        /// <summary>
        /// Permanently remove a BTC ADNR from an address
        /// </summary>
        [HttpPost("adnr/delete")]
        public async Task<IActionResult> DeleteAdnr([FromBody] BtcDeleteAdnrRequest request)
        {
            var adnr = BitcoinAdnr.GetBitcoinAdnr();
            if (adnr == null)
                return Fail("DB_UNAVAILABLE", "BTC ADNR database unavailable.", 500);

            var adnrCheck = adnr.FindOne(x => x.BTCAddress == request.BtcFromAddress);
            if (adnrCheck == null)
                return Fail("NOT_FOUND", "This address does not have a DNR associated with it.", 404);

            var wallet = AccountData.GetSingleAccount(adnrCheck.RBXAddress);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account with address: {adnrCheck.RBXAddress} was not found.", 404);

            var result = await BitcoinAdnr.DeleteAdnrTx(wallet.Address, request.BtcFromAddress);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed to broadcast. Error: {result.Item2}");

            return Created(new { Message = "Transaction has been broadcasted.", Hash = result.Item1.Hash });
        }

        #endregion

        #region Tokenization (vBTC v1 arbiter model)

        /// <summary>
        /// Tokenize Bitcoin (mints a vBTC v1 arbiter-model token contract)
        /// </summary>
        [HttpPost("tokenize")]
        public async Task<IActionResult> TokenizeBitcoin([FromBody] BTCTokenizePayload payload)
        {
            var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            var tokenizationDetails = await ArbiterService.GetTokenizationDetails(payload.RBXAddress, scUID);

            if (tokenizationDetails.Item1 == "FAIL")
                return Fail("ARBITER_FAILED", tokenizationDetails.Item2);

            var scMain = await BtcServices.TokenizationService.CreateTokenizationScMain(payload.RBXAddress, payload.FileLocation,
                tokenizationDetails.Item1, tokenizationDetails.Item2, payload.Name, payload.Description);

            if (scMain == null)
                return Fail("GENERATION_FAILED", "Failed to generate vBTC token. Please check logs for more.", 500);

            var featureList = payload.Features;
            if (featureList?.Count() > 0)
            {
                var nonMultiAssetFeatures = featureList.Where(x => x.FeatureName != FeatureName.MultiAsset).ToList();
                if (nonMultiAssetFeatures.Any())
                    return Fail("VALIDATION", "vBTC Tokens may only contain multi-asset features.");

                if (scMain.Features != null)
                    scMain.Features.AddRange(payload.Features);
            }

            scMain.SmartContractUID = scUID; // premade scuid

            var createSC = await BtcServices.TokenizationService.CreateTokenizationSmartContract(scMain);

            if (!createSC.Item1)
                return Fail("CONTRACT_WRITE_FAILED", "Failed to write vBTC token contract. Please check logs for more.", 500);

            var publishSc = await BtcServices.TokenizationService.MintSmartContract(createSC.Item2, true, TransactionType.TKNZ_MINT);

            if (!publishSc.Item1)
                return Fail("TX_FAILED", $"Failed To Produce a Valid TX. Reason: {publishSc.Item2}");

            return Created(new { Message = "Transaction Success!", Hash = publishSc.Item2 });
        }

        /// <summary>
        /// Get tokenization details (arbiter deposit address + proof) for a VFX address
        /// </summary>
        [HttpGet("tokenize/details/{vfxAddress}")]
        public async Task<IActionResult> GetTokenizationDetails(string vfxAddress)
        {
            var scUID = Guid.NewGuid().ToString().Replace("-", "") + ":" + TimeUtil.GetTime().ToString();

            var tokenizationDetails = await ArbiterService.GetTokenizationDetails(vfxAddress, scUID);

            if (tokenizationDetails.Item1 == "FAIL")
                return Fail("ARBITER_FAILED", tokenizationDetails.Item2);

            return Ok(new
            {
                SmartContractUID = scUID,
                DepositAddress = tokenizationDetails.Item1,
                ProofJson = tokenizationDetails.Item2.ToBase64()
            });
        }

        /// <summary>
        /// List tokenized BTC contracts
        /// </summary>
        [HttpGet("tokenize/list")]
        public async Task<IActionResult> GetTokenizedList()
        {
            var tknzList = await TokenizedBitcoin.GetTokenizedList();
            return Ok(new { TokenizedList = tknzList });
        }

        /// <summary>
        /// Transfer ownership of a tokenized BTC contract
        /// </summary>
        [HttpPost("tokenize/{scUID}/transfer-ownership")]
        public async Task<IActionResult> TransferOwnership(string scUID, [FromBody] BtcTransferOwnershipRequest request)
        {
            var result = await BtcServices.TokenizationService.TransferOwnership(scUID, request.ToAddress, request.BackupURL ?? "");
            return FromLegacyJson(result, "TRANSFER_OWNERSHIP_FAILED", 201);
        }

        /// <summary>
        /// Transfer tokenized BTC to a VFX address
        /// </summary>
        [HttpPost("transfer")]
        public async Task<IActionResult> TransferBtcCoin([FromBody] BTCTokenizeTransaction payload)
        {
            var result = await BtcServices.TokenizationService.TransferCoin(payload);
            return FromLegacyJson(result, "TRANSFER_FAILED", 201);
        }

        /// <summary>
        /// Transfer tokenized BTC to multiple recipients
        /// </summary>
        [HttpPost("transfer-multi")]
        public async Task<IActionResult> TransferBtcCoinMulti([FromBody] BTCTokenizeTransactionMulti payload)
        {
            var result = await BtcServices.TokenizationService.TransferCoinMulti(payload);
            return FromLegacyJson(result, "TRANSFER_FAILED", 201);
        }

        /// <summary>
        /// Withdraw tokenized BTC to a Bitcoin address
        /// </summary>
        [HttpPost("withdraw")]
        public async Task<IActionResult> WithdrawBtcCoin([FromBody] BTCTokenizeTransaction payload)
        {
            if (string.IsNullOrEmpty(payload.FromAddress))
                return Fail("VALIDATION", "VFX From address cannot be null here.");

            var result = await BtcServices.TokenizationService.WithdrawalCoin(
                payload.FromAddress, payload.ToAddress, payload.SCUID, payload.Amount, payload.ChosenFeeRate);
            return FromLegacyJson(result, "WITHDRAWAL_FAILED", 201);
        }

        /// <summary>
        /// Withdraw tokenized BTC with a pre-signed external request
        /// </summary>
        [HttpPost("withdraw/raw")]
        public async Task<IActionResult> WithdrawBtcCoinRaw([FromBody] BTCTokenizeWithdrawalRaw payload)
        {
            if (string.IsNullOrEmpty(payload.VFXAddress))
                return Fail("VALIDATION", "VFX From address cannot be null here.");

            var result = await BtcServices.TokenizationService.WithdrawalCoin(
                payload.VFXAddress,
                payload.BTCToAddress,
                payload.SmartContractUID,
                payload.Amount,
                payload.Timestamp,
                payload.UniqueId,
                payload.VFXSignature,
                payload.IsTest,
                payload.ChosenFeeRate);
            return FromLegacyJson(result, "WITHDRAWAL_FAILED", 201);
        }

        #endregion

        #region Tokenized balances

        /// <summary>
        /// Tokenized BTC balance for an address in a specific contract
        /// </summary>
        [HttpGet("tokenized-balances/{address}/{scUID}")]
        public async Task<IActionResult> GetTokenizedBalance(string address, string scUID)
        {
            var scState = SmartContractStateTrei.GetSmartContractState(scUID);

            if (scState == null)
                return Fail("NOT_FOUND", $"SC State Missing: {scUID}", 404);

            bool isOwner = address == scState.OwnerAddress;

            if (!isOwner)
            {
                if (scState.SCStateTreiTokenizationTXes != null)
                {
                    var balances = scState.SCStateTreiTokenizationTXes.Where(x => x.FromAddress == address || x.ToAddress == address).ToList();

                    if (balances.Any())
                        return Ok(new { Balance = balances.Sum(x => x.Amount) });
                }
                return Ok(new { Balance = 0.0M });
            }

            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);

            if (sc == null)
            {
                var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);

                if (scMain == null)
                    return Fail("DECOMPILE_FAILED", $"Failed to generate Smart Contract Data: {scUID}", 500);

                sc = scMain;
            }

            if (sc.Features == null)
                return Fail("NO_FEATURES", $"Contract has no features: {scUID}");

            var tknzFeature = sc.Features.Where(x => x.FeatureName == FeatureName.Tokenization).Select(x => x.FeatureFeatures).FirstOrDefault();

            if (tknzFeature == null)
                return Fail("NO_FEATURES", $"Contract missing a tokenization feature: {scUID}");

            var tknz = (TokenizationFeature)tknzFeature;

            if (tknz == null)
                return Fail("NO_FEATURES", $"Token feature error: {scUID}");

            var client = await ReserveBlockCore.Bitcoin.Bitcoin.ElectrumXClient();

            if (client == null)
                return Fail("ELECTRUMX_UNAVAILABLE", "Could not connect to bitcoin network. Please ensure you are not blocking connections to the BTC Network.", 503);

            var btcChainBalance = await client.GetBalance(tknz.DepositAddress, false);

            if (btcChainBalance == null)
                return Ok(new { Balance = 0.0M });

            var btcConfirmedBalance = btcChainBalance.Confirmed / 100_000_000M;

            if (scState.SCStateTreiTokenizationTXes != null)
            {
                var balances = scState.SCStateTreiTokenizationTXes.Where(x => x.FromAddress == address || x.ToAddress == address).ToList();

                if (balances.Any())
                    return Ok(new { Balance = btcConfirmedBalance + balances.Sum(x => x.Amount) });
            }

            return Ok(new { Balance = btcConfirmedBalance });
        }

        /// <summary>
        /// All tokenized BTC balances and contract IDs for an address
        /// </summary>
        [HttpGet("tokenized-balances/{address}")]
        public async Task<IActionResult> GetAllTokenizedBalances(string address)
        {
            var scs = SmartContractStateTrei.GetvBTCSmartContracts(address);
            if (scs == null)
                return Ok(new { TotalBalance = 0.0M, SmartContractList = new List<object>() });

            decimal totalBalance = 0.0M;
            var contractBalanceList = new List<object>();
            var processedContractUIDs = new HashSet<string>();

            var client = await ReserveBlockCore.Bitcoin.Bitcoin.ElectrumXClient();

            if (client == null)
                return Fail("ELECTRUMX_UNAVAILABLE", "Could not connect to bitcoin network. Please ensure you are not blocking connections to the BTC Network.", 503);

            foreach (var scState in scs)
            {
                try
                {
                    string scUID = scState.SmartContractUID;

                    if (processedContractUIDs.Contains(scUID))
                        continue;

                    processedContractUIDs.Add(scUID);

                    bool isOwner = address == scState.OwnerAddress;
                    decimal balance = 0.0M;

                    if (scState.SCStateTreiTokenizationTXes != null)
                    {
                        var transactions = scState.SCStateTreiTokenizationTXes
                            .Where(x => x.FromAddress == address || x.ToAddress == address)
                            .ToList();

                        if (transactions.Any())
                            balance = transactions.Sum(x => x.Amount);

                        if (isOwner)
                        {
                            var sc = SmartContractMain.SmartContractData.GetSmartContract(scUID);
                            if (sc == null)
                                sc = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);

                            if (sc != null && sc.Features != null)
                            {
                                var tknzFeature = sc.Features
                                    .Where(x => x.FeatureName == FeatureName.Tokenization)
                                    .Select(x => x.FeatureFeatures)
                                    .FirstOrDefault();

                                if (tknzFeature != null && tknzFeature is TokenizationFeature tknz)
                                {
                                    if (client != null)
                                    {
                                        var btcChainBalance = await client.GetBalance(tknz.DepositAddress, false);
                                        if (btcChainBalance != null)
                                            balance += btcChainBalance.Confirmed / 100_000_000M;
                                    }
                                }
                            }
                        }

                        totalBalance += balance;
                        contractBalanceList.Add(new
                        {
                            SmartContractUID = scUID,
                            Balance = balance,
                            IsOwner = isOwner
                        });
                    }
                }
                catch (Exception ex)
                {
                    contractBalanceList.Add(new
                    {
                        SmartContractUID = scState.SmartContractUID,
                        Balance = 0.0M,
                        Error = ex.Message
                    });
                }
            }

            return Ok(new
            {
                TotalBalance = totalBalance,
                SmartContractList = contractBalanceList
            });
        }

        #endregion

        #region Utility

        /// <summary>
        /// Default vBTC image (Base64)
        /// </summary>
        [HttpGet("default-image")]
        public IActionResult GetDefaultImage()
        {
            var defaultImageLocation = NFTAssetFileUtility.GetvBTCDefaultLogoLocation();

            if (!System.IO.File.Exists(defaultImageLocation))
                return Fail("NOT_FOUND", $"Could not find file in: {defaultImageLocation}", 404);

            byte[] imageBytes = System.IO.File.ReadAllBytes(defaultImageLocation);
            var imageBase = imageBytes.ToBase64();

            return Ok(new
            {
                EncodingFormat = "base64",
                ImageExtension = "png",
                ImageName = "defaultvBTC.png",
                ImageBase = imageBase
            });
        }

        /// <summary>
        /// Default Bitcoin address type for new accounts
        /// </summary>
        [HttpGet("addresses/type-default")]
        public IActionResult GetDefaultAddressType()
        {
            return Ok(new { AddressType = Globals.ScriptPubKeyType.ToString() });
        }

        #endregion
    }
}
