using System;
using System.Collections.Concurrent;
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
using VfxBlock = VerifiedXCore.Models.Block;
using VfxTx = VerifiedXCore.Models.Transaction;
using SignatureService = VerifiedXCore.Services.SignatureService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A chain at its tip with a caster committee in this node's membership record, blocks produced and signed as a
    /// producer serves them, and the membership chain height set for the test. Blocks go through
    /// BlockValidatorService.ValidateBlock, transactions through TransactionValidatorService.VerifyTX.
    /// </summary>
    public abstract class MembershipChainTestBase : BtcAttestTestBase
    {
        protected const string Vault = "0123456789abcdef0123456789abcdef:1790600000";
        protected const string HashStep = "BlockValidatorService.ValidateBlock()-10";
        protected const string RunStep = "BlockValidatorService.ValidateBlock()-membershipRun";
        protected const string TxStep = "BlockValidatorService.ValidateBlock()-13";
        protected const string CertStep = "BlockValidatorService.ValidateBlock()-cert";

        private readonly long _priorChainHeight = Globals.MembershipChainHeight;
        private readonly string _priorAnchor = Globals.MembershipAnchorRecordHash;
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

        /// <summary>Five casters; the committee this node holds is the first four.</summary>
        protected readonly List<V> Casters = Enumerable.Range(0, 5).Select(_ => NewKey()).ToList();
        protected readonly V Producer = NewKey();

        protected MembershipChainTestBase() : base(realMainnetRules: false)
        {
            Globals.BridgeBurnBindingHeight = 1;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BridgeVotesBoundToAttemptHeight = long.MaxValue;
            Globals.BTCNetwork = Network.Main;
            Globals.IsChainSynced = true;
            Globals.IsBlockCaster = false;
            Globals.ValidatorAddress = "";
            Globals.CertEnforceHeight = long.MaxValue;
            Globals.BlockCasters = new ConcurrentBag<Peers>();
            Globals.MembershipChainHeight = long.MaxValue;
            Globals.MembershipAnchorRecordHash = "";
            BlockchainData.ChainRef = "test-chain";
            CasterMembershipStore.InvalidateCache();
            ValidatorMembershipPull.ResetForTests();
            // No peer answers a pull: what this node holds is what it judges by.
            CasterDiscoveryService.FetchMembershipResponse = (_, _) => Task.FromResult<MembershipRecordResponse?>(null);
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = Vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = Minter.Address, OwnerAddress = Minter.Address });
            foreach (var caster in Casters)
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
            Globals.MembershipChainHeight = _priorChainHeight;
            Globals.MembershipAnchorRecordHash = _priorAnchor;
            CasterMembershipStore.InvalidateCache();
            base.Dispose();
        }

        // ── Records ──────────────────────────────────────────────────────────────────────────────

        protected static long Tip => Globals.LastBlock.Height;

        protected static CasterMembershipRecord Record(long seq, long effective, string prev, IEnumerable<V> members, string change, string changed = "")
        {
            var r = new CasterMembershipRecord
            {
                RecordSeq = seq, EffectiveFromHeight = effective, PrevRecordHash = prev, ChangeType = change, ChangedAddress = changed,
                Casters = members.Select(m => new CasterInfo { Address = m.Address, PeerIP = "10.9.0." + (m.Address.GetHashCode() & 0x7f), PublicKey = m.Pub })
                    .OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
            };
            r.RecordHash = CasterMembershipStore.ComputeRecordHash(r);
            return r;
        }

        /// <summary>A record signed by <paramref name="signers"/>.</summary>
        protected static CasterMembershipRecord Signed(CasterMembershipRecord r, IEnumerable<V> signers)
        {
            var payload = CasterMembershipStore.CanonicalPayload(r);
            r.Signatures = signers.Select(m => new RecordSignature { SignerAddress = m.Address, Signature = SignatureService.CreateSignature(payload, m.Key, m.Pub) }).ToList();
            return r;
        }

        /// <summary>The record after <paramref name="prev"/> with <paramref name="members"/>, signed by a majority of prev's set.</summary>
        protected CasterMembershipRecord Next(CasterMembershipRecord prev, IEnumerable<V> members, string change, string changed, long effective)
        {
            var r = Record(prev.RecordSeq + 1, effective, prev.RecordHash, members, change, changed);
            var prevSet = prev.Casters.Select(c => c.Address).ToHashSet();
            return Signed(r, Casters.Where(c => prevSet.Contains(c.Address)).Take(prevSet.Count / 2 + 1));
        }

        /// <summary>Puts a record in this node's store as if it had been adopted.</summary>
        protected static void Hold(CasterMembershipRecord r)
        {
            DbContext.DB_Config!.GetCollection<CasterMembershipRecord>("rsrv_caster_membership").Upsert(r.RecordSeq, r);
            CasterMembershipStore.InvalidateCache();
        }

        protected static long HeldSeq() => CasterMembershipStore.GetCurrent()?.RecordSeq ?? -1;

        // ── Blocks ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The next block, as its producer serves it: these transactions, this run, and a certificate by <paramref name="attestors"/> (none when null).</summary>
        protected VfxBlock NextBlock(IEnumerable<V>? attestors, List<CasterMembershipRecord>? run, params VfxTx[] transactions)
        {
            var height = Tip + 1;
            var prev = Globals.LastBlock.Hash;
            var reward = new VfxTx
            {
                FromAddress = "Coinbase_BlkRwd", ToAddress = Producer.Address, TransactionType = TransactionType.TX, Timestamp = TimeUtil.GetTime(),
                Amount = BlockVersionUtility.GetBlockVersion(height) < 4 ? BlockchainData.GetBlockReward() : 0.0M,
            };
            reward.Build();
            var txs = new List<VfxTx> { reward };
            txs.AddRange(transactions);
            var block = new VfxBlock
            {
                Height = height, Version = BlockVersionUtility.GetBlockVersion(height), PrevHash = prev, Timestamp = TimeUtil.GetTime(), NumOfTx = txs.Count,
                Validator = Producer.Address, ValidatorAnswer = ProofUtility.ComputeVrf(Producer.Pub, height, VrfSeed.ThirdInput(height, prev) ?? prev).ProofHash,
                ChainRefId = "test-chain", Transactions = txs, MembershipRun = run,
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, Producer.Key, Producer.Pub);
            if (attestors != null)
                Certify(block, attestors);
            return block;
        }

        protected static void Certify(VfxBlock block, IEnumerable<V> attestors)
        {
            var msg = ConsensusMessageFormatter.FormatAttestationV1(block.Height, block.Hash, block.Validator, block.PrevHash);
            block.ConsensusCertificate = new ConsensusCertificate
            {
                BlockHeight = block.Height, BlockHash = block.Hash, WinnerAddress = block.Validator, PrevHash = block.PrevHash,
                Attestations = attestors.Select(m => new CasterAttestation { CasterAddress = m.Address, Signature = SignatureService.CreateSignature(msg, m.Key, m.Pub) }).ToList(),
            };
        }

        /// <summary>A copy of a block as a peer would pass it on: the same JSON, read back.</summary>
        protected static VfxBlock Relayed(VfxBlock block) => JsonConvert.DeserializeObject<VfxBlock>(JsonConvert.SerializeObject(block))!;

        protected static List<string> StepsSince(DateTime since) =>
            BlockDiagnostics.RollbackSnapshot().Where(r => r.TimeUtc >= since).Select(r => r.Location).ToList();

        // ── Bridge transactions ──────────────────────────────────────────────────────────────────

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

        /// <summary>A pool unlock sent by <paramref name="sender"/> (the first caster by default), voted by <paramref name="voters"/>.</summary>
        protected VfxTx Unlock(IEnumerable<V> voters, V? sender = null, decimal amount = 0.004M)
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
            sender ??= Casters[0];
            var nonce = StateData.GetSpecificAccountStateTrei(sender.Address)?.Nonce ?? 0;
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = sender.Address, ToAddress = sender.Address, Amount = 0.0M, Fee = 0.0M, Nonce = nonce,
                TransactionType = TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, Data = data.ToString(Formatting.None),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }
    }
}
