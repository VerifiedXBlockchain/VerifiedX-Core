using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Bitcoin.Services;

namespace ReserveBlockCore.BrowserWalletServices
{
    /// <summary>
    /// Base (EVM) ETH + vBTC.b balances for BTC accounts with a linked EVM address — mirrors Electrum-style reads.
    /// </summary>
    public static class WalletBtcBaseService
    {
        public static async Task<object> GetLinkedBaseBalancesAsync()
        {
            var accounts = BitcoinAccount.GetBitcoinAccounts();
            if (accounts == null || !accounts.Any())
                return Array.Empty<object>();

            var list = new List<object>();
            foreach (var a in accounts)
            {
                if (string.IsNullOrWhiteSpace(a.LinkedEvmAddress))
                {
                    list.Add(new
                    {
                        btcAddress = a.Address,
                        linkedEvmAddress = (string?)null,
                        ethBalance = (decimal?)null,
                        vbtcBBalance = (decimal?)null,
                        success = false,
                        message = "No linked Base EVM address. Use the Link Base EVM button on this BTC account.",
                        network = BaseBridgeService.BaseNetworkDisplayName,
                        chainId = BaseBridgeService.BaseChainId,
                        canReadEth = BaseBridgeService.CanReadEth,
                        canReadVbtc = BaseBridgeService.CanReadVbtcToken
                    });
                    continue;
                }

                var evm = a.LinkedEvmAddress.Trim();
                var eth = await BaseBridgeService.GetEthBalanceAsync(evm);
                var tok = await BaseBridgeService.GetBaseBalance(evm);

                list.Add(new
                {
                    btcAddress = a.Address,
                    linkedEvmAddress = evm,
                    ethBalance = eth.Success ? eth.BalanceEth : (decimal?)null,
                    ethMessage = eth.Message,
                    vbtcBBalance = tok.Success ? tok.Balance : (decimal?)null,
                    vbtcMessage = tok.Message,
                    success = eth.Success && tok.Success,
                    network = BaseBridgeService.BaseNetworkDisplayName,
                    chainId = BaseBridgeService.BaseChainId,
                    canReadEth = BaseBridgeService.CanReadEth,
                    canReadVbtc = BaseBridgeService.CanReadVbtcToken
                });
            }

            return list;
        }
    }
}
