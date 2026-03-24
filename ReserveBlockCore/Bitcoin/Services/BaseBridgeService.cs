using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Hex.HexTypes;
using Newtonsoft.Json;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Utilities;
using System.Numerics;

namespace ReserveBlockCore.Bitcoin.Services
{
    /// <summary>
    /// Bridges vBTC from VerifiedX to Base by calling mint() on the VBTCb ERC-20 contract.
    /// For demo: the relay node is the contract owner and calls mint directly.
    /// Production: would submit FROST-signed proofs to a CanonicalGateway contract.
    /// </summary>
    public static class BaseBridgeService
    {
        // ── Configuration (set via Globals.IsTestNet + environment) ──
        public static string BaseRpcUrl { get; set; } = "https://sepolia.base.org";
        public static string VBTCbContractAddress { get; set; } = "";
        public static string RelayPrivateKey { get; set; } = "";
        public static int BaseChainId { get; set; } = 84532; // default until LoadConfig
        /// <summary>Contract + relay key — can submit mint txs.</summary>
        public static bool IsEnabled => !string.IsNullOrEmpty(VBTCbContractAddress) && !string.IsNullOrEmpty(RelayPrivateKey);
        /// <summary>RPC + token contract — can read vBTC.b balances (no relay key).</summary>
        public static bool CanReadVbtcToken => !string.IsNullOrWhiteSpace(VBTCbContractAddress) && !string.IsNullOrWhiteSpace(BaseRpcUrl);
        /// <summary>RPC available — can read native ETH balance on Base.</summary>
        public static bool CanReadEth => !string.IsNullOrWhiteSpace(BaseRpcUrl);

        /// <summary>Human-readable Base network name (follows Globals.IsTestNet).</summary>
        public static string BaseNetworkDisplayName => ReserveBlockCore.Globals.IsTestNet ? "Base Sepolia" : "Base Mainnet";

        // ── Minimal ERC-20 + mint ABI ──
        // function mint(address to, uint256 amount) external
        private const string MINT_ABI = @"[
            {
                ""inputs"": [
                    { ""internalType"": ""address"", ""name"": ""to"", ""type"": ""address"" },
                    { ""internalType"": ""uint256"", ""name"": ""amount"", ""type"": ""uint256"" }
                ],
                ""name"": ""mint"",
                ""outputs"": [],
                ""stateMutability"": ""nonpayable"",
                ""type"": ""function""
            },
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
        /// Mint vBTC.b on Base Sepolia for a given bridge lock record.
        /// Returns (success, txHash or error message).
        /// </summary>
        public static async Task<(bool Success, string Result)> MintVBTCbOnBase(BridgeLockRecord lockRecord)
        {
            try
            {
                if (!IsEnabled)
                    return (false, "Base bridge is not configured. Set VBTCbContractAddress and RelayPrivateKey.");

                if (string.IsNullOrEmpty(lockRecord.EvmDestination) || !lockRecord.EvmDestination.StartsWith("0x") || lockRecord.EvmDestination.Length != 42)
                    return (false, $"Invalid EVM destination address: {lockRecord.EvmDestination}");

                if (lockRecord.AmountSats <= 0)
                    return (false, "Amount must be greater than zero");

                LogUtility.Log($"[BaseBridge] Minting vBTC.b on Base. To: {lockRecord.EvmDestination}, Amount: {lockRecord.Amount} BTC ({lockRecord.AmountSats} sats), LockId: {lockRecord.LockId}",
                    "BaseBridgeService.MintVBTCbOnBase");

                // Create web3 instance with relay account
                var account = new Account(RelayPrivateKey, BaseChainId);
                var web3 = new Web3(account, BaseRpcUrl);
                web3.TransactionManager.UseLegacyAsDefault = false;

                // Get contract instance
                var contract = web3.Eth.GetContract(MINT_ABI, VBTCbContractAddress);
                var mintFunction = contract.GetFunction("mint");

                // vBTC.b uses 8 decimals (same as BTC satoshis)
                // AmountSats is already in satoshis, which maps directly to the token's smallest unit
                var amountWei = new BigInteger(lockRecord.AmountSats);

                // Update status to ProofSubmitted before sending
                BridgeLockRecord.UpdateStatus(lockRecord.LockId, BridgeLockStatus.ProofSubmitted);

                // Estimate gas
                var gas = await mintFunction.EstimateGasAsync(
                    account.Address,
                    new HexBigInteger(300000),
                    new HexBigInteger(0),
                    lockRecord.EvmDestination,
                    amountWei);

                // Send transaction
                var txHash = await mintFunction.SendTransactionAsync(
                    account.Address,
                    new HexBigInteger(gas.Value + 50000), // add buffer
                    new HexBigInteger(0),
                    lockRecord.EvmDestination,
                    amountWei);

                LogUtility.Log($"[BaseBridge] Mint TX submitted. Hash: {txHash}, LockId: {lockRecord.LockId}",
                    "BaseBridgeService.MintVBTCbOnBase");

                // Wait for receipt (up to 60 seconds)
                var receipt = await WaitForReceipt(web3, txHash, 60);

                if (receipt != null && receipt.Status?.Value == 1)
                {
                    BridgeLockRecord.UpdateStatus(lockRecord.LockId, BridgeLockStatus.Minted, baseTxHash: txHash);
                    LogUtility.Log($"[BaseBridge] Mint CONFIRMED. TxHash: {txHash}, Block: {receipt.BlockNumber?.Value}",
                        "BaseBridgeService.MintVBTCbOnBase");
                    return (true, txHash);
                }
                else if (receipt != null)
                {
                    var err = "Transaction reverted on-chain";
                    BridgeLockRecord.UpdateStatus(lockRecord.LockId, BridgeLockStatus.Failed, baseTxHash: txHash, errorMessage: err);
                    return (false, $"Mint TX reverted. Hash: {txHash}");
                }
                else
                {
                    // Receipt not found within timeout - TX may still be pending
                    BridgeLockRecord.UpdateStatus(lockRecord.LockId, BridgeLockStatus.ProofSubmitted, baseTxHash: txHash);
                    return (true, $"TX submitted but not yet confirmed. Hash: {txHash}. Check Base Sepolia explorer.");
                }
            }
            catch (Exception ex)
            {
                var errMsg = $"Mint failed: {ex.Message}";
                BridgeLockRecord.UpdateStatus(lockRecord.LockId, BridgeLockStatus.Failed, errorMessage: errMsg);
                LogUtility.Log($"[BaseBridge] ERROR: {errMsg}", "BaseBridgeService.MintVBTCbOnBase");
                return (false, errMsg);
            }
        }

        /// <summary>
        /// Process all pending bridge lock records (status = Locked) by minting on Base.
        /// Called by background worker or manually via API.
        /// </summary>
        public static async Task<List<(string LockId, bool Success, string Result)>> ProcessPendingLocks()
        {
            var results = new List<(string LockId, bool Success, string Result)>();

            if (!IsEnabled)
                return results;

            var pending = BridgeLockRecord.GetPendingRelays();
            foreach (var lock_ in pending)
            {
                var result = await MintVBTCbOnBase(lock_);
                results.Add((lock_.LockId, result.Success, result.Result));

                // Small delay between transactions to avoid nonce issues
                if (pending.Count > 1)
                    await Task.Delay(2000);
            }

            return results;
        }

        /// <summary>
        /// Query vBTC.b balance on Base for a given EVM address.
        /// </summary>
        public static async Task<(bool Success, decimal Balance, string Message)> GetBaseBalance(string evmAddress)
        {
            try
            {
                if (!CanReadVbtcToken)
                    return (false, 0, "Base vBTC.b read not configured (set BASE_BRIDGE_RPC_URL and BASE_BRIDGE_CONTRACT)");

                var web3 = new Web3(BaseRpcUrl);
                var contract = web3.Eth.GetContract(MINT_ABI, VBTCbContractAddress);

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
                var contract = web3.Eth.GetContract(MINT_ABI, VBTCbContractAddress);

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

        /// <summary>Native ETH balance on Base (read-only, no relay key).</summary>
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
        /// Load configuration from environment variables. Defaults follow <see cref="ReserveBlockCore.Globals.IsTestNet"/>
        /// (Base Sepolia vs Base mainnet), like BTC Electrum/network selection.
        /// </summary>
        public static void LoadConfig()
        {
            if (ReserveBlockCore.Globals.IsTestNet)
            {
                BaseRpcUrl = "https://sepolia.base.org";
                BaseChainId = 84532;
            }
            else
            {
                BaseRpcUrl = "https://mainnet.base.org";
                BaseChainId = 8453;
            }

            var rpc = Environment.GetEnvironmentVariable("BASE_BRIDGE_RPC_URL");
            if (!string.IsNullOrEmpty(rpc)) BaseRpcUrl = rpc;

            var contract = Environment.GetEnvironmentVariable("BASE_BRIDGE_CONTRACT");
            if (!string.IsNullOrEmpty(contract)) VBTCbContractAddress = contract;

            var key = Environment.GetEnvironmentVariable("BASE_BRIDGE_RELAY_KEY");
            if (!string.IsNullOrEmpty(key)) RelayPrivateKey = key;

            var chainIdStr = Environment.GetEnvironmentVariable("BASE_BRIDGE_CHAIN_ID");
            if (!string.IsNullOrEmpty(chainIdStr) && int.TryParse(chainIdStr, out var chainId))
                BaseChainId = chainId;

            if (IsEnabled)
            {
                LogUtility.Log($"[BaseBridge] Mint enabled. RPC: {BaseRpcUrl}, Contract: {VBTCbContractAddress}, ChainId: {BaseChainId}, Network: {BaseNetworkDisplayName}",
                    "BaseBridgeService.LoadConfig");
            }
            else if (CanReadVbtcToken)
            {
                LogUtility.Log($"[BaseBridge] Read-only (balances). RPC: {BaseRpcUrl}, Contract: {VBTCbContractAddress}, ChainId: {BaseChainId}, Network: {BaseNetworkDisplayName}",
                    "BaseBridgeService.LoadConfig");
            }
            else if (CanReadEth)
            {
                LogUtility.Log($"[BaseBridge] RPC only (ETH balance). {BaseRpcUrl}, Network: {BaseNetworkDisplayName}",
                    "BaseBridgeService.LoadConfig");
            }
            else
            {
                LogUtility.Log("[BaseBridge] Not configured. Set BASE_BRIDGE_RPC_URL (and optionally BASE_BRIDGE_CONTRACT / BASE_BRIDGE_RELAY_KEY).",
                    "BaseBridgeService.LoadConfig");
            }
        }

        /// <summary>
        /// Background loop that mints pending bridge locks every 30 seconds (see <see cref="BridgeLockRecord.GetPendingRelays"/>:
        /// VFX lock confirmed on-chain, status Locked, relay not yet completed).
        /// </summary>
        public static async Task BridgeMintRetryLoop()
        {
            // Wait for startup to complete
            await Task.Delay(30_000);

            while (!ReserveBlockCore.Globals.StopAllTimers)
            {
                try
                {
                    if (IsEnabled)
                    {
                        var pending = BridgeLockRecord.GetPendingRelays();
                        if (pending.Any())
                        {
                            LogUtility.Log($"[BaseBridge] Retry loop: {pending.Count} pending lock(s) to mint.", "BaseBridgeService.BridgeMintRetryLoop");
                            await ProcessPendingLocks();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogUtility.Log($"[BaseBridge] Retry loop error: {ex.Message}", "BaseBridgeService.BridgeMintRetryLoop");
                }

                await Task.Delay(30_000);
            }
        }

        #region Helpers

        private static async Task<Nethereum.RPC.Eth.DTOs.TransactionReceipt?> WaitForReceipt(Web3 web3, string txHash, int timeoutSeconds)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < timeoutSeconds)
            {
                try
                {
                    var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    if (receipt != null)
                        return receipt;
                }
                catch { }
                await Task.Delay(3000);
            }
            return null;
        }

        #endregion
    }
}