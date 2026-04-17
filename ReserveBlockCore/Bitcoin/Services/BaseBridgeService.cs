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

        private static string _vbtcV3ContractAddress = "";
        /// <summary>V3 contract address (pool-based burnForVfxExit). Set via BASE_BRIDGE_V3_CONTRACT env var.</summary>
        public static string VBTCbV3ContractAddress
        {
            get => _vbtcV3ContractAddress;
            set => _vbtcV3ContractAddress = value?.Trim() ?? "";
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

        // ── Full VBTCbV2 contract ABI (sourced from docs/contract_abi.txt) ──
        public const string CONTRACT_ABI = @"[{""inputs"":[{""internalType"":""address"",""name"":""target"",""type"":""address""}],""name"":""AddressEmptyCode"",""type"":""error""},{""inputs"":[],""name"":""ECDSAInvalidSignature"",""type"":""error""},{""inputs"":[{""internalType"":""uint256"",""name"":""length"",""type"":""uint256""}],""name"":""ECDSAInvalidSignatureLength"",""type"":""error""},{""inputs"":[{""internalType"":""bytes32"",""name"":""s"",""type"":""bytes32""}],""name"":""ECDSAInvalidSignatureS"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""implementation"",""type"":""address""}],""name"":""ERC1967InvalidImplementation"",""type"":""error""},{""inputs"":[],""name"":""ERC1967NonPayable"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""spender"",""type"":""address""},{""internalType"":""uint256"",""name"":""allowance"",""type"":""uint256""},{""internalType"":""uint256"",""name"":""needed"",""type"":""uint256""}],""name"":""ERC20InsufficientAllowance"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""sender"",""type"":""address""},{""internalType"":""uint256"",""name"":""balance"",""type"":""uint256""},{""internalType"":""uint256"",""name"":""needed"",""type"":""uint256""}],""name"":""ERC20InsufficientBalance"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""approver"",""type"":""address""}],""name"":""ERC20InvalidApprover"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""receiver"",""type"":""address""}],""name"":""ERC20InvalidReceiver"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""sender"",""type"":""address""}],""name"":""ERC20InvalidSender"",""type"":""error""},{""inputs"":[{""internalType"":""address"",""name"":""spender"",""type"":""address""}],""name"":""ERC20InvalidSpender"",""type"":""error""},{""inputs"":[],""name"":""FailedCall"",""type"":""error""},{""inputs"":[],""name"":""InvalidInitialization"",""type"":""error""},{""inputs"":[],""name"":""NotInitializing"",""type"":""error""},{""inputs"":[],""name"":""UUPSUnauthorizedCallContext"",""type"":""error""},{""inputs"":[{""internalType"":""bytes32"",""name"":""slot"",""type"":""bytes32""}],""name"":""UUPSUnsupportedProxiableUUID"",""type"":""error""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""owner"",""type"":""address""},{""indexed"":true,""internalType"":""address"",""name"":""spender"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""value"",""type"":""uint256""}],""name"":""Approval"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""burner"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""indexed"":false,""internalType"":""string"",""name"":""btcDestination"",""type"":""string""},{""indexed"":false,""internalType"":""uint256"",""name"":""chainId"",""type"":""uint256""}],""name"":""BTCExitBurned"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""newImplementation"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""adminNonce"",""type"":""uint256""}],""name"":""ContractUpgraded"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""burner"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""indexed"":false,""internalType"":""string"",""name"":""vfxLockId"",""type"":""string""},{""indexed"":false,""internalType"":""uint256"",""name"":""chainId"",""type"":""uint256""}],""name"":""ExitBurned"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":false,""internalType"":""uint64"",""name"":""version"",""type"":""uint64""}],""name"":""Initialized"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""to"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""indexed"":false,""internalType"":""string"",""name"":""lockId"",""type"":""string""},{""indexed"":false,""internalType"":""uint256"",""name"":""nonce"",""type"":""uint256""}],""name"":""MintExecuted"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""from"",""type"":""address""},{""indexed"":true,""internalType"":""address"",""name"":""to"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""value"",""type"":""uint256""}],""name"":""Transfer"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""implementation"",""type"":""address""}],""name"":""Upgraded"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""validator"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""}],""name"":""ValidatorAdded"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":false,""internalType"":""address[]"",""name"":""validators"",""type"":""address[]""},{""indexed"":false,""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""}],""name"":""ValidatorBatchAdded"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":false,""internalType"":""address[]"",""name"":""validators"",""type"":""address[]""},{""indexed"":false,""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""}],""name"":""ValidatorBatchRemoved"",""type"":""event""},{""anonymous"":false,""inputs"":[{""indexed"":true,""internalType"":""address"",""name"":""validator"",""type"":""address""},{""indexed"":false,""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""}],""name"":""ValidatorRemoved"",""type"":""event""},{""inputs"":[],""name"":""MIN_REQUIRED_SIGNATURES"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""MIN_VALIDATORS_FOR_HIGH_THRESHOLD"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""UPGRADE_INTERFACE_VERSION"",""outputs"":[{""internalType"":""string"",""name"":"""",""type"":""string""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""newValidator"",""type"":""address""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""addValidator"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""address[]"",""name"":""newValidators"",""type"":""address[]""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""addValidatorBatch"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[],""name"":""adminNonce"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""owner"",""type"":""address""},{""internalType"":""address"",""name"":""spender"",""type"":""address""}],""name"":""allowance"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""spender"",""type"":""address""},{""internalType"":""uint256"",""name"":""value"",""type"":""uint256""}],""name"":""approve"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""account"",""type"":""address""}],""name"":""balanceOf"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""internalType"":""string"",""name"":""btcDestination"",""type"":""string""}],""name"":""burnForBTCExit"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""internalType"":""string"",""name"":""vfxLockId"",""type"":""string""}],""name"":""burnForExit"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[],""name"":""decimals"",""outputs"":[{""internalType"":""uint8"",""name"":"""",""type"":""uint8""}],""stateMutability"":""pure"",""type"":""function""},{""inputs"":[],""name"":""getAdminNonce"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""getValidators"",""outputs"":[{""internalType"":""address[]"",""name"":"""",""type"":""address[]""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""string"",""name"":""name_"",""type"":""string""},{""internalType"":""string"",""name"":""symbol_"",""type"":""string""},{""internalType"":""address[]"",""name"":""initialValidators"",""type"":""address[]""}],""name"":""initialize"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""string"",""name"":""lockId"",""type"":""string""}],""name"":""isLockIdUsed"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":"""",""type"":""address""}],""name"":""isValidator"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""to"",""type"":""address""},{""internalType"":""uint256"",""name"":""amount"",""type"":""uint256""},{""internalType"":""string"",""name"":""lockId"",""type"":""string""},{""internalType"":""uint256"",""name"":""nonce"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""mintWithProof"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[],""name"":""name"",""outputs"":[{""internalType"":""string"",""name"":"""",""type"":""string""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""proxiableUUID"",""outputs"":[{""internalType"":""bytes32"",""name"":"""",""type"":""bytes32""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""oldValidator"",""type"":""address""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""removeValidator"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""address[]"",""name"":""oldValidators"",""type"":""address[]""},{""internalType"":""uint256"",""name"":""vfxBlockHeight"",""type"":""uint256""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""removeValidatorBatch"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[],""name"":""requiredMintSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""requiredRemoveSignatures"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""symbol"",""outputs"":[{""internalType"":""string"",""name"":"""",""type"":""string""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""totalSupply"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""to"",""type"":""address""},{""internalType"":""uint256"",""name"":""value"",""type"":""uint256""}],""name"":""transfer"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""from"",""type"":""address""},{""internalType"":""address"",""name"":""to"",""type"":""address""},{""internalType"":""uint256"",""name"":""value"",""type"":""uint256""}],""name"":""transferFrom"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""newImplementation"",""type"":""address""},{""internalType"":""bytes"",""name"":""data"",""type"":""bytes""}],""name"":""upgradeToAndCall"",""outputs"":[],""stateMutability"":""payable"",""type"":""function""},{""inputs"":[{""internalType"":""address"",""name"":""newImplementation"",""type"":""address""},{""internalType"":""bytes[]"",""name"":""signatures"",""type"":""bytes[]""}],""name"":""upgradeWithValidatorApproval"",""outputs"":[],""stateMutability"":""nonpayable"",""type"":""function""},{""inputs"":[{""internalType"":""bytes32"",""name"":"""",""type"":""bytes32""}],""name"":""usedLockIds"",""outputs"":[{""internalType"":""bool"",""name"":"""",""type"":""bool""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[],""name"":""validatorCount"",""outputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""stateMutability"":""view"",""type"":""function""},{""inputs"":[{""internalType"":""uint256"",""name"":"""",""type"":""uint256""}],""name"":""validators"",""outputs"":[{""internalType"":""address"",""name"":"""",""type"":""address""}],""stateMutability"":""view"",""type"":""function""}]";

        // ── Read-only balance ABI (subset used by GetBaseBalance / GetBaseTotalSupply) ──
        private const string BALANCE_ABI = @"[
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
                var contract = web3.Eth.GetContract(BALANCE_ABI, VBTCbV2ContractAddress);

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
                var contract = web3.Eth.GetContract(BALANCE_ABI, VBTCbV2ContractAddress);

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

        /// <summary>Reads <c>requiredMintSignatures</c> from VBTCbV2 (defaults to 2 if RPC fails).</summary>
        public static async Task<int> GetRequiredMintSignaturesFromChainAsync()
        {
            if (!IsV2MintBridge) return 2;
            foreach (var url in RpcUrlCandidates())
            {
                try
                {
                    var web3 = new Web3(url);
                    var c = web3.Eth.GetContract(CONTRACT_ABI, VBTCbV2ContractAddress);
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