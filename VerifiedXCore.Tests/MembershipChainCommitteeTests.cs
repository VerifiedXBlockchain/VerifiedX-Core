using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From the membership chain's start the caster committee for a height is the one the chain carried, whatever record
    /// this node holds: who may attest a block and how many must, whose votes a bridge unlock counts and who may send one,
    /// the weight fork choice gives a certificate. A node that never received a demotion judges the block after it as its
    /// peers do. Through BlockValidatorService.ValidateBlock at the synced tip, ValidateBlocks as the download runs it,
    /// TransactionValidatorService.VerifyTX, BlockRollbackUtility and SnapshotRestoreUtility.
    /// </summary>
    [Collection("DbContextSequential")]
    public class MembershipChainCommitteeTests : MembershipChainTestBase
    {
        private CasterMembershipRecord _anchor = null!;
        private int _pulls;

        public MembershipChainCommitteeTests()
        {
            CasterDiscoveryService.FetchMembershipResponse = (_, _) => { Interlocked.Increment(ref _pulls); return Task.FromResult<MembershipRecordResponse?>(null); };
        }

        private List<V> Old => Casters.Take(4).ToList();
        private List<V> New => Casters.Take(3).ToList();

        /// <summary>The chain has started from the record of <paramref name="members"/>, which this node holds too (unless <paramref name="holdIt"/> is false).</summary>
        private async Task StartTheChain(IEnumerable<V> members, bool holdIt = true)
        {
            await Start();
            var list = members.ToList();
            _anchor = Record(3, 50, "prev", list, "Promotion", list.Last().Address);
            if (holdIt) Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
            Assert.True(await Take(NextBlock(null, new List<CasterMembershipRecord> { _anchor })));
            Assert.True(MembershipChain.ActiveAt(Tip + 1));
        }

        private static async Task<bool> Take(Block block) => await BlockValidatorService.ValidateBlock(Relayed(block), false);

        /// <summary>
        /// This node's records go back to the anchor, as on a node the record never reached. (A written block's records go
        /// into this node's own; here they are taken out again, so what decides is shown to be the chain.)
        /// </summary>
        private void BackToTheAnchor()
        {
            var col = DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership");
            col.DeleteMany(x => x.RecordSeq > _anchor.RecordSeq);
            CasterMembershipStore.InvalidateCache();
            Assert.Equal(_anchor.RecordSeq, HeldSeq());
        }

        /// <summary>The block that carries the demotion of the fourth caster, certified by the committee before it.</summary>
        private async Task<CasterMembershipRecord> CarryTheDemotion()
        {
            var demotion = Next(_anchor, New, "Demotion", Casters[3].Address, Tip + 1);
            Assert.True(await Take(NextBlock(Old.Take(3), new List<CasterMembershipRecord> { demotion })));
            return demotion;
        }

        // ── The issue: a node that never received the demotion ───────────────────────────────────

        /// <summary>
        /// The committee drops from four casters to three, and this node never received the demotion: it still holds the
        /// four-caster record. The block after the one that carried the demotion is certified by two of the three and its
        /// pool unlock carries two votes, a majority of three and not of four. The node takes it, asking its peers for
        /// nothing, because the committee is the one the chain carried.
        /// </summary>
        [Fact]
        public async Task ANodeThatNeverReceivedTheDemotion_TakesTheBlockTheNewCommitteeCertifiedAndVoted()
        {
            await StartTheChain(Old);
            await CarryTheDemotion();
            BackToTheAnchor();                                            // this node's own record is the four-caster one
            Assert.Equal(2, BridgeCasterConsensus.RequiredVotesFor(BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1)));
            var pullsBefore = _pulls;

            var block = NextBlock(New.Take(2), null, Unlock(New.Take(2)));
            Assert.True(await Take(block));

            Assert.Equal(block.Height, Tip);
            Assert.Equal(pullsBefore, _pulls);
        }

        /// <summary>The same block on a node with no validator key and no membership record at all, catching up by download.</summary>
        [Fact]
        public async Task ANodeWithNoRecordAtAll_DownloadingTheChain_JudgesTheUnlockByTheCarriedCommittee()
        {
            await StartTheChain(Old, holdIt: false);
            await CarryTheDemotion();
            Assert.Null(CasterMembershipStore.GetCurrent());
            Globals.IsChainSynced = false;                                // catching up: certificates are not asked of downloaded blocks
            var block = NextBlock(null, null, Unlock(New.Take(2)));
            Assert.True(BlockStaging.Stage(block, "10.9.0.1"));

            await Globals.BlocksDownloadSlim.WaitAsync();
            try { await BlockValidatorService.ValidateBlocks(); }
            finally { Globals.BlocksDownloadSlim.Release(); }

            Assert.Equal(block.Height, Tip);
        }

        /// <summary>The other way: a promotion from three to four. A node holding only the three-caster record refuses an unlock two of the four voted.</summary>
        [Fact]
        public async Task AfterACarriedPromotion_ANodeOnTheSmallerRecordStillRefusesAnUnlockTheOldMajorityVoted()
        {
            await StartTheChain(New);
            var promotion = Next(_anchor, Old, "Promotion", Casters[3].Address, Tip + 1);
            Assert.True(await Take(NextBlock(New.Take(2), new List<CasterMembershipRecord> { promotion })));
            BackToTheAnchor();

            var unlock = Unlock(New.Take(2));
            var (ok, why) = await TransactionValidatorService.VerifyTX(unlock, false, true, false, null, false, Tip + 1, TimeUtil.GetTime());
            Assert.False(ok);
            Assert.Contains("2/3", why);

            var since = DateTime.UtcNow;
            Assert.False(await Take(NextBlock(Old.Take(3), null, unlock)));
            Assert.Equal(new[] { TxStep }, StepsSince(since));            // judged once: no newer record could change it
            Assert.True(await Take(NextBlock(Old.Take(3), null, Unlock(Old.Take(3)))));
        }

        // ── Certificates ────────────────────────────────────────────────────────────────────────

        /// <summary>The carrying block is certified by the committee before it; the committee it installs certifies from the next block.</summary>
        [Fact]
        public async Task TheCarrierIsCertifiedByTheCommitteeBeforeIt_AndTheNextBlockByTheNewOne()
        {
            await StartTheChain(Old);
            var demotion = Next(_anchor, New, "Demotion", Casters[3].Address, Tip + 1);
            var since = DateTime.UtcNow;
            Assert.False(await Take(NextBlock(New.Take(2), new List<CasterMembershipRecord> { demotion }))); // two of four
            Assert.Equal(new[] { CertStep }, StepsSince(since));
            Assert.True(await Take(NextBlock(New, new List<CasterMembershipRecord> { demotion })));

            since = DateTime.UtcNow;
            Assert.False(await Take(NextBlock(new[] { Casters[3] }.Concat(New.Take(1)), null))); // the demoted caster does not count
            Assert.Equal(new[] { CertStep }, StepsSince(since));
            Assert.False(await Take(NextBlock(null, null)));             // from the chain's start a block needs a certificate on every node
            Assert.True(await Take(NextBlock(New.Take(2), null)));
        }

        /// <summary>Fork choice weighs a competing block's certificate by the chain's committee, not this node's record.</summary>
        [Fact]
        public async Task ForkChoice_WeighsACertificateByTheChainsCommittee()
        {
            await StartTheChain(Old);
            await CarryTheDemotion();
            var competing = NextBlock(Old.Skip(1), null);                  // casters 1, 2 and the demoted 3
            Assert.Equal(2, ForkChoiceUtility.CountValidCertWeight(competing));
            Assert.Equal(New.Select(c => c.Address).OrderBy(a => a), ConsensusCertificateVerifier.AttestorSetFor(competing).OrderBy(a => a));
        }

        // ── Rollback and restore ────────────────────────────────────────────────────────────────

        /// <summary>A rollback below the carrying block takes its records with it; a block in its place without them keeps the old committee.</summary>
        [Fact]
        public async Task ARollbackBelowTheCarrier_GivesTheOldCommitteeBack()
        {
            await StartTheChain(Old);
            await CarryTheDemotion();
            Assert.Equal(3, BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1).Count);

            Assert.True(await BlockRollbackUtility.RollbackBlocksFast(1));
            Assert.Equal(4, BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1).Count);

            Assert.True(await Take(NextBlock(Old.Take(3), null)));         // the branch it moved to carried nothing
            Assert.Equal(4, BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1).Count);
            var (ok, _) = await TransactionValidatorService.VerifyTX(Unlock(New.Take(2)), false, true, false, null, false, Tip + 1, TimeUtil.GetTime());
            Assert.False(ok);
        }

        /// <summary>A snapshot restore to below the carrying block: the committee is the one the restored chain carried.</summary>
        [Fact]
        public async Task ASnapshotRestoreBelowTheCarrier_GivesTheOldCommitteeBack()
        {
            await StartTheChain(Old);
            StateTreiStatusService.SetSynced(Tip);
            var slotHeight = Tip;
            await StateSnapshotService.UpdateCycleAsync(slotHeight, Globals.LastBlock.Hash);
            Assert.Equal(slotHeight, StateSnapshotService.PickVerifiedSlotForRestore(slotHeight)?.Height);
            await CarryTheDemotion();
            Assert.True(await Take(NextBlock(New.Take(2), null)));
            Assert.Equal(3, BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1).Count);

            Assert.True(await SnapshotRestoreUtility.TryRestoreAsync(slotHeight));

            Assert.Equal(slotHeight, Tip);
            Assert.Equal(4, BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1).Count);
        }

        // ── Seed resets ride the carried record ─────────────────────────────────────────────────

        /// <summary>
        /// From the chain's start a seed reset is judged as a carried record: the hard-coded seed set and two seed signatures.
        /// Each seed checked the stall on its own clock before it signed; the block reads no clock. One without the seeds'
        /// signatures is not valid and is taken off the block.
        /// </summary>
        [Fact]
        public async Task ASeedReset_IsJudgedOnTheSeedsSignatures_NotOnThisNodesClock()
        {
            await Start();
            var seeds = SeedNodeService.GetBootstrapSeedPeers().Where(p => !string.IsNullOrEmpty(p.ValidatorAddress))
                .Select(p => new CasterInfo { Address = p.ValidatorAddress!, PeerIP = (p.PeerIP ?? "").Replace("::ffff:", ""), PublicKey = p.ValidatorPublicKey ?? "" })
                .OrderBy(c => c.Address, StringComparer.Ordinal).ToList();
            CasterMembershipRecord ResetAfter(CasterMembershipRecord prev)
            {
                var r = new CasterMembershipRecord
                {
                    RecordSeq = prev.RecordSeq + 1, EffectiveFromHeight = Tip + 1, PrevRecordHash = prev.RecordHash, ChangeType = CasterMembershipStore.BootstrapResetChangeType,
                    Casters = seeds, Signatures = Old.Take(2).Select(m => new RecordSignature { SignerAddress = m.Address, Signature = "00" }).ToList(),
                };
                r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
                return r;
            }
            var priorVree = Globals.VreeActivationHeight;
            Globals.VreeActivationHeight = 1;
            try
            {
                // Its refusal is the signatures', with any stall: no clock is read for it.
                var reset = ResetAfter(Record(3, 50, "prev", Old, "Promotion", Casters[3].Address));
                Assert.False(CasterMembershipStore.ValidateBootstrapReset(reset, long.MaxValue, out var why));
                Assert.Contains("signatures 0/2 from seeds", why);

                // Before the chain starts a reset is never a carried record.
                var before = NextBlock(null, null);
                before.MembershipRecord = reset;
                Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(before));

                Globals.VreeActivationHeight = priorVree;
                await StartTheChain(Old);
                Globals.VreeActivationHeight = 1;
                var after = NextBlock(Old.Take(3), null);
                after.MembershipRecord = ResetAfter(_anchor);
                Assert.Equal(VreeElection.CarriedVerdict.Invalid, VreeElection.JudgeCarried(after));
                Assert.True(await Take(after));                            // taken off, and the block judged on its own
                Assert.Null(BlockchainData.GetBlockByHeight(after.Height)!.MembershipRecord);
                Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
            }
            finally { Globals.VreeActivationHeight = priorVree; }
        }

        /// <summary>
        /// This node holds a seed reset (as it would an election or a rotation) that takes effect at the next block, and the
        /// next block does not carry it: the outgoing committee certified it. A node without the reset takes that block, and
        /// so must this one, or one block gets two verdicts. The block is judged by the chain's committee; the held record is
        /// not put on it. Outside block judging (this node's own round) the held record is still the one it casts under.
        /// </summary>
        [Fact]
        public async Task ARecordThisNodeHolds_ForTheNextBlock_DoesNotChangeTheVerdictOnABlockThatDoesNotCarryIt()
        {
            var priorVree = Globals.VreeActivationHeight;
            Globals.VreeActivationHeight = 1;
            try
            {
                await StartTheChain(Old);
                var seeds = SeedNodeService.GetBootstrapSeedPeers().Where(p => !string.IsNullOrEmpty(p.ValidatorAddress))
                    .Select(p => new CasterInfo { Address = p.ValidatorAddress!, PeerIP = (p.PeerIP ?? "").Replace("::ffff:", ""), PublicKey = p.ValidatorPublicKey ?? "" })
                    .OrderBy(c => c.Address, StringComparer.Ordinal).ToList();
                var reset = new CasterMembershipRecord
                {
                    RecordSeq = _anchor.RecordSeq + 1, EffectiveFromHeight = Tip + 1, PrevRecordHash = _anchor.RecordHash,
                    ChangeType = CasterMembershipStore.BootstrapResetChangeType, Casters = seeds,
                };
                reset.RecordHash = CasterMembershipStore.ComputeRecordHash(reset);
                Hold(reset);
                Assert.Equal(reset.RecordHash, CasterMembershipStore.GetForHeight(Tip + 1)!.RecordHash); // its own round

                var block = NextBlock(Old.Take(3), null);
                Assert.True(await Take(block));

                Assert.Null(BlockchainData.GetBlockByHeight(block.Height)!.MembershipRecord);
                Assert.Equal(_anchor.RecordHash, MembershipChain.RecordFor(Tip + 1)!.RecordHash);
            }
            finally { Globals.VreeActivationHeight = priorVree; }
        }

        // ── Below the chain's start nothing changes ─────────────────────────────────────────────

        [Fact]
        public async Task BeforeTheChainStarts_TheRecordThisNodeHoldsStillDecides()
        {
            await Start();
            _anchor = Record(3, 50, "prev", Old, "Promotion", Casters[3].Address);
            Hold(_anchor);
            Globals.MembershipChainHeight = Tip + 1;
            Globals.MembershipAnchorRecordHash = _anchor.RecordHash;
            Assert.False(MembershipChain.ActiveAt(Tip + 1));
            Assert.Equal(3, BridgeCasterConsensus.RequiredVotesFor(BridgeCasterConsensus.GetCommitteeForHeight(Tip + 1)));
            Assert.True(await Take(NextBlock(null, null)));                 // no certificate armed on this node, no chain yet
        }
    }
}
