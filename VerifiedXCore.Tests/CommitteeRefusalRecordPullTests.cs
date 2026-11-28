using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;
using SignatureService = VerifiedXCore.Services.SignatureService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A block is judged against the caster committee in the membership record this node holds: who may attest it, whose
    /// votes a bridge unlock or exit carries, who may send one. Records reach a node apart from the blocks, so a node that
    /// has not received a record yet judges the next block by the committee before it and refuses a block its peers took.
    /// Such a refusal brings the next pull of the record forward; it is not read as a sign of a damaged state; and when
    /// the pull brings a newer record the block is judged once more. Only a record this node adopted gets the block that
    /// second look: what peers say they hold does not, and nothing holds the node while they say it.
    /// Through BlockValidatorService.ValidateBlock at a synced tip, ValidateBlocks as the startup download runs it, and
    /// TransactionValidatorService.VerifyTX at admission, locally and by the raw route.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CommitteeRefusalRecordPullTests : BtcAttestTestBase
    {
        private const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        private const string TxStep = "BlockValidatorService.ValidateBlock()-13";
        private const string CertStep = "BlockValidatorService.ValidateBlock()-cert";
        private const long OldSeq = 3;

        private readonly long _priorBinding = Globals.BridgeBurnBindingHeight;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly long _priorBound = Globals.BridgeVotesBoundToAttemptHeight;
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly bool _priorSynced = Globals.IsChainSynced;
        private readonly bool _priorCaster = Globals.IsBlockCaster;
        private readonly string _priorAddress = Globals.ValidatorAddress;
        private readonly long _priorCertEnforce = Globals.CertEnforceHeight;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly ConcurrentBag<Peers> _priorCasters = Globals.BlockCasters;
        private readonly IReadOnlyList<long> _priorHeads = CasterDiscoveryService.LastObservedPeerHeads;
        private readonly Func<string, long, Task<MembershipRecordResponse?>> _priorFetch = CasterDiscoveryService.FetchMembershipResponse;

        /// <summary>The committee before the change (four casters) and after it (the first three).</summary>
        private readonly List<V> _casters = Enumerable.Range(0, 4).Select(_ => NewKey()).ToList();
        private readonly V _producer = NewKey();
        private CasterMembershipRecord _old = null!;
        private CasterMembershipRecord _new = null!;
        /// <summary>What every peer answers a pull with.</summary>
        private MembershipRecordResponse? _peersAnswer;
        private int _pulls;

        public CommitteeRefusalRecordPullTests() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeVotesBoundToAttemptHeight = long.MaxValue;
            Globals.BTCNetwork = Network.Main;
            Globals.IsChainSynced = true;
            Globals.IsBlockCaster = false;
            Globals.ValidatorAddress = "";
            Globals.CertEnforceHeight = 1;
            Globals.BlockCasters = new ConcurrentBag<Peers>();
            BlockchainData.ChainRef = "test-chain";
            CasterMembershipStore.InvalidateCache();
            ValidatorMembershipPull.ResetForTests();
            CasterDiscoveryService.FetchMembershipResponse = (_, _) =>
            {
                Interlocked.Increment(ref _pulls);
                return Task.FromResult(_peersAnswer);
            };
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Minter.Address, OwnerAddress = Minter.Address });
            foreach (var caster in _casters)
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = caster.Address, Balance = 10M, Nonce = 0 });
        }

        public override void Dispose()
        {
            CasterDiscoveryService.FetchMembershipResponse = _priorFetch;
            CasterDiscoveryService.SetObservedPeerHeadsForTests(_priorHeads.ToArray());
            ValidatorMembershipPull.ResetForTests();
            BlockDownloadService.BlockDict.Clear();
            Globals.BlockCasters = _priorCasters;
            Globals.SyncKnownCastersFromBlockCasters();
            Globals.ValidatorAddress = _priorAddress;
            Globals.IsBlockCaster = _priorCaster;
            Globals.IsChainSynced = _priorSynced;
            Globals.CertEnforceHeight = _priorCertEnforce;
            BlockchainData.ChainRef = _priorChainRef;
            Globals.BTCNetwork = _priorNetwork;
            Globals.BridgeVotesBoundToAttemptHeight = _priorBound;
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BridgeBurnBindingHeight = _priorBinding;
            CasterMembershipStore.InvalidateCache();
            base.Dispose();
        }

        // ── Harness ──────────────────────────────────────────────────────────────────────────────

        private static long Tip => Globals.LastBlock.Height;

        private static CasterMembershipRecord Record(long seq, long effective, string prev, IEnumerable<V> members, string change, string changed = "")
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq, EffectiveFromHeight = effective, PrevRecordHash = prev, ChangeType = change, ChangedAddress = changed,
                Casters = members.Select((m, i) => new CasterInfo { Address = m.Address, PeerIP = $"10.9.0.{i + 1}", PublicKey = m.Pub }).OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
            };
            r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
            return r;
        }

        /// <summary>
        /// The chain is at its tip, this node holds the four-caster record, and the committee has signed the record that
        /// takes the fourth caster out from the next block: three casters, so two votes or attestations are a majority
        /// where three were.
        /// </summary>
        private async Task StartWithTheRecordBeforeTheChange()
        {
            await Start();
            _old = Record(OldSeq, 50, "prev", _casters, "Promotion", _casters[3].Address);
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(_old.RecordSeq, _old);
            CasterMembershipStore.InvalidateCache();
            _new = Record(OldSeq + 1, Tip + 1, _old.RecordHash, _casters.Take(3), "Demotion", _casters[3].Address);
            var payload = CasterMembershipStore.CanonicalPayload(_new);
            _new.Signatures = _casters.Take(3).Select(m => new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) }).ToList();
            Assert.Equal(3, BridgeCasterConsensus.RequiredVotesFor(BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1)));
        }

        private void PeersHoldTheNewRecord() =>
            _peersAnswer = new MembershipRecordResponse { HeadSeq = _new.RecordSeq, Records = new List<CasterMembershipRecord> { _new } };

        /// <summary>Peers say they hold a newer record and hand over none.</summary>
        private void PeersOnlyClaimANewerRecord() => _peersAnswer = new MembershipRecordResponse { HeadSeq = 9 };

        private static long HeldSeq() => CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1;

        private string Lock(decimal amount)
        {
            var id = "lock-" + Guid.NewGuid().ToString("N");
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState
            {
                LockId = id, SmartContractUID = Vault, OwnerAddress = Minter.Address, Amount = amount, AmountSats = (long)(amount * 100_000_000M),
                EvmDestination = "0x" + new string('a', 40), LockTimestamp = TimeUtil.GetTime(),
            });
            return id;
        }

        /// <summary>A pool unlock sent by the first caster, voted by <paramref name="voters"/>.</summary>
        private VfxTx Unlock(IEnumerable<V> voters, decimal amount = 0.004M)
        {
            var burn = "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var sats = (long)(amount * 100_000_000M);
            var voted = TimeUtil.GetTime();
            var votes = voters.Select(c => new CasterConsensusVote
            {
                CasterAddress = c.Address, BaseBurnTxHash = burn, BurnType = "POOL_EXIT", Timestamp = voted,
                Signature = SignatureService.CreateSignature(BridgeCasterConsensus.BuildBoundVoteMessage(burn, "POOL_EXIT", sats, Minter.Address, voted), c.Key, c.Pub),
            }).ToList();
            var data = new JObject
            {
                ["Function"] = "VBTCBridgePoolUnlock()", ["TotalAmount"] = amount, ["TotalAmountSats"] = sats, ["VfxDestinationAddress"] = Minter.Address,
                ["ExitBurnTxHash"] = burn,
                ["Allocations"] = JArray.FromObject(new[] { new PoolUnlockAllocation { LockId = Lock(amount), SmartContractUID = Vault, UnlockAmount = amount } }),
                ["CasterConsensusVotes"] = JArray.FromObject(votes),
            };
            var sender = _casters[0];
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }

        /// <summary>The next block, as its producer serves it: its reward, these transactions, and a certificate attested by <paramref name="attestors"/> (none when null).</summary>
        private VfxBlock NextBlock(IEnumerable<V>? attestors, params VfxTx[] transactions)
        {
            var height = Tip + 1;
            var prev = Globals.LastBlock.Hash;
            var reward = new VfxTx
            {
                FromAddress = "Coinbase_BlkRwd", ToAddress = _producer.Address, TransactionType = TransactionType.TX, Timestamp = TimeUtil.GetTime(),
                Amount = BlockVersionUtility.GetBlockVersion(height) < 4 ? BlockchainData.GetBlockReward() : 0.0M,
            };
            reward.Build();
            var txs = new List<VfxTx> { reward };
            txs.AddRange(transactions);
            var block = new VfxBlock
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = prev, Timestamp = TimeUtil.GetTime(), NumOfTx = txs.Count,
                Validator = _producer.Address, ValidatorAnswer = ProofUtility.ComputeVrf(_producer.Pub, height, VrfSeed.ThirdInput(height, prev) ?? prev).ProofHash,
                ChainRefId = "test-chain", Transactions = txs,
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, _producer.Key, _producer.Pub);
            if (attestors != null)
            {
                var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
                block.ConsensusCertificate = new ConsensusCertificate
                {
                    BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                    Attestations = attestors.Select(m => new CasterAttestation { CasterAddress = m.Address, Signature = SignatureService.CreateSignature(msg, m.Key, m.Pub) }).ToList(),
                };
            }
            return block;
        }

        private static List<string> StepsSince(DateTime since) =>
            BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();

        // ── ValidateBlock at the synced tip ──────────────────────────────────────────────────────

        /// <summary>
        /// The committee went from four casters to three. The next block's certificate is attested by all three, so it holds
        /// under either record; its pool unlock carries two votes, a majority of three and not of four. A node still on the
        /// four-caster record refused the block and stayed at it. It now asks for the record, adopts the one its peers hand
        /// over, and takes the block in the same pass.
        /// </summary>
        [Fact]
        public async Task ABlockWhoseUnlockTheNewCommitteeVoted_IsTakenOnceTheNewerRecordIsPulled()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersHoldTheNewRecord();
            var block = NextBlock(_casters.Take(3), Unlock(_casters.Take(2)));
            var counted = BlockValidatorService.ConsecutiveTxFailures;
            var since = DateTime.UtcNow;

            var taken = await BlockValidatorService.ValidateBlock(block, false);

            Assert.Equal(new[] { TxStep }, StepsSince(since));           // refused once, under the record before the change
            Assert.Equal(_new.RecordSeq, HeldSeq());                     // the record was adopted, signed by its predecessor's committee
            Assert.True(taken);
            Assert.Equal(block.Height, Tip);
            Assert.True(_pulls > 0);
            Assert.False(BlockValidatorService.WasRefusedForItsContents(block.Height, block.Hash)); // the first verdict was the record's, not the block's
            Assert.True(BlockValidatorService.ConsecutiveTxFailures <= counted);
        }

        /// <summary>
        /// Peers that only say they hold a newer record change nothing: the refusal stands as it did, the block's producer
        /// can still be left out for it, and nothing holds the node. With the record in hand the same block is taken at
        /// once, whatever the peers go on saying.
        /// </summary>
        [Fact]
        public async Task WhenPeersOnlyClaimANewerRecord_TheRefusalStands_AndNothingHoldsTheNode()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersOnlyClaimANewerRecord();
            Assert.True(await ValidatorMembershipPull.PullIfDueAsync());  // the minute pull has just run
            Assert.True(ValidatorMembershipPull.SecondsUntilDue() > ValidatorMembershipPull.RetrySeconds);
            Assert.Equal(new long[] { 9, 9, 9 }, CasterDiscoveryService.LastObservedPeerHeads.Take(3)); // every peer claims a newer record
            var pullsBefore = _pulls;
            var block = NextBlock(_casters.Take(3), Unlock(_casters.Take(2)));
            var counted = BlockValidatorService.ConsecutiveTxFailures;
            var since = DateTime.UtcNow;

            Assert.False(await BlockValidatorService.ValidateBlock(block, false));

            Assert.Equal(new[] { TxStep }, StepsSince(since));
            Assert.Equal(OldSeq, HeldSeq());
            Assert.Equal(block.Height - 1, Tip);
            // The next pull is brought forward, and no sooner than the retry time after the last: none ran for this block.
            Assert.True(ValidatorMembershipPull.SecondsUntilDue() <= ValidatorMembershipPull.RetrySeconds);
            Assert.Equal(pullsBefore, _pulls);
            // Not a sign of a damaged state: no restore is counted towards.
            Assert.Equal(counted, BlockValidatorService.ConsecutiveTxFailures);
            // Still a refusal for the block's contents on this node: a caster that tried it leaves its producer out.
            Assert.True(BlockValidatorService.WasRefusedForItsContents(block.Height, block.Hash));

            // A pull that is due and brings no record leaves the refusal as it is.
            ValidatorMembershipPull.ResetForTests();
            since = DateTime.UtcNow;
            Assert.False(await BlockValidatorService.ValidateBlock(block, false));
            Assert.Equal(new[] { TxStep }, StepsSince(since));
            Assert.True(_pulls > pullsBefore);
            Assert.Equal(OldSeq, HeldSeq());

            // The record arrives (an announcement, the minute pull): the block is taken, the peers' claims unchanged.
            Assert.True(CasterMembershipStore.TryAppend(_new, out var why, fromSync: true), why);
            Assert.True(await BlockValidatorService.ValidateBlock(block, false));
            Assert.Equal(block.Height, Tip);
        }

        /// <summary>
        /// The same for the certificate: two of the three casters attested the next block, a majority of three and not of
        /// the four this node still counts. It asks for the record, adopts it and takes the block.
        /// </summary>
        [Fact]
        public async Task ABlockTheNewCommitteeCertified_IsTakenOnceTheNewerRecordIsPulled()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersHoldTheNewRecord();
            var block = NextBlock(_casters.Take(2));
            Assert.False(ConsensusCertificateVerifier.VerifyOrNotRequired(block));
            var since = DateTime.UtcNow;

            var taken = await BlockValidatorService.ValidateBlock(block, false);

            Assert.Equal(new[] { CertStep }, StepsSince(since));
            Assert.Equal(_new.RecordSeq, HeldSeq());
            Assert.True(taken);
            Assert.Equal(block.Height, Tip);
        }

        /// <summary>
        /// A certificate refused under any record asks for none: a block with no certificate, one whose certificate is for
        /// another block, one that carries no attestation. Anyone can send such a block, and it must not have this node
        /// ask its peers.
        /// </summary>
        [Fact]
        public async Task ACertificateRefusedUnderAnyRecord_AsksForNoRecord()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersOnlyClaimANewerRecord();
            Assert.True(await ValidatorMembershipPull.PullIfDueAsync());  // the minute pull has just run
            Assert.True(ValidatorMembershipPull.SecondsUntilDue() > ValidatorMembershipPull.RetrySeconds);
            var pullsBefore = _pulls;

            var uncertified = NextBlock(null);
            var forAnother = NextBlock(_casters.Take(3));
            forAnother.ConsensusCertificate!.BlockHash = "another-block";
            var empty = NextBlock(_casters.Take(0));
            foreach (var block in new[] { uncertified, forAnother, empty })
            {
                var since = DateTime.UtcNow;
                Assert.False(await BlockValidatorService.ValidateBlock(block, false));
                Assert.Equal(new[] { CertStep }, StepsSince(since));
                Assert.False(ConsensusCertificateVerifier.RefusalDependsOnCommittee(block));
            }

            Assert.True(ValidatorMembershipPull.SecondsUntilDue() > ValidatorMembershipPull.RetrySeconds);
            Assert.Equal(pullsBefore, _pulls);

            // One refused on the committee does: the next pull is brought forward.
            var short1 = NextBlock(_casters.Take(2));
            Assert.False(await BlockValidatorService.ValidateBlock(short1, false));
            Assert.True(ConsensusCertificateVerifier.RefusalDependsOnCommittee(short1));
            Assert.True(ValidatorMembershipPull.SecondsUntilDue() <= ValidatorMembershipPull.RetrySeconds);
        }

        /// <summary>
        /// A block with a fault of its own is refused twice the same way: an unlock with one vote is short under either
        /// record. The newer record gets it a second look and no more, and its producer can be left out for it.
        /// </summary>
        [Fact]
        public async Task ABlockThatIsShortUnderEitherRecord_IsRefusedAfterItsSecondLook()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersHoldTheNewRecord();
            var block = NextBlock(_casters.Take(3), Unlock(_casters.Take(1)));
            var since = DateTime.UtcNow;

            Assert.False(await BlockValidatorService.ValidateBlock(block, false));

            Assert.Equal(new[] { TxStep, TxStep }, StepsSince(since));
            Assert.Equal(_new.RecordSeq, HeldSeq());
            Assert.Equal(block.Height - 1, Tip);
            Assert.True(BlockValidatorService.WasRefusedForItsContents(block.Height, block.Hash));
        }

        /// <summary>
        /// The record can arrive on its own while the block is being judged or just after (an announcement, the minute
        /// pull). The block is then judged once more without a pull being waited for, and the note of a fault in its
        /// contents goes: the newer record is told from the one the refused verdict was reached with, not from what the
        /// node holds once that verdict is in.
        /// </summary>
        [Fact]
        public async Task ARecordThatArrivesAsTheBlockIsRefused_StillGetsItItsSecondLook()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersOnlyClaimANewerRecord();                                   // a pull would bring nothing
            var block = NextBlock(_casters.Take(3), Unlock(_casters.Take(2)));
            var arrived = 0;
            BlockValidatorService.AfterFirstVerdictForTests = () =>
            {
                if (Interlocked.Increment(ref arrived) == 1)
                    Assert.True(CasterMembershipStore.TryAppend(_new, out var why, fromSync: true), why);
            };
            var since = DateTime.UtcNow;
            try
            {
                var taken = await BlockValidatorService.ValidateBlock(block, false);

                Assert.Equal(new[] { TxStep }, StepsSince(since));           // refused once, under the record before the change
                Assert.Equal(1, arrived);
                Assert.True(taken);
                Assert.Equal(block.Height, Tip);
                Assert.Equal(0, _pulls);                                     // the record was already here: no pull was waited for
                Assert.False(BlockValidatorService.WasRefusedForItsContents(block.Height, block.Hash));
            }
            finally { BlockValidatorService.AfterFirstVerdictForTests = null; }
        }

        /// <summary>
        /// The wait for the pull has an end. Peers that never answer keep block validation for the wait and no longer, and
        /// the blocks refused while that pull is still out do not wait for it again.
        /// </summary>
        [Fact]
        public async Task PeersThatNeverAnswer_KeepBlockValidationForTheWaitAndNoLonger()
        {
            await StartWithTheRecordBeforeTheChange();
            var never = new TaskCompletionSource<MembershipRecordResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CasterDiscoveryService.FetchMembershipResponse = (_, _) => { Interlocked.Increment(ref _pulls); return never.Task; };
            var priorWait = ValidatorMembershipPull.Wait;
            ValidatorMembershipPull.Wait = TimeSpan.FromMilliseconds(400);
            try
            {
                var block = NextBlock(_casters.Take(3), Unlock(_casters.Take(2)));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Assert.False(await BlockValidatorService.ValidateBlock(block, false));
                Assert.True(_pulls > 0);                                   // the pull went out
                Assert.InRange(clock.ElapsedMilliseconds, 300, 5_000);     // and was waited for, for the wait

                await Task.Delay(500);                                     // that pull is still out, past the wait
                clock.Restart();
                Assert.False(await BlockValidatorService.ValidateBlock(block, false));
                Assert.True(clock.ElapsedMilliseconds < 300, $"waited {clock.ElapsedMilliseconds} ms for a pull already past the wait");
                Assert.Equal(OldSeq, HeldSeq());
            }
            finally
            {
                never.SetResult(null);
                for (var i = 0; i < 100 && ValidatorMembershipPull.PullRunning; i++) await Task.Delay(20);
                ValidatorMembershipPull.Wait = priorWait;
            }
        }

        // ── The download path ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A node catching up from its peers judges a bridge transaction's votes in every block it downloads, and reaches
        /// the blocks under a record before its minute pull has brought it: the download loop takes the block once the
        /// record is pulled, and goes on.
        /// </summary>
        [Fact]
        public async Task TheDownloadLoop_TakesTheBlockOnceTheNewerRecordIsPulled()
        {
            await StartWithTheRecordBeforeTheChange();
            PeersHoldTheNewRecord();
            Globals.IsChainSynced = false;                       // catching up: certificates are not asked of downloaded blocks
            var block = NextBlock(null, Unlock(_casters.Take(2)));
            Assert.True(BlockStaging.Stage(block, "10.9.0.1"));
            var since = DateTime.UtcNow;

            await Globals.BlocksDownloadSlim.WaitAsync();        // held by the startup download while it runs
            try { await BlockValidatorService.ValidateBlocks(); }
            finally { Globals.BlocksDownloadSlim.Release(); }

            Assert.Equal(new[] { TxStep }, StepsSince(since));
            Assert.Equal(_new.RecordSeq, HeldSeq());
            Assert.Equal(block.Height, Tip);
        }

        // ── Admission ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// At admission a node on the record before the change refuses the unlock too, locally and by the raw route, and
        /// keeps nothing of it; the refusal is the committee's, the one a block's is. With the record adopted both paths
        /// admit it.
        /// </summary>
        [Fact]
        public async Task AtAdmission_LocalAndRaw_TheRefusalIsTheCommittees_AndGoesWithTheNewerRecord()
        {
            await StartWithTheRecordBeforeTheChange();
            var unlock = Unlock(_casters.Take(2));

            async Task<JObject> Raw(VfxTx tx)
            {
                var body = JObject.Parse(JsonConvert.SerializeObject(tx));
                body["Data"] = JToken.Parse(tx.Data!);
                return JObject.Parse(await new TXV1Controller().SendRawTransaction(body));
            }

            var (admitted, why) = await TransactionValidatorService.VerifyTX(unlock);
            Assert.False(admitted);
            Assert.StartsWith(BridgeCasterConsensus.VotesRejectedPrefix, why);
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal(why));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(why));
            var raw = await Raw(unlock);
            Assert.Equal("Fail", raw["Result"]?.ToString());
            Assert.Empty(TransactionData.GetPool().FindAll());

            // In a block the reason is the same one.
            var (inBlock, inBlockWhy) = await TransactionValidatorService.VerifyTX(unlock, false, true, false, null, false, Tip + 1, TimeUtil.GetTime());
            Assert.False(inBlock);
            Assert.Equal(why, inBlockWhy);

            Assert.True(CasterMembershipStore.TryAppend(_new, out var refused, fromSync: true), refused);
            var (now, nowWhy) = await TransactionValidatorService.VerifyTX(unlock);
            Assert.True(now, nowWhy);
            Assert.Equal("Success", (await Raw(unlock))["Result"]?.ToString());
            Assert.Single(TransactionData.GetPool().FindAll());
        }

        /// <summary>
        /// Which refusals are the committee's. A sender outside it and a count short of its majority are; so is a count
        /// that also left a vote out for its time, though that one may come from a stored exit and still counts as a
        /// possible sign of a damaged state. A refusal that reads the transaction alone is none of these.
        /// </summary>
        [Fact]
        public async Task TheCommitteesRefusals_AreTheSenderAndTheCount_NotWhatTheTransactionAloneDecides()
        {
            await StartWithTheRecordBeforeTheChange();

            // Sent by someone outside the committee (as a caster that the newer record adds would be, on a node behind it).
            var outsider = Unlock(_casters.Take(3));
            outsider.FromAddress = outsider.ToAddress = _producer.Address;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _producer.Address, Balance = 10M, Nonce = 0 });
            outsider.Build();
            outsider.Signature = SignatureService.CreateSignature(outsider.Hash, _producer.Key, _producer.Pub);
            var (_, sender) = await TransactionValidatorService.VerifyTX(outsider, false, true, false, null, false, Tip + 1, TimeUtil.GetTime());
            Assert.Equal(BridgeTxRules.SubmitterNotInCommitteeReason, sender);
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal(sender));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(sender));

            var count = $"{BridgeCasterConsensus.VotesRejectedPrefix}exit to BTC: 2/3 valid bound {BridgeCasterConsensus.TooFewVotesMarker}";
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal(count));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal(count));
            Assert.False(BlockValidatorService.IsStateCorruptionSignal($"{count}; 1 {BridgeCasterConsensus.VoteForOtherHandlerReason}"));

            var withTime = $"{count}; 1 {BridgeCasterConsensus.VoteOutsideAttemptTimeReason}";
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal(withTime));
            Assert.True(BlockValidatorService.IsStateCorruptionSignal(withTime));

            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal($"{BridgeCasterConsensus.VotesRejectedPrefix}pool unlock: {BridgeCasterConsensus.VoteListedTwiceReason}"));
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal($"{BridgeCasterConsensus.VotesRejectedPrefix}VBTCb unlock: {BridgeCasterConsensus.CommitteeUnavailableReason}"));
            Assert.True(BridgeCasterConsensus.IsCommitteeRefusal(BridgeTxRules.CommitteeUnavailableReason));

            foreach (var own in new[] { "no votes", "amount must be positive", "destination required" })
            {
                var reason = $"{BridgeCasterConsensus.VotesRejectedPrefix}pool unlock: {own}";
                Assert.False(BridgeCasterConsensus.IsCommitteeRefusal(reason));
                Assert.True(BlockValidatorService.IsStateCorruptionSignal(reason));
            }
            Assert.False(BridgeCasterConsensus.IsCommitteeRefusal("Bridge transaction has no sender."));
            Assert.False(BridgeCasterConsensus.IsCommitteeRefusal("This is a new account with no balance."));
            Assert.False(BridgeCasterConsensus.IsCommitteeRefusal(null));
        }
    }
}
