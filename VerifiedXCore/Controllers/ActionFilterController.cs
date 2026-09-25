using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Controllers
{
    public class ActionFilterController : ActionFilterAttribute
    {
        public static List<string> ApprovedMethodList = new List<string> { "GetDebugInfo", "Mother", "Egg", "CheckStatus", "GetCLIVersion", "GetWalletInfo", "NetworkMetrics", "SyncBalances" };

        public const string LockedWalletMessage = "You must type in your encryption password first!";

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            // Resolve the real controller/action from the descriptor. The old code matched any route
            // VALUE (e.g. a path segment equal to "SendTransaction") and read the action positionally.
            var descriptor = filterContext.ActionDescriptor as ControllerActionDescriptor;
            var controllerName = descriptor?.ControllerName ?? filterContext.RouteData.Values["controller"]?.ToString();
            var actionName = descriptor?.ActionName ?? filterContext.RouteData.Values["action"]?.ToString();

            // Security checks FAIL CLOSED: an exception here used to be swallowed and the request then
            // proceeded as if every check had passed.
            try
            {
                if (Globals.AlwaysRequireAPIPassword == true)
                {
                    var somepass = filterContext.RouteData.Values.ContainsKey("somePassword");
                    if (somepass)
                    {
                        var pass = filterContext.RouteData.Values["somePassword"].ToString();
                        var passCheck = Globals.APIPassword.ToDecrypt(pass);
                        if (passCheck == pass && passCheck != "Fail")
                        {
                            //Allow command to process
                        }
                        else
                        {
                            filterContext.Result = new StatusCodeResult(403);
                        }
                    }
                    else
                    {
                        filterContext.Result = new StatusCodeResult(403);
                    }
                }

                if(Globals.APIToken != null)
                {
                    bool bypass = actionName != null && ApprovedMethodList.Contains(actionName);

                    var apiToken = filterContext.HttpContext.Request.Headers["apitoken"];
                    if(apiToken != Globals.APIToken.ToUnsecureString() && !bypass)
                    {
                        filterContext.Result = new StatusCodeResult(403);
                    }
                }

                // BB-3: default deny while the wallet is encrypted and locked. Only actions listed in
                // LockedWalletPolicy (reads, raw externally-signed relays, unlock/status) may run; every
                // other action — including any route added later — is refused until unlock.
                if (filterContext.Result == null && LockedWalletPolicy.WalletIsLocked() &&
                    !LockedWalletPolicy.IsAllowedWhileLocked(controllerName, actionName))
                {
                    filterContext.HttpContext.Response.StatusCode = 401;
                    filterContext.Result = new UnauthorizedObjectResult(LockedWalletMessage);
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"API security filter failed for {controllerName}/{actionName}: {ex.Message}", "ActionFilterController.OnActionExecuting()");
                filterContext.Result = new StatusCodeResult(500);
                return;
            }

            try
            {
                var actionArguments = filterContext.ActionArguments.Count();

                string actionKeysStr = "";
                if(actionArguments > 0)
                {
                    filterContext.ActionArguments.Keys.ToList().ForEach(x => {
                        actionKeysStr += x + ", ";
                    });
                }

                // VX-12: routes that touch key material (GetAllAddresses, GetAllReserveAccounts,
                // GetBitcoinAccountList) are no longer excluded from the API log.
                List<string> APIExclusionList = new List<string> { "SendBlock", "GetWalletInfo", "GetValidatorAddresses",
                    "GetAllLocalTX", "GetSuccessfulLocalTX", "GetFailedLocalTX", "GetPendingLocalTX", "GetMinedLocalTX", "GetAllTopics",
                    "GetActiveTopics", "GetInactiveTopics", "GetMyTopics", "GetAllSmartContracts", "GetMintedSmartContracts", "CheckStatus",
                    "GetIsWalletEncrypted", "GetMyVotes", "GetSingleSmartContract", "GetNFTAssetLocation", "GetCLIVersion", "CheckPasswordNeeded",
                    "GetBeacons", "GetValidatorInfo", "IsValidating", "NetworkMetrics", "Network", "Height", "LastBlock", "GetDecShop", "GetSummaryChatMessages",
                    "GetAllCollections", "GetSimpleShopChatMessages", "GetDecShopData", "GetShopSpecificAuction", "GetListing", "GetCollectionListings",
                    "GetSmartContractData", "GetBalances", "GetDefaultAddressType", "GetLastAccounySync", "GetTokenizedBTCList", "GetAddressTXList",
                    "GetDefaultAddressType", "GetAddressUTXOList", "GetCurrentSCOwner", "GetBitcoinTXList", "GetElectrumXState" };

                if (actionName == null || !APIExclusionList.Contains(actionName))
                {
                    if (Globals.GUI || Globals.LogAPI)
                        APILogUtility.Log($"API Called: {DateTime.Now.ToString()}. Total Number of Action Arguments: {actionArguments}. Action Keys (Only if Arguments > 0): {actionKeysStr}", $"/{controllerName}/{actionName}");
                }
            }
            catch { /* logging only */ }
        }
    }
}
