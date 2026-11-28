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
    /// A Base burn for a VFX exit may name a registered name as its destination. The pool unlock credited the name as
    /// written, a ledger key nobody can spend from. From Globals.PoolUnlockCreditsAddressHeight the unlock carries
    /// VfxCreditAddress, the address the name resolved to when the unlock was built, credits it, and a block refuses an
    /// unlock whose VfxCreditAddress is missing, is not an address, or is not the address its destination names. The
    /// casters' votes stay bound to the burned destination. Below the height an unlock is judged and applied as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PoolUnlockCreditsAddressTests : BtcAttestTestBase
    {
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        /// <summary>A vault from before the chain watched vaults' Bitcoin: its exits are judged by the bridge's own rules.</summary>
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        private const string Name = "holder.vfx";
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorCredit = Globals.PoolUnlockCreditsAddressHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly List<V> _casters = Enumerable.Range(0, Casters).Select(_ => NewKey()).ToList();

        public PoolUnlockCreditsAddressTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.PoolUnlockCreditsAddressHeight = long.MaxValue; // as released: unset until a test sets it
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Minter.Address, OwnerAddress = Minter.Address });
            Adnr.GetAdnr()!.Insert(new Adnr { Address = Minter.Address, Name = Name, TxHash = "adnr-tx", Timestamp = TimeUtil.GetTime() });
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
            Globals.PoolUnlockCreditsAddressHeight = _priorCredit;
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

        /// <summary>Every caster's vote for the burn, its amount and the destination burned on Base.</summary>
        private List<CasterConsensusVote> Votes(string burn, long sats, string destination) => _casters.Select(caster =>
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildBoundVoteMessage(burn, "POOL_EXIT", sats, destination, timestamp);
            return new CasterConsensusVote { CasterAddress = caster.Address, BaseBurnTxHash = burn, BurnType = "POOL_EXIT", Timestamp = timestamp,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, caster.Key, caster.Pub) };
        }).ToList();

        /// <summary>The pool unlock a caster sends for a burn to <paramref name="destination"/>, crediting <paramref name="credit"/> when given.</summary>
        private VfxTx Unlock(string burn, string lockId, decimal amount, string destination, string? credit)
        {
            var sats = (long)(amount * 100_000_000M);
            var data = new JObject
            {
                ["Function"] = "VBTCBridgePoolUnlock()", ["TotalAmount"] = amount, ["TotalAmountSats"] = sats, ["VfxDestinationAddress"] = destination,
            };
            if (credit != null) data["VfxCreditAddress"] = credit;
            data["ExitBurnTxHash"] = burn;
            data["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = lockId, SmartContractUID = Vault, UnlockAmount = amount } });
            data["CasterConsensusVotes"] = JArray.FromObject(Votes(burn, sats, destination));
            var caster = _casters[0];
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = caster.Address, ToAddress = caster.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, caster.Key, caster.Pub);
            return tx;
        }

        /// <summary>Who the vault's ledger credited last.</summary>
        private static string? LastCredited() =>
            SmartContractStateTrei.GetSmartContractState(Vault)?.SCStateTreiTokenizationTXes?.LastOrDefault()?.ToAddress;

        [Fact]
        public void ADestinationIsResolvedToTheAddressItNames()
        {
            Assert.Equal(Minter.Address, BridgeTxRules.ResolveVfxDestination(Minter.Address));
            Assert.Equal(Minter.Address, BridgeTxRules.ResolveVfxDestination(Name));
            Assert.Null(BridgeTxRules.ResolveVfxDestination("nobody.vfx"));
            Assert.Null(BridgeTxRules.ResolveVfxDestination(" " + Minter.Address));
            Assert.Null(BridgeTxRules.ResolveVfxDestination(""));
            Assert.False(BridgeTxRules.IsVfxAddress(Name));
            Assert.True(BridgeTxRules.IsVfxAddress(Minter.Address));
        }

        [Fact]
        public async Task FromTheHeight_AnUnlockToAName_CreditsTheAddressItNames()
        {
            await Start();
            var gate = Tip + 1;
            Globals.PoolUnlockCreditsAddressHeight = gate;
            var lockId = Lock(0.004M);
            var unlock = Unlock(NewBurn(), lockId, 0.004M, Name, Minter.Address);

            var (admitted, admittedWhy) = await TransactionValidatorService.VerifyTX(unlock);
            Assert.True(admitted, admittedWhy);
            var (ok, why) = await Verify(unlock, gate);
            Assert.True(ok, why);
            await Apply(gate, unlock);

            Assert.Equal(Minter.Address, LastCredited());
            Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);
        }

        [Fact]
        public async Task FromTheHeight_AnUnlockWhoseCreditIsNotTheAddressItsDestinationNames_IsRefused()
        {
            await Start();
            var gate = Tip + 1;
            Globals.PoolUnlockCreditsAddressHeight = gate;
            var lockId = Lock(0.004M);
            var other = NewKey().Address;
            foreach (var (destination, credit) in new (string, string?)[]
            {
                (Name, null),               // no address carried
                (Name, Name),               // the name itself
                (Name, other),              // an address the name does not name
                (Minter.Address, other),    // an address destination credits only itself
                ("nobody.vfx", other),      // a name that names nothing
            })
            {
                var (ok, why) = await Verify(Unlock(NewBurn(), lockId, 0.004M, destination, credit), gate + 1);
                Assert.False(ok, $"accepted {destination} crediting {credit}");
                Assert.Contains("VfxCreditAddress", why);
            }
            // An address destination credits itself.
            var (selfOk, selfWhy) = await Verify(Unlock(NewBurn(), lockId, 0.004M, Minter.Address, Minter.Address), gate);
            Assert.True(selfOk, selfWhy);
        }

        [Fact]
        public async Task BelowTheHeight_AnUnlockIsJudgedAndAppliedAsBefore()
        {
            await Start();
            var gate = Tip + 10;
            Globals.PoolUnlockCreditsAddressHeight = gate;
            var lockId = Lock(0.004M);
            var unlock = Unlock(NewBurn(), lockId, 0.004M, Name, null);
            var (ok, why) = await Verify(unlock, gate - 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, unlock);
            Assert.Equal(Name, LastCredited()); // as before the height: the name as written
        }
    }
}
