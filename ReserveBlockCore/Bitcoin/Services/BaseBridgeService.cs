using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexTypes;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Utilities;
using System.Net.Http;
using System.Numerics;
using System.Text;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Base bridge service for VBTCbV2 (multi-sig mint on Base).
    /// V2: Validators sign attestations; users submit proofs to the contract. No relay key needed.
    /// Configuration is loaded from config.txt via Config.ProcessConfig().
    /// </summary>
    public static class BaseBridgeService
    {
        // ── Configuration (set from config.txt via ProcessConfig) ──
        public static string BaseRpcUrl { get; set; } = "https://sepolia.base.org";
        public static string BaseRpcUrl2 { get; set; } = "";
        public static string BaseRpcUrl3 { get; set; } = "";

        private static string _vbtcV2ContractAddress = "";
        /// <summary>VBTCbV2 proxy on Base (multi-sig mint). Set via BaseBridgeV2Contract in config.txt.</summary>
        public static string VBTCbV2ContractAddress
        {
            get => _vbtcV2ContractAddress;
            set
            {
                _vbtcV2ContractAddress = value?.Trim() ?? "";
                ReserveBlockCore.Globals.VBTCbV2ContractAddress = _vbtcV2ContractAddress;
            }
        }

        /// <summary>Same as <see cref="VBTCbV2ContractAddress"/> (exit watcher / legacy API name).</summary>
        public static string VBTCbContractAddress
        {
            get => VBTCbV2ContractAddress;
            set => VBTCbV2ContractAddress = value;
        }

        public static int BaseChainId { get; set; } = 84532; // default until LoadConfig

        /// <summary>RPC + V2 proxy configured (reads, exit watch, attestation path).</summary>
        public static bool IsEnabled => IsV2MintBridge && !string.IsNullOrWhiteSpace(BaseRpcUrl);

        /// <summary>True when VBTCbV2 proxy is configured.</summary>
        public static bool IsV2MintBridge => !string.IsNullOrWhiteSpace(VBTCbV2ContractAddress);
        /// <summary>RPC + V2 contract — can read vBTC.b balances.</summary>
        public static bool CanReadVbtcToken =>
            IsV2MintBridge && !string.IsNullOrWhiteSpace(BaseRpcUrl);
        /// <summary>RPC available — can read native ETH balance on Base.</summary>
        public static bool CanReadEth => !string.IsNullOrWhiteSpace(BaseRpcUrl);

        /// <summary>Human-readable Base network name (follows Globals.IsTestNet).</summary>
        public static string BaseNetworkDisplayName => ReserveBlockCore.Globals.IsTestNet ? "Base Sepolia" : "Base Mainnet";

        // ── Minimal ERC-20 ABI (read-only) ──
        private const string MINT_ABI = @"[
            {
                ""inputs"": [{ ""internalType"": ""address"", ""name"": ""account"", ""type"": ""address"" }],
                ""name"": ""balanceOf"",
                ""outputs"": [{ ""internalType"": ""uint256"", ""name"": """", ""type"": ""uint256"" }],
                ""stateMutability"": ""view"",
                ""type"": ""function""
            },
            {
                ""inputs"": [],
                ""name"": ""totalSupply"",
                ""outputs"": [{ ""internalType"": ""uint256"", ""name"": """", ""type"": ""uint256"" }],
                ""stateMutability"": ""view"",
                ""type"": ""function""
            },
            {
                ""inputs"": [],
                ""name"": ""decimals"",
                ""outputs"": [{ ""internalType"": ""uint8"", ""name"": """", ""type"": ""uint8"" }],
                ""stateMutability"": ""view"",
                ""type"": ""function""
            }
        ]";

        /// <summary>
        /// Query vBTC.b balance on Base for a given EVM address.
        /// </summary>
        public static async Task<(bool Success, decimal Balance, string Message)> GetBaseBalance(string evmAddress)
        {
            try
            {
                if (!CanReadVbtcToken)
                    return (false, 0, "Base vBTC.b read not configured (set BaseBridgeRpcUrl and BaseBridgeV2Contract in config.txt)");

                var web3 = new Web3(BaseRpcUrl);
                var contract = web3.Eth.GetContract(MINT_ABI, VBTCbV2ContractAddress);

                var balanceFunc = contract.GetFunction("balanceOf");
                var balance = await balanceFunc.CallAsync<BigInteger>(evmAddress);

                // Convert from satoshis (8 decimals) to BTC
                var btcBalance = (decimal)balance / 100_000_000M;
                return (true, btcBalance, "OK");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>
        /// Get total supply of vBTC.b on Base.
        /// </summary>
        public static async Task<(bool Success, decimal TotalSupply, string Message)> GetBaseTotalSupply()
        {
            try
            {
                if (!CanReadVbtcToken)
                    return (false, 0, "Base vBTC.b read not configured");

                var web3 = new Web3(BaseRpcUrl);
                var contract = web3.Eth.GetContract(MINT_ABI, VBTCbV2ContractAddress);

                var supplyFunc = contract.GetFunction("totalSupply");
                var supply = await supplyFunc.CallAsync<BigInteger>();

                var btcSupply = (decimal)supply / 100_000_000M;
                return (true, btcSupply, "OK");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>Native ETH balance on Base (read-only).</summary>
        public static async Task<(bool Success, decimal BalanceEth, string Message)> GetEthBalanceAsync(string evmAddress)
        {
            try
            {
                if (!CanReadEth)
                    return (false, 0, "Base RPC not configured");

                if (string.IsNullOrEmpty(evmAddress) || !evmAddress.StartsWith("0x") || evmAddress.Length != 42)
                    return (false, 0, "Invalid EVM address");

                var web3 = new Web3(BaseRpcUrl);
                var wei = await web3.Eth.GetBalance.SendRequestAsync(evmAddress);
                var eth = Web3.Convert.FromWei(wei.Value);
                return (true, eth, "OK");
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        /// <summary>
        /// Log current bridge configuration status. All values are set from config.txt via ProcessConfig().
        /// </summary>
        public static void LoadConfig()
        {
            Globals.VBTCbV2ContractAddress = VBTCbV2ContractAddress.Trim();
            if (BaseChainId == 0)
                BaseChainId = (int)Globals.BaseEvmChainId;

            if (IsV2MintBridge)
            {
                LogUtility.Log($"[BaseBridge] V2 bridge configured. RPC: {BaseRpcUrl}, Contract: {VBTCbV2ContractAddress}, ChainId: {BaseChainId}, Network: {BaseNetworkDisplayName}",
                    "BaseBridgeService.LoadConfig");
            }
            else if (CanReadEth)
            {
                LogUtility.Log($"[BaseBridge] RPC only (ETH balance). {BaseRpcUrl}, Network: {BaseNetworkDisplayName}",
                    "BaseBridgeService.LoadConfig");
            }
            else
            {
                LogUtility.Log("[BaseBridge] Not configured. Set BaseBridgeRpcUrl and BaseBridgeV2Contract in config.txt.",
                    "BaseBridgeService.LoadConfig");
            }
        }

        public static IEnumerable<string> RpcUrlCandidates()
        {
            if (!string.IsNullOrWhiteSpace(BaseRpcUrl)) yield return BaseRpcUrl;
            if (!string.IsNullOrWhiteSpace(BaseRpcUrl2)) yield return BaseRpcUrl2;
            if (!string.IsNullOrWhiteSpace(BaseRpcUrl3)) yield return BaseRpcUrl3;
        }

        private const string RequiredMintSigsAbi = @"[{""inputs"":[],""name"":""requiredMintSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""}]";

        /// <summary>Reads <c>requiredMintSignatures</c> from VBTCbV2 (defaults to 2 if RPC fails).</summary>
        public static async Task<int> GetRequiredMintSignaturesFromChainAsync()
        {
            if (!IsV2MintBridge) return 2;
            foreach (var url in RpcUrlCandidates())
            {
                try
                {
                    var web3 = new Web3(url);
                    var c = web3.Eth.GetContract(RequiredMintSigsAbi, VBTCbV2ContractAddress);
                    var fn = c.GetFunction("requiredMintSignatures");
                    var v = await fn.CallAsync<BigInteger>();
                    var i = (int)v;
                    return i < 2 ? 2 : i;
                }
                catch { }
            }
            return 2;
        }

        /// <summary>Best-effort: confirms a Base tx receipt exists and succeeded.</summary>
        public static async Task<bool> HasSuccessfulReceiptAsync(string txHash)
        {
            if (string.IsNullOrWhiteSpace(txHash)) return false;
            foreach (var url in RpcUrlCandidates())
            {
                try
                {
                    var web3 = new Web3(url);
                    var r = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    if (r != null && r.Status?.Value == 1)
                        return true;
                }
                catch { }
            }
            return false;
        }
    }
}