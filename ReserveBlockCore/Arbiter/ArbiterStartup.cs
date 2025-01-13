using ImageMagick;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NBitcoin.Policy;
using NBitcoin.Protocol;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.Services;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.SecretSharing.Cryptography;
using ReserveBlockCore.SecretSharing.Math;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;
using System;
using System.Numerics;
using System.Text.Json;
using static ReserveBlockCore.Models.Integrations;

namespace ReserveBlockCore.Arbiter
{
    public class ArbiterStartup
    {
        public IConfiguration Configuration { get; }

        public ArbiterStartup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
            services.Configure<FormOptions>(x =>
            {
                x.ValueLengthLimit = 4 * 1024 * 1024; // 4 MB
                x.MultipartBodyLengthLimit = 4 * 1024 * 1024; // 4 MB
            });
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    // Handle the GET request
                    var ipAddress = context.Connection.RemoteIpAddress?.MapToIPv4().ToString();
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync($"Hello {ipAddress}, this is the server's response!");
                });

                endpoints.MapGet("/getsigneraddress", async context =>
                {
                    // Handle the GET request
                    if (Globals.ArbiterSigningAddress == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        var response = JsonConvert.SerializeObject(new { Success = false, Message = $"No Signing Address" }, Formatting.Indented);
                        await context.Response.WriteAsync(response);
                        return;
                    }
                    else
                    {
                        var response = JsonConvert.SerializeObject(new { Success = true, Message = $"Address Found", Address = $"{Globals.ArbiterSigningAddress.Address}" }, Formatting.Indented);
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        await context.Response.WriteAsync(response);
                        return;
                    }
                });

                endpoints.MapGet("/depositaddress/{address}/{**scUID}", async context =>
                {

                     var address = context.Request.RouteValues["address"] as string;
                     var scUID = context.Request.RouteValues["scUID"] as string;

                     if (string.IsNullOrEmpty(address))
                     {
                         context.Response.StatusCode = StatusCodes.Status400BadRequest;
                         var response = JsonConvert.SerializeObject(new { Success = false, Message = $"No Address" }, Formatting.Indented);
                         await context.Response.WriteAsync(response);
                         return;
                     }

                    // Before calling CreatePublicKeyForArbiter
                    SCLogUtility.Log($"Creating PubKey with inputs:", "ArbiterStartup");
                    SCLogUtility.Log($"SigningKey: {Globals.ArbiterSigningAddress.GetKey}", "ArbiterStartup");
                    SCLogUtility.Log($"SCUID: {scUID}", "ArbiterStartup");
                    var publicKey = BitcoinAccount.CreatePublicKeyForArbiter(Globals.ArbiterSigningAddress.GetKey, scUID);
                    SCLogUtility.Log($"Generated PubKey: {publicKey}", "ArbiterStartup");

                    var message = publicKey + scUID;
                    
                     var signature = Services.SignatureService.CreateSignature(message, Globals.ArbiterSigningAddress.GetPrivKey, Globals.ArbiterSigningAddress.PublicKey);

                     if(signature == "F")
                    
                     {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Signature Failed." }, Formatting.Indented);
                        await context.Response.WriteAsync(response);
                        return;
                    
                     }

                     context.Response.StatusCode = StatusCodes.Status200OK;
                     var requestorResponseJson = JsonConvert.SerializeObject(new { Success = true, Message = $"PubKey created", PublicKey = publicKey, Signature = signature }, Formatting.Indented);
                     await context.Response.WriteAsync(requestorResponseJson);
                     return;
                    
                });

                endpoints.MapPost("/getsignedmultisig",  async context =>
                {
                    // Handle the GET request
                    try
                    {
                        using (var reader = new StreamReader(context.Request.Body))
                        {
                            var body = await reader.ReadToEndAsync();
                            var postData = JsonConvert.DeserializeObject<PostData.MultiSigSigningPostData>(body);
                            if (postData != null)
                            {
                                var result = new
                                {
                                    Transaction = postData.TransactionData,
                                    ScriptCoinList = postData.ScriptCoinListData,
                                    postData.SCUID,
                                    postData.VFXAddress,
                                    postData.Timestamp,
                                    postData.Signature,
                                    postData.Amount,
                                    postData.UniqueId
                                };

                                var timeCheck = TimeUtil.GetTime(-45);

                                if (timeCheck > result.Timestamp)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Timestamp too old." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var sigCheck = Services.SignatureService.VerifySignature(result.VFXAddress, $"{result.VFXAddress}.{result.Timestamp}.{result.UniqueId}", postData.Signature);

                                if (!sigCheck)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Invalid Signature from message: {result.VFXAddress}.{result.Timestamp}" }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var coinsToSpend = result.ScriptCoinList.OrderBy(x => x.ScriptPubKey.ToString()).ToArray();

                                if (coinsToSpend == null || coinsToSpend?.Count() < 1)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"No Coins to Spend" }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                TransactionBuilder builder = Globals.BTCNetwork.CreateTransactionBuilder();

                                var privateKey = BitcoinAccount.CreatePrivateKeyForArbiter(Globals.ArbiterSigningAddress.GetKey, result.SCUID);
                                var pubKey = privateKey.PubKey.ToString();

                                SCLogUtility.Log($"Inputs to key derivation:", "ArbiterStartup");
                                SCLogUtility.Log($"SigningKey: {Globals.ArbiterSigningAddress.GetKey}", "ArbiterStartup");
                                SCLogUtility.Log($"SCUID: {result.SCUID}", "ArbiterStartup");
                                SCLogUtility.Log($"Signing Details:", "ArbiterStartup");
                                SCLogUtility.Log($"Private Key WIF: {privateKey.GetWif(Globals.BTCNetwork)}", "ArbiterStartup");
                                SCLogUtility.Log($"Private Key PubKey: {privateKey.PubKey}", "ArbiterStartup");


                                // Make sure we're using one of the expected keys
                                //if (pubKey != "02f3346721a4af10e87a703e74504f4b422adbf72e9597261a27594bf9c1fa5d4a" &&
                                //    pubKey != "039463ae25ebebdf19be147f95b47702abd5bbf2bf87d4f849f84ccc4ad2002a23")
                                //{
                                //    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                //    context.Response.ContentType = "application/json";
                                //    var response = JsonConvert.SerializeObject(new
                                //    {
                                //        Success = false,
                                //        Message = $"Generated pubkey {pubKey} does not match either of the expected pubkeys in redeem script.\nSigningKey: {Globals.ArbiterSigningAddress.GetKey}\nSCUID: {result.SCUID}"
                                //    }, Formatting.Indented);
                                //    await context.Response.WriteAsync(response);
                                //    return;
                                //}

                                SCLogUtility.Log($"Key Comparison:", "ArbiterStartup");
                                SCLogUtility.Log($"Generated pubkey: {privateKey.PubKey}", "ArbiterStartup");

                                // Extract public keys from the ScriptCoinListData
                                var firstInput = coinsToSpend.FirstOrDefault();
                                if (firstInput != null)
                                {
                                    var redeemScript = Script.FromHex(firstInput.RedeemScript);
                                    var pubKeysFromScript = redeemScript.GetAllPubKeys();

                                    foreach (var key in pubKeysFromScript)
                                    {
                                        SCLogUtility.Log($"Comparing with: {key}", "ArbiterStartup");
                                    }

                                    bool keyMatches = pubKeysFromScript.Any(x => x.ToString() == privateKey.PubKey.ToString());
                                    SCLogUtility.Log($"Key matches: {keyMatches}", "ArbiterStartup");

                                    if (!keyMatches)
                                    {
                                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                        context.Response.ContentType = "application/json";
                                        var response = JsonConvert.SerializeObject(new
                                        {
                                            Success = false,
                                            Message = $"Generated pubkey {privateKey.PubKey} does not match any of the expected pubkeys.\nSigningKey: {Globals.ArbiterSigningAddress.GetKey}\nSCUID: {result.SCUID}"
                                        }, Formatting.Indented);
                                        await context.Response.WriteAsync(response);
                                        return;
                                    }
                                }

                                var unsignedTransaction = NBitcoin.Transaction.Parse(result.Transaction, Globals.BTCNetwork);

                                List<ScriptCoin> coinList = new List<ScriptCoin>();

                                //foreach(var input in coinsToSpend)
                                //{
                                //    Script redeemScript = Script.FromHex(input.RedeemScript);
                                //    Script scriptPubKey = Script.FromHex(input.ScriptPubKey);
                                //    OutPoint outPoint = new OutPoint(uint256.Parse(input.TxHash), input.Vout);
                                //    Coin coin = new Coin(outPoint, new TxOut(Money.Coins(input.Money), redeemScript));
                                //    ScriptCoin coinToSpend = new ScriptCoin(coin, scriptPubKey);

                                //    coinList.Add(coinToSpend);
                                //}

                                //Added
                                //Original Above
                                foreach (var input in coinsToSpend)
                                {
                                    Script redeemScript = Script.FromHex(input.RedeemScript);
                                    Script scriptPubKey = Script.FromHex(input.ScriptPubKey);
                                    OutPoint outPoint = new OutPoint(uint256.Parse(input.TxHash), input.Vout);
                                    Coin coin = new Coin(outPoint, new TxOut(Money.Coins(input.Money), scriptPubKey)); // Changed from redeemScript to scriptPubKey
                                    ScriptCoin coinToSpend = new ScriptCoin(coin, redeemScript);  // Changed from scriptPubKey to redeemScript

                                    coinList.Add(coinToSpend);
                                }

                                SCLogUtility.Log($"=== DEBUG INFORMATION START ===", "ArbiterStartup");

                                foreach (var input in coinsToSpend)
                                {
                                    SCLogUtility.Log($"Input Script Details:", "ArbiterStartup");
                                    SCLogUtility.Log($"ScriptPubKey (hex): {input.ScriptPubKey}", "ArbiterStartup");
                                    SCLogUtility.Log($"RedeemScript (hex): {input.RedeemScript}", "ArbiterStartup");
                                }

                                // In ArbiterStartup.cs, around where we do the signing, add these debug logs:
                                SCLogUtility.Log($"Debug: Number of coins to sign: {coinList.Count}", "ArbiterStartup");

                                // Log coin details
                                SCLogUtility.Log("Script Details:", "ArbiterStartup");
                                foreach (var coin in coinList)
                                {
                                    if (coin is ScriptCoin scriptCoin)
                                    {
                                        SCLogUtility.Log($"ScriptPubKey: {scriptCoin.ScriptPubKey}", "ArbiterStartup");
                                        SCLogUtility.Log($"RedeemScript: {scriptCoin.Redeem}", "ArbiterStartup");
                                    }
                                }

                                // Log transaction details before signing
                                SCLogUtility.Log($"Debug: Unsigned transaction details:", "ArbiterStartup");
                                SCLogUtility.Log($"Debug: Input count: {unsignedTransaction.Inputs.Count}", "ArbiterStartup");
                                SCLogUtility.Log($"Debug: Output count: {unsignedTransaction.Outputs.Count}", "ArbiterStartup");
                                foreach (var output in unsignedTransaction.Outputs)
                                {
                                    SCLogUtility.Log($"Debug: Output amount: {output.Value}, ScriptPubKey: {output.ScriptPubKey}", "ArbiterStartup");
                                }

                                // Log private key details (safely)
                                SCLogUtility.Log($"Debug: Private key valid: {privateKey != null}", "ArbiterStartup");

                                // Attempt signing
                                //NBitcoin.Transaction keySigned = builder
                                //    .AddCoins(coinList.ToArray())
                                //    .AddKeys(privateKey)
                                //    .SignTransaction(unsignedTransaction);


                                SCLogUtility.Log($"Private Key WIF: {privateKey.GetWif(Globals.BTCNetwork)}", "ArbiterStartup");
                                SCLogUtility.Log($"Private Key PubKey: {privateKey.PubKey}", "ArbiterStartup");

                                // Add coins first with explicit script
                                foreach (var coin in coinList)
                                {
                                    if (coin is ScriptCoin scriptCoin)
                                    {
                                        SCLogUtility.Log($"Adding coin:", "ArbiterStartup");
                                        SCLogUtility.Log($"Script Coin Redeem: {scriptCoin.Redeem}", "ArbiterStartup");
                                        SCLogUtility.Log($"Script Coin RedeemScript Hash: {scriptCoin.Redeem.Hash}", "ArbiterStartup");
                                        SCLogUtility.Log($"Script Coin ScriptPubKey: {scriptCoin.ScriptPubKey}", "ArbiterStartup");
                                    }
                                }

                                // Add coins and key first
                                builder.AddCoins(coinList.ToArray());
                                builder.AddKeys(privateKey);

                                SCLogUtility.Log("Transaction Details:", "ArbiterStartup");
                                SCLogUtility.Log($"Input Count: {unsignedTransaction.Inputs.Count}", "ArbiterStartup");

                                foreach (var input in unsignedTransaction.Inputs)
                                {
                                    SCLogUtility.Log($"Input Script: {input.ScriptSig}", "ArbiterStartup");
                                }

                                var keySigned = builder
                                    .SetSigningOptions(new SigningOptions
                                    {
                                        EnforceLowR = true,
                                        SigHash = SigHash.All
                                    })
                                    .SignTransaction(unsignedTransaction);

                                // Just log what NBitcoin produced
                                foreach (var input in keySigned.Inputs)
                                {
                                    SCLogUtility.Log($"ScriptSig after signing: {input.ScriptSig}", "ArbiterStartup");
                                }

                                TransactionPolicyError[] errors;
                                bool verified = builder.Verify(keySigned, out errors);

                                if (!verified)
                                {
                                    SCLogUtility.Log("Verification Errors:", "ArbiterStartup");
                                    foreach (var error in errors)
                                    {
                                        SCLogUtility.Log($"Error Details: {error}", "ArbiterStartup");
                                    }

                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new
                                    {
                                        Success = false,
                                        Message = $"Transaction signing failed verification: {string.Join(", ", errors.Select(x => x.ToString()))}",
                                        SignedTx = keySigned.ToHex()
                                    }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var scState = SmartContractStateTrei.GetSmartContractState(result.SCUID);

                                if(scState == null)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to find vBTC token at state level." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var totalOwned = 0.0M;

                                if(scState.OwnerAddress != result.VFXAddress)
                                {
                                    if(scState.SCStateTreiTokenizationTXes != null)
                                    {
                                        var vBTCList = scState.SCStateTreiTokenizationTXes.Where(x => x.ToAddress == result.VFXAddress && x.FromAddress == result.VFXAddress).ToList();
                                        if(vBTCList.Count() > 0)
                                        {
                                            //Also get the state level stuff we are saving now.
                                            totalOwned = vBTCList.Sum(x => x.Amount);
                                            if(totalOwned < postData.Amount)
                                            {
                                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                                context.Response.ContentType = "application/json";
                                                var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Insufficient Balance" }, Formatting.Indented);
                                                await context.Response.WriteAsync(response);
                                                return;
                                            }
                                        }
                                        else
                                        {
                                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                            context.Response.ContentType = "application/json";
                                            var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Account not found as owner of vBTC." }, Formatting.Indented);
                                            await context.Response.WriteAsync(response);
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                        context.Response.ContentType = "application/json";
                                        var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Account not found as owner of vBTC." }, Formatting.Indented);
                                        await context.Response.WriteAsync(response);
                                        return;
                                    }
                                }
                                else
                                {
                                    //TODO: Do owner balance check here!
                                }

                                var scMain = SmartContractMain.GenerateSmartContractInMemory(scState.ContractData);

                                if(scMain == null )
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to make SC Main at state level." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                if (scMain.Features == null)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"NO SC Features Found." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var tknzFeature = scMain.Features.Where(x => x.FeatureName == FeatureName.Tokenization).Select(x => x.FeatureFeatures).FirstOrDefault();

                                if (tknzFeature == null)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"No Token Feature Found." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var tknz = (TokenizationFeature)tknzFeature;

                                if(tknz == null)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to cast token feature." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var depositAddress = tknz.DepositAddress;

                                bool changeAddressCorrect = false;
                                foreach (var output in keySigned.Outputs)
                                {
                                    var addr = output.ScriptPubKey.GetDestinationAddress(Globals.BTCNetwork);
                                    if(addr != null)
                                    {
                                        if (addr.ToString() == depositAddress)
                                            changeAddressCorrect = true;
                                    }
                                }

                                if(!changeAddressCorrect)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Change address must match deposit address." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var keySignedHex = keySigned.ToHex();

                                var nTokenizedWithdrawal = new TokenizedWithdrawals
                                {
                                    Amount = result.Amount,
                                    IsCompleted = false,
                                    OriginalUniqueId = result.UniqueId,
                                    OriginalRequestTime = result.Timestamp,
                                    OriginalSignature = result.Signature,
                                    RequestorAddress = result.VFXAddress,
                                    SmartContractUID = result.SCUID,
                                    TransactionHash = "0",
                                    WithdrawalRequestType = WithdrawalRequestType.Arbiter,
                                    Timestamp = TimeUtil.GetTime(),
                                    ArbiterUniqueId = RandomStringUtility.GetRandomStringOnlyLetters(16)
                                };

                                var responseData = new ResponseData.MultiSigSigningResponse {
                                    Success = true,
                                    Message = "Transaction Signed",
                                    SignedTransaction = keySignedHex,
                                    TokenizedWithdrawals = nTokenizedWithdrawal,
                                };

                                var account = AccountData.GetSingleAccount(Globals.ValidatorAddress);

                                if(account == null )
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Error with Validator/Arb Account." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var wtx = await TokenizationService.CreateTokenizedWithdrawal(nTokenizedWithdrawal, Globals.ValidatorAddress, result.VFXAddress, account, result.SCUID, true);

                                if (wtx.Item1 == null)
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to create withdrawal transaction. Reason: {wtx.Item2}" }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                                var scTx = wtx.Item1;

                                var txresult = await TransactionValidatorService.VerifyTX(scTx);

                                if (txresult.Item1 == true)
                                {
                                    scTx.TransactionStatus = TransactionStatus.Pending;

                                    if (account != null)
                                    {
                                        await WalletService.SendTransaction(scTx, account);
                                    }

                                    context.Response.StatusCode = StatusCodes.Status200OK;
                                    context.Response.ContentType = "application/json";
                                    var requestorResponseJson = JsonConvert.SerializeObject(responseData, Formatting.Indented);
                                    await context.Response.WriteAsync(requestorResponseJson);
                                    return;
                                }
                                else
                                {
                                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                    context.Response.ContentType = "application/json";
                                    var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to create withdrawal transaction." }, Formatting.Indented);
                                    await context.Response.WriteAsync(response);
                                    return;
                                }

                            }
                            else
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                context.Response.ContentType = "application/json";
                                var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Failed to deserialize json" }, Formatting.Indented);
                                await context.Response.WriteAsync(response);
                                return;
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.Response.ContentType = "application/json";
                        var response = JsonConvert.SerializeObject(new { Success = false, Message = $"Error: {ex}" }, Formatting.Indented);
                        await context.Response.WriteAsync(response);
                        return;
                    }
                    


                    

                });

            });
        }
    }
}
