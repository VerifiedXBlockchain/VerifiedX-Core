using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A vault can keep bridge locks after its Bitcoin key falls below the holders it needs to sign
    /// (BtcVaultKeys.CannotSignRefusal). An exit drawing on such a lock was accepted: its part could not be pinned, and the
    /// exit waited out its window and failed. From Globals.BridgeAllocationsSignableHeight a block refuses a bridge exit
    /// (or pool unlock) that draws on one; below it the exit is judged as before. Driven through VerifyTX with the
    /// committee's bound votes, as a block's transaction.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeAllocationsSignableTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>A vault from before the chain watched vaults' Bitcoin: its exits are judged by the bridge's own rules.</summary>
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorSignable = Globals.BridgeAllocationsSignableHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();

        public BridgeAllocationsSignableTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeAllocationsSignableHeight = long.MaxValue; // as released: unset until a test sets it
            Globals.BTCNetwork = Network.Main;
            foreach (var caster in _casters)
            {
                Globals.BootstrapCasterAddresses.Add(caster.Address);
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = caster.Address, Balance = 10M, Nonce = 0 });
            }
        }

        public override void Dispose()
        {
            foreach (var caster in _casters) Globals.BootstrapCasterAddresses.Remove(caster.Address);
            Globals.BTCNetwork = _priorNetwork;
            Globals.BridgeAllocationsSignableHeight = _priorSignable;
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BridgeBurnBindingHeight = _priorBinding;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>A lock on a vault (the older one unless given), as the block that carried it left it; returns its id.</summary>
        private string Lock(decimal amount, string vault = Vault)
        {
            var id = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>Every caster's vote for the burn, its amount and its destination.</summary>
        private List<CasterConsensusVote> Votes(string burn, long sats) => _casters.Select(caster =>
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildBoundVoteMessage(burn, "BTC_EXIT", sats, BtcDest, timestamp);
            return new CasterConsensusVote { CasterAddress = caster.Address, BaseBurnTxHash = burn, BurnType = "BTC_EXIT", Timestamp = timestamp,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub) };
        }).ToList();

        /// <summary>
        /// The exit a caster sends for a burn, drawing <paramref name="amount"/> from one lock, with the committee's votes.
        /// <paramref name="total"/> and <paramref name="totalSats"/> leave a total out, <paramref name="shape"/> changes
        /// the payload further, and <paramref name="spelled"/> writes the burn's id another way than the votes do.
        /// </summary>
        private VfxTx Exit(string burn, string lockId, decimal amount, bool total = true, bool totalSats = true, string? spelled = null, int sender = 0,
            string vault = Vault, Action<JObject>? shape = null, long? timestamp = null)
        {
            var sats = (long)(amount * 100_000_000M);
            var data = new JObject
            {
                ["Function"] = "VBTCBridgeExitToBTC()", ["ContractUID"] = vault, ["LockId"] = lockId, ["Amount"] = amount, ["AmountSats"] = sats,
            };
            if (total) data["TotalAmount"] = amount;
            if (totalSats) data["TotalAmountSats"] = sats;
            data["BtcDestination"] = BtcDest;
            data["BaseBurnTxHash"] = spelled ?? burn;
            data["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = lockId, SmartContractUID = vault, UnlockAmount = amount } });
            data["BtcWithdrawals"] = JValue.CreateNull(); // as the casters' builder writes it
            data["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, sats));
            shape?.Invoke(data);
            var caster = _casters[sender];
            var tx = new VfxTx
            {
                Timestamp = timestamp ?? TimeUtil.GetTime(), FromAddress = caster.Address, ToAddress = caster.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, caster.Key, caster.Pub);
            return tx;
        }

        /// <summary>The vault's key, as the chain records it after an epoch where too few of its holders still count.</summary>
        private static void KeyHasTooFewHolders(string vault)
        {
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveScript(new VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow
            {
                SmartContractUID = vault, ScriptHex = "5120" + new string('7', 64), MinSigners = 3, HealthyHolders = 2,
            });
            Assert.Contains("needs to sign", VerifiedXCore.Bitcoin.Attestation.BtcVaultKeys.CannotSignRefusal(vault));
        }

        [Fact]
        public async Task FromTheHeight_AnExitDrawingOnAVaultWhoseKeyCannotSign_IsRefused_AndBelowItJudgedAsBefore()
        {
            await Start();
            var gate = Tip + 1;
            var lockId = Lock(0.004M);
            var exit = Exit(NewBurn(), lockId, 0.004M);
            var (sound, soundWhy) = await Verify(exit, gate);
            Assert.True(sound, soundWhy);

            KeyHasTooFewHolders(Vault);
            var (unset, unsetWhy) = await Verify(exit, gate);                       // height unset: as before
            Assert.True(unset, unsetWhy);
            Globals.BridgeAllocationsSignableHeight = gate + 1;
            var (below, belowWhy) = await Verify(exit, gate);
            Assert.True(below, belowWhy);
            foreach (var height in new[] { gate + 1, gate + 500 })
            {
                var (ok, why) = await Verify(exit, height);
                Assert.False(ok, $"accepted at {height}");
                Assert.Contains("needs to sign", why);
            }
        }
    }
}
