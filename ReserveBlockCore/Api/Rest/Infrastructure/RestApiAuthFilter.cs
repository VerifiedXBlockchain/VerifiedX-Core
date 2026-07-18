using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Extensions;

namespace ReserveBlockCore.Api.Rest.Infrastructure
{
    public class RestApiAuthFilter : ActionFilterAttribute
    {
        private static readonly HashSet<string> TokenBypassActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "GetStatus"
        };

        private static readonly HashSet<string> EncryptionRequiredActions = new(StringComparer.OrdinalIgnoreCase)
        {
            "Send", "CreateAdnr", "TransferAdnr", "DeleteAdnr",
            "ImportKey", "CreateSignature", "CastVote", "CreateTopic",
            "Mint", "Transfer", "Burn", "Evolve", "Devolve",
            "StartSale", "CompleteSale",
            "TransferToken", "BurnToken", "MintToken", "PauseToken",
            "BanAddress", "TransferOwnership",
            // vBTC (VbtcController) actions that sign with the local wallet key
            "CreateVbtcContract", "CreateVbtcContractRaw", "TransferVbtc",
            "RequestVbtcWithdrawal", "CompleteVbtcWithdrawal", "ShieldVbtc",
            "BridgeVbtcToBase",
            // Bitcoin (BitcoinController) actions that sign with local keys or import them
            "SendBtcTransaction", "ReplaceBtcByFee", "TransferBtcCoin",
            "TransferBtcCoinMulti", "WithdrawBtcCoin", "TokenizeBitcoin",
            "ImportBtcPrivateKey"
        };

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var actionName = context.RouteData.Values["action"]?.ToString() ?? "";

            if (Globals.APIToken != null)
            {
                bool bypass = TokenBypassActions.Contains(actionName);
                var headerToken = context.HttpContext.Request.Headers["apitoken"].ToString();
                if (!bypass && headerToken != Globals.APIToken.ToUnsecureString())
                {
                    context.Result = new ObjectResult(
                        ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid or missing API token."))
                    { StatusCode = 403 };
                    return;
                }
            }

            if (Globals.IsWalletEncrypted && Globals.EncryptPassword.Length == 0)
            {
                if (EncryptionRequiredActions.Contains(actionName))
                {
                    context.Result = new ObjectResult(
                        ApiResponse<object>.Fail("WALLET_LOCKED", "Wallet is encrypted and locked. Unlock it first."))
                    { StatusCode = 401 };
                    return;
                }
            }
        }
    }
}
