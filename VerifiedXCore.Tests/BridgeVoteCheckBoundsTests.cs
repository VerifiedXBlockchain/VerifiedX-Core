using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge transaction's vote list costs a bounded number of signature checks: none for a caster already counted,
    /// none once enough are counted, and at admission a list in which a caster has more than one vote to check is
    /// refused. In a block the same lists count exactly as before. An unlock naming no lock is refused before its votes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeVoteCheckBoundsTests : IDisposable
    {
        private const string Burn = "0x1111111111111111111111111111111111111111111111111111111111111111";
        private const string Destination = "xDestinationAddressForTheUnlock000";
        private const long Sats = 100_000;
        private const long Height = 5001;
        /// <summary>With this network's own seed casters they form the committee; these are a majority of it.</summary>
        private const int Casters = 5;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly bool _priorIsTestNet;
        private readonly Block _priorLastBlock;
        private readonly long _priorBindingHeight;
        private readonly List<(PrivateKey Key, string Pub, string Address)> _casters = new();

        public BridgeVoteCheckBoundsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridge_vote_bounds_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorIsTestNet = Globals.IsTestNet;
            Globals.IsTestNet = false; // the keys made here have mainnet addresses, and the fallback committee is this network's
            _priorLastBlock = Globals.LastBlock;
            _priorBindingHeight = Globals.BridgeBurnBindingHeight;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Height - 1 };
            for (var i = 0; i < Casters; i++)
            {
                (PrivateKey Key, string Pub, string Address) caster = NewKey();
                _casters.Add(caster);
                Globals.BootstrapCasterAddresses.Add(caster.Address);
            }
        }

        public void Dispose()
        {
            foreach (var caster in _casters) Globals.BootstrapCasterAddresses.Remove(caster.Address);
            try { DbContext.CloseDB(); } catch { }
            Globals.BridgeBurnBindingHeight = _priorBindingHeight;
            Globals.LastBlock = _priorLastBlock;
            Globals.IsTestNet = _priorIsTestNet;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private CasterConsensusVote BoundVote(int caster, bool valid = true)
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildBoundVoteMessage(Burn, "POOL_EXIT", valid ? Sats : Sats + 1, Destination, timestamp);
            return new CasterConsensusVote { CasterAddress = _casters[caster].Address, BaseBurnTxHash = Burn, BurnType = "POOL_EXIT", Timestamp = timestamp,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, _casters[caster].Key, _casters[caster].Pub) };
        }

        private CasterConsensusVote LegacyVote(int caster, bool valid = true)
        {
            var timestamp = TimeUtil.GetTime();
            var message = BridgeCasterConsensus.BuildVoteMessage(valid ? Burn : Burn + "0", "EXIT", timestamp);
            return new CasterConsensusVote { CasterAddress = _casters[caster].Address, BaseBurnTxHash = Burn, BurnType = "EXIT", Timestamp = timestamp,
                Signature = VerifiedXCore.Services.SignatureService.CreateSignature(message, _casters[caster].Key, _casters[caster].Pub) };
        }

        private (bool Ok, string Reason) Bound(List<CasterConsensusVote> votes, bool admission) =>
            BridgeCasterConsensus.VerifyBoundVotes(votes, Burn, "POOL_EXIT", Sats, Destination, Height, eachCasterOnce: admission);

        private (bool Ok, string Reason) Legacy(List<CasterConsensusVote> votes, bool admission) =>
            BridgeCasterConsensus.TryVerifyVotesFromCommittee(votes, Burn, "EXIT", Height, eachCasterOnce: admission);

        [Fact]
        public void TheCastersMadeHere_AreAMajorityOfTheCommittee()
        {
            var committee = BridgeCasterConsensus.GetCommitteeForHeight(Height);
            Assert.All(_casters, c => Assert.Contains(c.Address, committee));
            Assert.True(BridgeCasterConsensus.RequiredVotesFor(committee) <= Casters);
        }

        [Fact]
        public void OneVoteACaster_IsAcceptedAtAdmissionAndInABlock()
        {
            var bound = Enumerable.Range(0, Casters).Select(i => BoundVote(i)).ToList();
            var legacy = Enumerable.Range(0, Casters).Select(i => LegacyVote(i)).ToList();

            Assert.True(Bound(bound, admission: true).Ok);
            Assert.True(Bound(bound, admission: false).Ok);
            Assert.True(Legacy(legacy, admission: true).Ok);
            Assert.True(Legacy(legacy, admission: false).Ok);
        }

        [Fact]
        public void ACasterWithSeveralVotesToCheck_IsRefusedAtAdmission_AndCountedAsBeforeInABlock()
        {
            // Votes of one caster that do not verify, then the list's real votes.
            var bound = Enumerable.Repeat(0, 20).Select(_ => BoundVote(0, valid: false)).Concat(Enumerable.Range(0, Casters).Select(i => BoundVote(i))).ToList();
            var legacy = Enumerable.Repeat(0, 20).Select(_ => LegacyVote(0, valid: false)).Concat(Enumerable.Range(0, Casters).Select(i => LegacyVote(i))).ToList();

            Assert.Equal((false, BridgeCasterConsensus.VoteListedTwiceReason), Bound(bound, admission: true));
            Assert.Equal((false, BridgeCasterConsensus.VoteListedTwiceReason), Legacy(legacy, admission: true));
            Assert.True(Bound(bound, admission: false).Ok);
            Assert.True(Legacy(legacy, admission: false).Ok);
        }

        [Fact]
        public void VotesThatDoNotVerify_NeverMakeAMajority_InEitherMode()
        {
            var bound = Enumerable.Range(0, Casters).Select(i => BoundVote(i, valid: false)).ToList();
            var legacy = Enumerable.Range(0, Casters).Select(i => LegacyVote(i, valid: false)).ToList();

            Assert.False(Bound(bound, admission: true).Ok);
            Assert.False(Bound(bound, admission: false).Ok);
            Assert.False(Legacy(legacy, admission: true).Ok);
            Assert.False(Legacy(legacy, admission: false).Ok);
        }

        [Fact]
        public void AtAdmission_RepeatsAfterTheListIsDecided_AreNotRead()
        {
            // Every caster's vote, then repeats: the list is decided before the repeats are reached, so at admission
            // they are not met as a second vote to check.
            var bound = Enumerable.Range(0, Casters).Select(i => BoundVote(i)).Concat(Enumerable.Repeat(0, 50).Select(_ => BoundVote(0, valid: false))).ToList();

            Assert.True(Bound(bound, admission: true).Ok);
            Assert.True(Bound(bound, admission: false).Ok);
        }

        /// <summary>An unlock of a lock the sender holds, carrying the given votes.</summary>
        private Transaction UnlockOfAHeldLock(List<CasterConsensusVote> votes)
        {
            Globals.BridgeBurnBindingHeight = 1;
            (PrivateKey Key, string Pub, string Address) sender = NewKey();
            const string vault = "0123456789abcdef0123456789abcdef:1790600000";
            var lockId = "lock-" + Guid.NewGuid().ToString("N");
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = sender.Address, Balance = 10M, Nonce = 0 });
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState { LockId = lockId, SmartContractUID = vault, OwnerAddress = sender.Address,
                Amount = 0.001M, AmountSats = 100_000, EvmDestination = "0x00000000000000000000000000000000000000aa" });
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCBridgeUnlock()", ContractUID = vault, LockId = lockId,
                    Amount = 0.001M, AmountSats = 100_000L, ExitBurnTxHash = Burn, CasterConsensusVotes = votes }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }

        [Fact]
        public async Task AListWithACasterTwice_IsRefusedByVerifyTxAtAdmission_AndAcceptedInABlockAsBefore()
        {
            var votes = Enumerable.Repeat(0, 3).Select(_ => LegacyVote(0, valid: false)).Concat(Enumerable.Range(0, Casters).Select(i => LegacyVote(i))).ToList();
            var tx = UnlockOfAHeldLock(votes);

            var (admitted, admissionReason) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(admitted);
            Assert.Contains(BridgeCasterConsensus.VoteListedTwiceReason, admissionReason);

            var (inBlock, blockReason) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Height);
            Assert.True(inBlock, blockReason);
        }

        [Fact]
        public async Task AnUnlockWithOneVoteACaster_IsAcceptedAtAdmission()
        {
            var tx = UnlockOfAHeldLock(Enumerable.Range(0, Casters).Select(i => LegacyVote(i)).ToList());

            var (ok, reason) = await TransactionValidatorService.VerifyTX(tx);

            Assert.True(ok, reason);
        }

        [Fact]
        public async Task AnUnlockNamingNoLock_IsRefusedForTheLock_NotForItsVotes()
        {
            Globals.BridgeBurnBindingHeight = 1;
            (PrivateKey Key, string Pub, string Address) sender = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = sender.Address, Balance = 10M, Nonce = 0 });
            var votes = Enumerable.Repeat(0, 40).Select(_ => LegacyVote(0, valid: false)).ToList();
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_UNLOCK,
                Data = JsonConvert.SerializeObject(new { Function = "VBTCBridgeUnlock()", ContractUID = "0123456789abcdef0123456789abcdef:1790600000", LockId = "no-such-lock",
                    Amount = 0.001M, AmountSats = 100_000L, ExitBurnTxHash = Burn, CasterConsensusVotes = votes }),
            };
            tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);

            var (ok, reason) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("Unknown bridge LockId", reason);
        }
    }
}
