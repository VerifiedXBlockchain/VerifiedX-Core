using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// vBTC API routes that answer for ANY address or contract (web wallets through Spyglass, raw-transaction builders) read
    /// the chain, not this node's local VBTCContractV2 table - that table holds only contracts this node's own wallet owns or
    /// holds, so the routes failed for everyone else (tester report: ownership transfer "vBTC V2 contract not found" on
    /// Spyglass, working only on a node whose wallet held the contract). Every test runs on a node with NO local record.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcChainOnlyApiTests : IDisposable
    {
        private const string Vault = "5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a:1790950000";
        private const string Nft = "5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b5b:1790950001";
        private const string Deposit = "bc1pchainonlyvaultdepositaddress000000000000000000000000000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly VBTCController _api = new VBTCController();

        public VbtcChainOnlyApiTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcchain_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 5000, Timestamp = TimeUtil.GetTime() };

            // On chain only: the vault (owner + a holder with 0.5 vBTC on its ledger) and an ordinary NFT.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Vault, ContractData = VbtcTestContracts.VaultContractData(Vault, _owner.Address, Deposit),
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = _owner.Address, ToAddress = _holder.Address, Amount = 0.5M },
                },
            });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = Nft, ContractData = VbtcTestContracts.PlainNftContractData, MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
            });
            Assert.Null(VBTCContractV2.GetContract(Vault));   // the premise: no local record on this node
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private void MinedRequest(string hash, long height, VBTCWithdrawalStatus status, bool completed, string? completionHash = null) =>
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = _owner.Address, OriginalUniqueId = "u-" + hash, SmartContractUID = Vault, Amount = 0.2M,
                BTCDestination = "bc1qdestination", FeeRate = 5, TransactionHash = hash, Status = status, IsCompleted = completed,
                RequestBlockHeight = height, Timestamp = TimeUtil.GetTime() - 60, BTCTxHash = completed ? "btc-" + hash : null,
                CompletionTxHash = completionHash, CompletionTimestamp = completionHash == null ? null : TimeUtil.GetTime(),
            }));

        [Fact]
        public async Task OwnershipTransferData_IsBuiltFromChain()
        {
            // The tester's report: 500 "vBTC V2 contract not found" on Spyglass.
            var json = await _api.GetVBTCOwnershipTransferData(Vault, _holder.Address, "NA");
            var payload = JArray.Parse(json);
            Assert.Equal("Transfer()", (string?)payload[0]["Function"]);
            Assert.Equal(Vault, (string?)payload[0]["ContractUID"]);
            Assert.Equal(_holder.Address, (string?)payload[0]["ToAddress"]);
        }

        [Fact]
        public async Task DepositAddress_IsServedFromChain()
        {
            var r = JObject.Parse(await _api.GetMPCDepositAddress(Vault));
            Assert.True((bool)r["Success"]!);
            Assert.Equal(Deposit, (string?)r["DepositAddress"]);
            Assert.False((bool)JObject.Parse(await _api.GetMPCDepositAddress(Nft))["Success"]!);   // not a vault
        }

        [Fact]
        public async Task AllBalances_ListTheHoldersContractFromChain()
        {
            var r = JObject.Parse(await _api.GetAllVBTCBalances(_holder.Address));
            Assert.True((bool)r["Success"]!);
            var c = Assert.Single((JArray)r["Contracts"]!);
            Assert.Equal(Vault, (string?)c["SmartContractUID"]);
            Assert.Equal(0.5M, (decimal)c["Balance"]!);
            Assert.Equal(Deposit, (string?)c["DepositAddress"]);
        }

        [Fact]
        public async Task MultiContractRawBuilders_FindTheSendersContractsOnChain()
        {
            var transfer = await VBTCService.BuildTransferAllocationPlan(_holder.Address, 0.3M);
            Assert.True(transfer.Ok, transfer.Error);
            Assert.Equal(Vault, Assert.Single(transfer.Allocations).SCUID);

            var withdrawal = await VBTCService.BuildWithdrawalAllocationPlan(_holder.Address, 0.3M);
            Assert.True(withdrawal.Ok, withdrawal.Error);
            Assert.Equal(Vault, Assert.Single(withdrawal.Allocations).SCUID);
        }

        [Fact]
        public async Task CancellationOwnerCheck_UsesTheOwnerOnChain()
        {
            async Task<string> Cancel((PrivateKey Key, string Pub, string Address) who)
            {
                var ts = TimeUtil.GetTime();
                var p = new VBTCCancellationRawPayload
                {
                    SmartContractUID = Vault, OwnerAddress = who.Address, WithdrawalRequestHash = "req1", BTCTxHash = "btc1",
                    FailureProof = "proof", Timestamp = ts, UniqueId = "cancel-" + who.Address,
                };
                p.OwnerSignature = VerifiedXCore.Services.SignatureService.CreateSignature(
                    $"{p.SmartContractUID}{p.OwnerAddress}{p.WithdrawalRequestHash}{p.BTCTxHash}{p.FailureProof}{p.Timestamp}{p.UniqueId}", who.Key, who.Pub);
                return (string?)JObject.Parse(await _api.CancelWithdrawalRaw(p))["Message"] ?? "";
            }
            Assert.NotEqual("Only the contract owner can request cancellation", await Cancel(_owner));   // past the owner check
            Assert.Equal("Only the contract owner can request cancellation", await Cancel(_holder));
        }

        [Fact]
        public async Task WithdrawalStatusAndHistory_ComeFromChainData()
        {
            var none = JObject.Parse(await _api.GetWithdrawalStatus(Vault));
            Assert.True((bool)none["Success"]!);
            Assert.Equal("None", (string?)none["Status"]);
            Assert.False((bool)none["HasActiveWithdrawal"]!);

            MinedRequest("req-done", 4900, VBTCWithdrawalStatus.Completed, completed: true, completionHash: "complete-tx");
            MinedRequest("req-open", 4990, VBTCWithdrawalStatus.Requested, completed: false);
            var open = JObject.Parse(await _api.GetWithdrawalStatus(Vault));
            Assert.Equal("Requested", (string?)open["Status"]);
            Assert.True((bool)open["HasActiveWithdrawal"]!);

            var history = JObject.Parse(await _api.GetWithdrawalHistory(Vault));
            var h = Assert.Single((JArray)history["WithdrawalHistory"]!);
            Assert.Equal("req-done", (string?)h["RequestHash"]);
            Assert.Equal("complete-tx", (string?)h["CompletionHash"]);
            Assert.Equal("btc-req-done", (string?)h["BTCTransactionHash"]);
        }

        [Fact]
        public async Task ContractList_ComesFromChain()
        {
            var owned = JObject.Parse(await _api.GetContractList(_owner.Address));
            var c = Assert.Single((JArray)owned["Contracts"]!);
            Assert.Equal(Vault, (string?)c["SmartContractUID"]);
            Assert.Equal(Deposit, (string?)c["DepositAddress"]);
            Assert.Empty((JArray)JObject.Parse(await _api.GetContractList(_holder.Address))["Contracts"]!);   // owner filter
            var all = (JArray)JObject.Parse(await _api.GetContractList(null))["Contracts"]!;
            Assert.Contains(all, x => (string?)x["SmartContractUID"] == Vault);
            Assert.DoesNotContain(all, x => (string?)x["SmartContractUID"] == Nft);                           // vaults only
        }

        [Fact]
        public void PrepareCompleteWithdrawalRaw_TakesTheDepositAddressFromChain()
        {
            Assert.Equal(Deposit, VBTCChainView.GetVault(Vault)?.Feature.DepositAddress);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var src = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.Contains("VBTCChainView.GetVault(payload.SmartContractUID)?.Feature.DepositAddress", src);
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
