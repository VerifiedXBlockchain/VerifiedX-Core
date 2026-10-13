using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The validator stake is bonded (owner decision, 13 October 2026). A registered validator's 5,000 VFX cannot be
    /// sent while it is registered and for 1,200 blocks after its exit is mined; and a validator counts for a vault's
    /// key ceremony, and among an epoch's Bitcoin report signers, only once that stake has been locked for two days,
    /// and only while it is still active.
    ///
    /// Before this the stake was looked at when a vault's key ceremony was judged and when an epoch's Bitcoin report
    /// signers were listed, and nothing kept it there: it was needed for one block and could leave in the next, while
    /// the key share or the place among the signers stayed.
    ///
    /// Transactions are judged as block validation judges them (VerifyTX at a block height) and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ValidatorBondTests : IDisposable
    {
        private const long Rule = 200;
        private const long Grandfather = 180;
        private const long DkgRule = 50;
        private const decimal Stake = 5_000M;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.ValidatorBondHeight;
        private readonly long _priorGrandfather = Globals.ValidatorBondGrandfatherHeight;
        private readonly long _priorDkgRule = Globals.VbtcV2DkgAttestationHeight;
        private readonly long _priorBurnRule = Globals.VaultBurnRuleHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;

        private sealed record V(PrivateKey Key, string Pub, string Address);
        private readonly List<V> _old = Enumerable.Range(0, 10).Select(_ => NewKey()).ToList();   // validators from before the rule
        private readonly V _poor = NewKey();     // registered before the rule, no longer holding the stake
        private readonly V _owner = NewKey();
        private readonly V _friend = NewKey();
        private readonly string _groupKey;
        private readonly string _address;

        public ValidatorBondTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"valbond_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.ValidatorBondHeight = Rule;
            Globals.ValidatorBondGrandfatherHeight = Grandfather;   // the validators registered at blocks 150-160 were validators then
            Globals.VbtcV2DkgAttestationHeight = DkgRule;
            Globals.VaultBurnRuleHeight = long.MaxValue;
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
            Globals.LastBlock = new Block { Height = 198 };

            long h = 150;
            foreach (var v in _old.Append(_poor)) Lifecycle(v, TransactionType.VBTC_V2_VALIDATOR_REGISTER, h++);
            foreach (var v in _old) Fund(v, Stake + 10M);
            Fund(_poor, 100M);
            Fund(_owner, 100M);
            Fund(_friend, 1M);

            var xOnly = new NBitcoin.Key().PubKey.ToBytes()[1..];
            _groupKey = Convert.ToHexString(xOnly).ToLowerInvariant();
            _address = FrostDkgAttestation.DeriveTaprootAddress(_groupKey, NBitcoin.Network.Main)!;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.ValidatorBondHeight = _priorRule;
            Globals.ValidatorBondGrandfatherHeight = _priorGrandfather;
            Globals.VbtcV2DkgAttestationHeight = _priorDkgRule;
            Globals.VaultBurnRuleHeight = _priorBurnRule;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static V NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return new V(key, pub, AccountData.GetHumanAddress(pub));
        }

        private static long Tip => Globals.LastBlock.Height;

        private static void Fund(V who, decimal balance)
        {
            var accounts = StateData.GetAccountStateTrei();
            var existing = StateData.GetSpecificAccountStateTrei(who.Address);
            if (existing == null) accounts.InsertSafe(new AccountStateTrei { Key = who.Address, Balance = balance, Nonce = 0 });
            else { existing.Balance = balance; accounts.UpdateSafe(existing); }
        }

        private static decimal Balance(V who) => StateData.GetSpecificAccountStateTrei(who.Address)!.Balance;

        private static Transaction LifecycleTx(V v, TransactionType type)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = v.Address, ToAddress = v.Address, Amount = 0.0M, Fee = 0.0M, Nonce = 0, TransactionType = type,
                Data = JsonConvert.SerializeObject(new { ValidatorAddress = v.Address, IPAddress = "8.8.8.8", FrostPublicKey = v.Pub, IsS3C = false }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, v.Key, v.Pub);
            return tx;
        }

        /// <summary>A registration, heartbeat or exit as a block of the chain holds it (what the validator registry reads), without applying it.</summary>
        private static void Lifecycle(V v, TransactionType type, long height) =>
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = height, Hash = $"h{height}-{Guid.NewGuid():N}", Transactions = new List<Transaction> { LifecycleTx(v, type) } });

        private static Transaction Send(V from, V to, decimal amount)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = from.Address, ToAddress = to.Address, Amount = amount, Fee = 0, Nonce = 0, TransactionType = TransactionType.TX,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Why)> Verify(Transaction tx, long height)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, why);
        }

        /// <summary>Mines a block: it joins the chain's blocks (the registry reads those) and is applied to state.</summary>
        private static async Task Apply(long height, params Transaction[] txs)
        {
            var block = new Block { Height = height, Hash = $"b{height}-{Guid.NewGuid():N}", StateRoot = "root" + height, Timestamp = TimeUtil.GetTime(), Transactions = txs.ToList() };
            BlockchainData.GetBlocks().InsertSafe(block);
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
        }

        /// <summary>The rule starts at block 200 and the stakes are looked at in block 201.</summary>
        private static async Task StartTheRule()
        {
            await Apply(Rule);
            await Apply(Rule + 1);
        }

        /// <summary>Every validator given sends a heartbeat shortly before <paramref name="height"/> (they count as active for 1,000 blocks).</summary>
        private static void KeepActive(long height, IEnumerable<V> validators)
        {
            var h = height - 40;
            foreach (var v in validators) Lifecycle(v, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT, h++);
        }

        // ── The lock ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task ValidatorsActiveWhenTheRuleStarts_AreBonded_AndCannotSendTheirStake()
        {
            var v = _old[0];
            // Before the rule: nothing is bonded, and a validator can send everything it has, stake included.
            await Apply(199);
            Assert.Null(ValidatorBond.Row(v.Address));
            Assert.True((await Verify(Send(v, _friend, 5_000M), 199)).Ok);
            Assert.Equal(0M, ValidatorBond.LockedAmount(v.Address, 199));

            await Apply(Rule);
            foreach (var each in _old.Append(_poor))
            {
                var row = ValidatorBond.Row(each.Address)!;
                Assert.Equal(Rule, row.BondedAtHeight);
                Assert.Equal(0, row.ExitHeight);
                Assert.Equal(Rule, row.LastModifiedHeight);              // stamped: snapshots and the rollback guard see it
            }
            Assert.Null(ValidatorBond.Row(_owner.Address));              // not a validator: no bond
            Assert.True(BtcAttestStore.ChangedAbove(Rule - 1));
            Assert.Equal(Stake, ValidatorBond.LockedAmount(v.Address, Rule + 1));

            // It holds 5,010. What is above the stake can be sent; the stake cannot, in a block or at admission.
            var (ok, why) = await Verify(Send(v, _friend, 5M), Rule + 1);
            Assert.True(ok, why);
            (ok, why) = await Verify(Send(v, _friend, 20M), Rule + 1);
            Assert.False(ok);
            Assert.Contains("is a registered validator", why);
            Assert.Contains("bonded and cannot be sent", why);
            var admitted = await TransactionValidatorService.VerifyTX(Send(v, _friend, 5_000M));
            Assert.False(admitted.Item1);
            Assert.Contains("bonded and cannot be sent", admitted.Item2);
            // Exactly what is above the stake: 10 VFX, and the fee comes out of that too.
            var nearlyAll = Send(v, _friend, 9.99M);
            Assert.True(nearlyAll.Fee > 0M && nearlyAll.Amount + nearlyAll.Fee < 10M);
            (ok, why) = await Verify(nearlyAll, Rule + 1);
            Assert.True(ok, why);
            Assert.False((await Verify(Send(v, _friend, 10M), Rule + 1)).Ok);   // 10 and its fee would dip into the stake

            // Two sends that each leave the stake alone, and together do not: refused as a block's transactions together.
            var state = new SameBlockDebitGuard.State { BlockHeight = Rule + 1 };
            Assert.True(SameBlockDebitGuard.TryRegister(Send(v, _friend, 6M), state).Ok);
            var (both, bothWhy) = SameBlockDebitGuard.TryRegister(Send(v, _owner, 6M), state);
            Assert.False(both);
            Assert.StartsWith(SameBlockDebitGuard.ReasonPrefix, bothWhy);

            // Somebody who is not a validator is not touched.
            Assert.True((await Verify(Send(_owner, _friend, 99M), Rule + 1)).Ok);
            // A validator that no longer held its stake when the rule started can send nothing; it can still exit.
            Assert.False((await Verify(Send(_poor, _friend, 1M), Rule + 1)).Ok);
            Assert.True(ValidatorBond.SpendRefusal(LifecycleTx(_poor, TransactionType.VBTC_V2_VALIDATOR_EXIT), Rule + 1) == null);

            // And the send that is allowed goes through.
            await Apply(Rule + 1, Send(v, _friend, 5M));
            Assert.True(Balance(v) >= Stake);
        }

        [Fact]
        public async Task AnExitStartsTheWait_AndTheStakeUnlocksTwelveHundredBlocksAfterIt()
        {
            await StartTheRule();
            var v = _old[0];
            var exit = LifecycleTx(v, TransactionType.VBTC_V2_VALIDATOR_EXIT);
            const long exitedAt = 300;
            await Apply(exitedAt, exit);
            var row = ValidatorBond.Row(v.Address)!;
            Assert.Equal(exitedAt, row.ExitHeight);
            Assert.Equal(exitedAt + ValidatorBond.ExitWaitBlocks, ValidatorBond.UnlockHeight(row));

            // Exited, and still bonded for the wait.
            var (ok, why) = await Verify(Send(v, _friend, 5_000M), exitedAt + 1);
            Assert.False(ok);
            Assert.Contains($"stays bonded until block {exitedAt + 1_200}", why);
            Assert.False((await Verify(Send(v, _friend, 5_000M), exitedAt + 1_199)).Ok);
            // From the 1,200th block after the exit it is its owner's again.
            Assert.True((await Verify(Send(v, _friend, 5_000M), exitedAt + 1_200)).Ok);
            Assert.Equal(0M, ValidatorBond.LockedAmount(v.Address, exitedAt + 1_200));

            // It is no longer an active validator, so it counts for no key ceremony either.
            Assert.DoesNotContain(ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(exitedAt), exitedAt + 1), e => e.ValidatorAddress == v.Address);
            Assert.False(ValidatorBond.IsAged(v.Address, exitedAt + 1));

            // A second exit changes nothing (the wait is counted from the first).
            await Apply(exitedAt + 10, LifecycleTx(v, TransactionType.VBTC_V2_VALIDATOR_EXIT));
            Assert.Equal(exitedAt, ValidatorBond.Row(v.Address)!.ExitHeight);
        }

        [Fact]
        public async Task AHeartbeatDuringTheWait_ResumesTheBond_AndRegisteringAfterAFullExit_StartsANewOne()
        {
            await StartTheRule();
            var (back, gone) = (_old[1], _old[2]);
            var lockedSince = ValidatorBond.Row(back.Address)!.FundedSinceHeight;
            await Apply(300, LifecycleTx(back, TransactionType.VBTC_V2_VALIDATOR_EXIT), LifecycleTx(gone, TransactionType.VBTC_V2_VALIDATOR_EXIT));

            // One changes its mind inside the wait: the stake never left, so the bond and its age carry on.
            await Apply(900, LifecycleTx(back, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT));
            var resumed = ValidatorBond.Row(back.Address)!;
            Assert.Equal(0, resumed.ExitHeight);
            Assert.Equal(Rule, resumed.BondedAtHeight);
            Assert.Equal(lockedSince, resumed.FundedSinceHeight);
            Assert.False((await Verify(Send(back, _friend, 5_000M), 300 + 1_200)).Ok);   // past the old unlock height: still bonded

            // The other waits it out, takes the stake back, and later registers again: a new bond, with no age.
            Assert.True((await Verify(Send(gone, _friend, 5_000M), 1_500)).Ok);
            await Apply(1_600, LifecycleTx(gone, TransactionType.VBTC_V2_VALIDATOR_REGISTER));
            var again = ValidatorBond.Row(gone.Address)!;
            Assert.Equal(1_600, again.BondedAtHeight);
            Assert.Equal(0, again.ExitHeight);
            Assert.Equal(0, again.FundedSinceHeight);
            Assert.False((await Verify(Send(gone, _friend, 5_000M), 1_601)).Ok);
        }

        [Fact]
        public async Task GoingQuiet_UnlocksNothing_OnlyTheExitDoes()
        {
            await StartTheRule();
            var v = _old[3];
            // Thousands of blocks without a heartbeat: the registry no longer lists it as active...
            await Apply(5_000);
            Assert.DoesNotContain(VBTCValidatorRegistry.GetActiveValidatorsAt(5_000), a => a.ValidatorAddress == v.Address);
            // ...and its stake is still bonded.
            Assert.False((await Verify(Send(v, _friend, 5_000M), 5_001)).Ok);
            Assert.Equal(Stake, ValidatorBond.LockedAmount(v.Address, 5_001));
            // The exit is free, and is what starts the clock. It is open to a validator that went quiet long ago: it
            // is judged from the bond in state, not from who was heard from lately.
            var exit = LifecycleTx(v, TransactionType.VBTC_V2_VALIDATOR_EXIT);
            var (ok, why) = await Verify(exit, 5_001);
            Assert.True(ok, why);
            await Apply(5_001, exit);
            Assert.True((await Verify(Send(v, _friend, 5_000M), 5_001 + 1_200)).Ok);
            // Once exited there is nothing more to exit; and an address with no bond has nothing to exit at all.
            (ok, why) = await Verify(LifecycleTx(v, TransactionType.VBTC_V2_VALIDATOR_EXIT), 5_002);
            Assert.False(ok);
            Assert.Contains("exited at block 5001", why);
            (ok, why) = await Verify(LifecycleTx(_owner, TransactionType.VBTC_V2_VALIDATOR_EXIT), 5_002);
            Assert.False(ok);
            Assert.Contains("has no validator bond", why);
        }

        // ── Minimum age for a vault's key ceremony ──────────────────────────────────────────────────────────

        private Transaction CreateVault(IReadOnlyList<V> ceremony)
        {
            var uid = FrostDkgAttestation.NewContractUid();
            var participants = ceremony.Select(v => v.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var threshold = FrostDkgAttestation.ThresholdFor(participants.Count, 51);
            var proof = FrostDkgAttestation.BuildProof(uid, _groupKey, _address, _owner.Address, threshold, participants, ceremony.Select(v => new FrostDkgAttestation.Attestation
            {
                ValidatorAddress = v.Address,
                Signature = SignatureService.CreateSignature(FrostDkgAttestation.Message(uid, _groupKey, _address, _owner.Address, threshold, participants), v.Key, v.Pub),
            }));
            var body = VbtcTestContracts.BuildContractData(uid, _owner.Address, new List<SmartContractFeatures>
            {
                new SmartContractFeatures
                {
                    FeatureName = FeatureName.TokenizationV2,
                    FeatureFeatures = new TokenizationV2Feature
                    {
                        AssetName = "vBTC", AssetTicker = "vBTC", DepositAddress = _address, Version = 2, ValidatorAddressesSnapshot = participants,
                        FrostGroupPublicKey = _groupKey, RequiredThreshold = 51, DKGProof = proof, ProofBlockHeight = 1, CeremonyId = uid, ImageBase = "default",
                    },
                },
            }, name: "vBTC");
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.VBTC_V2_CONTRACT_CREATE,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = uid, Data = body, MD5List = "NA" } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        [Fact]
        public async Task ValidatorsFromBeforeTheRule_ThatHoldTheirStake_CountForAKeyCeremonyAtOnce()
        {
            await Apply(Rule);
            // In the rule's own block nobody has been seen holding a stake yet.
            Assert.Empty(ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(Rule), Rule + 1));
            await Apply(Rule + 1);
            var eligible = ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(Rule + 1), Rule + 2).Select(v => v.ValidatorAddress).ToList();
            Assert.Equal(_old.Select(v => v.Address).OrderBy(a => a, StringComparer.Ordinal), eligible.OrderBy(a => a, StringComparer.Ordinal));
            Assert.DoesNotContain(_poor.Address, eligible);                       // bonded, but it holds no stake
            Assert.Equal(0, ValidatorBond.Row(_poor.Address)!.FundedSinceHeight);

            var (ok, why) = await Verify(CreateVault(_old), Rule + 2);
            Assert.True(ok, why);
            // One that holds no stake is not a participant consensus accepts.
            (ok, why) = await Verify(CreateVault(_old.Append(_poor).ToList()), Rule + 2);
            Assert.False(ok);
            Assert.Contains($"participant {_poor.Address} is not an eligible validator", why);
        }

        [Fact]
        public async Task ANewValidator_CountsForAKeyCeremony_OnlyAfterItsStakeHasBeenLockedForTwoDays_AndWhileItIsActive()
        {
            await StartTheRule();
            var fresh = NewKey();
            Fund(fresh, Stake);

            // It registers after the rule started. Its registration is judged and mined like any transaction.
            var register = LifecycleTx(fresh, TransactionType.VBTC_V2_VALIDATOR_REGISTER);
            var (ok, why) = await Verify(register, 250);
            Assert.True(ok, why);
            await Apply(250, register);
            var row = ValidatorBond.Row(fresh.Address)!;
            Assert.Equal(250, row.BondedAtHeight);
            Assert.Equal(0, row.FundedSinceHeight);
            Assert.False((await Verify(Send(fresh, _friend, 1_000M), 251)).Ok);     // bonded from its registration

            // Its stake is seen at the next look (every hundredth block), and its two days count from there.
            await Apply(300);
            const long lockedSince = 300;
            Assert.Equal(lockedSince, ValidatorBond.Row(fresh.Address)!.FundedSinceHeight);
            Assert.False(ValidatorBond.IsAged(fresh.Address, 301));

            // Active and funded, but too young: a ceremony that includes it is refused; without it, accepted.
            var everyone = _old.Append(fresh).ToList();
            (ok, why) = await Verify(CreateVault(everyone), 301);
            Assert.False(ok);
            Assert.Contains($"participant {fresh.Address} is not an eligible validator", why);
            (ok, why) = await Verify(CreateVault(_old), 301);
            Assert.True(ok, why);

            // One block short of two days: still too young. (Everyone has kept sending heartbeats.)
            var aged = lockedSince + ValidatorBond.CeremonyAgeBlocks;
            KeepActive(aged - 1, everyone);
            await Apply(aged - 2);
            Assert.False(ValidatorBond.IsAged(fresh.Address, aged - 1));
            Assert.False((await Verify(CreateVault(everyone), aged - 1)).Ok);

            // Two days: it counts.
            await Apply(aged - 1);
            Assert.True(ValidatorBond.IsAged(fresh.Address, aged));
            (ok, why) = await Verify(CreateVault(everyone), aged);
            Assert.True(ok, why);

            // The age is not enough by itself: the list is of validators that are still active. Much later, with
            // only the new one still sending heartbeats, the old ones are no longer on it, however long their bond.
            var later = aged + 3_000;
            KeepActive(later, new[] { fresh });
            await Apply(later - 1);
            var stillActive = ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(later - 1), later).Select(v => v.ValidatorAddress).ToList();
            Assert.Equal(new[] { fresh.Address }, stillActive);
            Assert.True(ValidatorBond.Row(_old[0].Address)!.FundedSinceHeight != 0);   // bonded and aged, but silent
            (ok, why) = await Verify(CreateVault(everyone), later);
            Assert.False(ok);
            Assert.Contains("is not an eligible validator", why);
        }

        [Fact]
        public async Task AStakeThatArrivesLate_AgesFromWhenItArrived_NotFromTheRegistration()
        {
            await StartTheRule();
            // Registered and bonded with almost nothing in the address (the registration in a block is applied as mined).
            var late = NewKey();
            Fund(late, 1M);
            await Apply(250, LifecycleTx(late, TransactionType.VBTC_V2_VALIDATOR_REGISTER));
            await Apply(300);
            await Apply(10_000);
            Assert.Equal(0, ValidatorBond.Row(late.Address)!.FundedSinceHeight);       // bonded for 9,750 blocks, never funded
            Assert.False((await Verify(Send(late, _friend, 0.5M), 10_001)).Ok);         // and it can send nothing meanwhile

            // The stake arrives just before a key ceremony would need it.
            Fund(late, Stake);
            KeepActive(14_700, new[] { late });
            await Apply(14_700);
            Assert.Equal(14_700, ValidatorBond.Row(late.Address)!.FundedSinceHeight);
            // Two days after it registered, it is still not counted: its stake has been locked for no time at all.
            Assert.False(ValidatorBond.IsAged(late.Address, 250 + ValidatorBond.CeremonyAgeBlocks + 100));
            Assert.True(ValidatorBond.IsAged(late.Address, 14_700 + ValidatorBond.CeremonyAgeBlocks));
            // And having arrived, it stays.
            Assert.False((await Verify(Send(late, _friend, 1M), 14_701)).Ok);
        }

        // ── Minimum age for the Bitcoin report signers ──────────────────────────────────────────────────────

        private static List<string> SignerSet(long vfxHeight) => BtcAttestStore.SignerSet(BtcAttestRules.EpochOf(vfxHeight))!.Addresses;
        private static List<string> Sorted(IEnumerable<V> validators) => validators.Select(v => v.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();

        /// <summary>
        /// As released: recording starts at the bond height, so the first signer set is taken in the block where the
        /// bonds start. That set is the funded validators, as before the bond. Every later set takes only validators
        /// whose stake has been locked for two days: one that registers today signs no report for two days.
        /// </summary>
        [Fact]
        public async Task TheReportSigners_AreTheFundedValidatorsAtTheStart_AndFromThenOnlyThoseWithAStakeLockedForTwoDays()
        {
            Globals.BitcoinAttestationRecordHeight = Rule;     // epochs start at 200, 800, 1400, ...
            await StartTheRule();
            Assert.Equal(Sorted(_old), SignerSet(Rule));       // not empty in the bond's own block; the unfunded one is not in it

            var fresh = NewKey();
            Fund(fresh, Stake);
            await Apply(250, LifecycleTx(fresh, TransactionType.VBTC_V2_VALIDATOR_REGISTER));
            await Apply(300);
            const long lockedSince = 300;
            Assert.Equal(lockedSince, ValidatorBond.Row(fresh.Address)!.FundedSinceHeight);

            // The next epoch: it is active and holds the stake (what used to be enough), and it is not a signer.
            var everyone = _old.Append(fresh).ToList();
            KeepActive(800, everyone);
            await Apply(800);
            Assert.Contains(VBTCValidatorRegistry.FundedOnly(VBTCValidatorRegistry.GetActiveValidatorsAt(799)), a => a.ValidatorAddress == fresh.Address);
            Assert.Equal(Sorted(_old), SignerSet(800));

            // The last epoch that starts before its two days are up (14,700): still not.
            var aged = lockedSince + ValidatorBond.CeremonyAgeBlocks;
            Assert.Equal(14_700, aged);
            KeepActive(14_600, everyone);
            await Apply(14_600);
            Assert.True(BtcAttestRules.IsEpochStart(14_600));
            Assert.Equal(Sorted(_old), SignerSet(14_600));

            // The first epoch that starts after them: it signs.
            KeepActive(15_200, everyone);
            await Apply(15_200);
            Assert.Equal(Sorted(everyone), SignerSet(15_200));

            // Age is not enough by itself: three epochs later only the new one is still heard from, and the set is
            // of validators that are still active.
            KeepActive(17_000, new[] { fresh });
            await Apply(17_000);
            Assert.True(BtcAttestRules.IsEpochStart(17_000));
            Assert.Equal(new List<string> { fresh.Address }, SignerSet(17_000));

            // One that exits is out of the next set, while its stake waits out the sets it was in.
            KeepActive(17_600, everyone);
            await Apply(17_590, LifecycleTx(_old[0], TransactionType.VBTC_V2_VALIDATOR_EXIT));
            await Apply(17_600);
            Assert.Equal(Sorted(everyone.Where(v => v != _old[0])), SignerSet(17_600));
            Assert.False((await Verify(Send(_old[0], _friend, 5_000M), 17_601)).Ok);
        }

        /// <summary>
        /// If recording starts one block after the bonds do, the first set is taken in the block where the stakes are
        /// first looked at, before that look. It is the funded validators there too, not an empty set.
        /// </summary>
        [Fact]
        public async Task TheFirstSignerSet_TakenBeforeAnyStakeWasLookedAt_IsTheFundedValidators()
        {
            Globals.BitcoinAttestationRecordHeight = Rule + 1;
            await StartTheRule();
            Assert.Equal(Sorted(_old), SignerSet(Rule + 1));
            // And before the bond there is no age to ask for: the funded validators, as always.
            Assert.Equal(Sorted(_old), ValidatorBond.SignerEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(Rule - 1), Rule - 1).Select(v => v.ValidatorAddress).OrderBy(a => a, StringComparer.Ordinal).ToList());
        }

        // ── How the bonds start (second path audit, 14 October 2026) ────────────────────────────────────────

        /// <summary>
        /// A validator counts as aged at the start only if it was a validator already at a height fixed in the release,
        /// below the tip when the release was cut. Every validator active one block before the start used to count,
        /// so a registration made once the bond was public bought the two days for nothing.
        /// </summary>
        [Fact]
        public async Task AValidatorThatRegisteredOnceTheBondWasKnown_IsBondedAtTheStart_AndWaitsItsTwoDays()
        {
            var late = NewKey();
            Fund(late, Stake + 1M);
            Lifecycle(late, TransactionType.VBTC_V2_VALIDATOR_REGISTER, Grandfather + 5);
            await StartTheRule();

            var row = ValidatorBond.Row(late.Address)!;
            Assert.Equal(Rule, row.BondedAtHeight);
            Assert.False(row.Grandfathered);
            Assert.Equal(Rule + 1, row.FundedSinceHeight);                         // locked from the first look, like anybody new
            Assert.False(ValidatorBond.IsAged(late.Address, Rule + 2));
            Assert.True(ValidatorBond.Row(_old[0].Address)!.Grandfathered);
            Assert.True(ValidatorBond.IsAged(_old[0].Address, Rule + 2));
            Assert.False((await Verify(Send(late, _friend, 100M), Rule + 2)).Ok);   // its stake is held all the same

            var eligible = ValidatorBond.CeremonyEligible(VBTCValidatorRegistry.GetActiveValidatorsAt(Rule + 1), Rule + 2).Select(v => v.ValidatorAddress).ToList();
            Assert.DoesNotContain(late.Address, eligible);
            Assert.Equal(_old.Count, eligible.Count);
            Assert.True(ValidatorBond.IsAged(late.Address, Rule + 1 + ValidatorBond.CeremonyAgeBlocks));
        }

        /// <summary>
        /// A validator bonded by the start alone that does not hold its stake is not held beyond the wait an exit has:
        /// what it has left used to be locked until it sent an exit of its own.
        /// </summary>
        [Fact]
        public async Task AValidatorSeededWithoutItsStake_IsTreatedAsExitedAtTheStart()
        {
            await StartTheRule();
            var row = ValidatorBond.Row(_poor.Address)!;
            Assert.Equal(Rule, row.ExitHeight);
            Assert.Equal(0, row.FundedSinceHeight);
            Assert.False((await Verify(Send(_poor, _friend, 1M), Rule + ValidatorBond.ExitWaitBlocks - 1)).Ok);
            Assert.True((await Verify(Send(_poor, _friend, 1M), Rule + ValidatorBond.ExitWaitBlocks)).Ok);
            // A heartbeat (which needs the balance) bonds it again, with no age.
            Fund(_poor, Stake);
            await Apply(Rule + 50, LifecycleTx(_poor, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT));
            Assert.Equal(0, ValidatorBond.Row(_poor.Address)!.ExitHeight);
            Assert.Equal(0, ValidatorBond.Row(_poor.Address)!.FundedSinceHeight);
            await Apply(Rule + 100);
            Assert.Equal(Rule + 100, ValidatorBond.Row(_poor.Address)!.FundedSinceHeight);
        }

        /// <summary>
        /// The bonds start at one block, the same on every node. A state that reaches a later block without them (built
        /// by a release that did not have them) used to start them there, with other validators and another height
        /// than the network's, and said nothing.
        /// </summary>
        [Fact]
        public async Task AStateThatHasNoBondsPastTheStart_IsNotSeededThere_AndTheBlockCountsAsNotApplied()
        {
            var block = new Block { Height = Rule + 5, Hash = "late-seed", StateRoot = "root", Timestamp = TimeUtil.GetTime(), Transactions = new List<Transaction>() };
            BlockchainData.GetBlocks().InsertSafe(block);
            Assert.False(await StateData.UpdateTreis(block));
            Assert.Null(ValidatorBond.Row(_old[0].Address));
            Assert.Throws<InvalidOperationException>(() => ValidatorBond.OnBlockStart(Rule + 6));
        }

        /// <summary>
        /// A heartbeat and a send of the stake do not share a block. Each was valid alone against the balances before the
        /// block, so the bond started on an address the stake had just left, and the stake went on to do the same from
        /// the next address: one bond row a block, for ever, and never a locked coin.
        /// </summary>
        [Fact]
        public async Task ABondThatStartsInABlock_HoldsTheStakeFromThatBlocksStart()
        {
            await StartTheRule();
            var fresh = NewKey();
            Fund(fresh, Stake + 2M);
            var heartbeat = LifecycleTx(fresh, TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT);
            Assert.True(ValidatorBond.StartsBond(heartbeat, Rule + 10));
            Assert.False(ValidatorBond.StartsBond(LifecycleTx(_old[0], TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT), Rule + 10)); // bonded already

            // The heartbeat first, then the stake: refused. What is above the stake still goes.
            var state = new SameBlockDebitGuard.State { BlockHeight = Rule + 10 };
            Assert.True(SameBlockDebitGuard.TryRegister(heartbeat, state).Ok);
            var (ok, why) = SameBlockDebitGuard.TryRegister(Send(fresh, _friend, 5_000M), state);
            Assert.False(ok);
            Assert.StartsWith(SameBlockDebitGuard.ReasonPrefix, why);
            Assert.True(SameBlockDebitGuard.TryRegister(Send(fresh, _friend, 1M), state).Ok);

            // The stake first, then the heartbeat: the heartbeat is refused.
            state = new SameBlockDebitGuard.State { BlockHeight = Rule + 10 };
            Assert.True(SameBlockDebitGuard.TryRegister(Send(fresh, _friend, 5_000M), state).Ok);
            (ok, why) = SameBlockDebitGuard.TryRegister(heartbeat, state);
            Assert.False(ok);
            Assert.Contains("starts a validator bond in this block", why);

            // Before the bonds: as it always was.
            state = new SameBlockDebitGuard.State { BlockHeight = Rule - 5 };
            Assert.True(SameBlockDebitGuard.TryRegister(heartbeat, state).Ok);
            Assert.True(SameBlockDebitGuard.TryRegister(Send(fresh, _friend, 5_000M), state).Ok);
        }

        /// <summary>
        /// As released, the bonds start one block before Bitcoin recording does, so the first signer set (taken at the
        /// recording block) counts stakes that are already locked. Started together, a stake could be counted for that
        /// set and leave in the same block.
        /// </summary>
        [Fact]
        public async Task TheFirstSignerSet_CountsStakesThatAreAlreadyLocked()
        {
            Globals.BitcoinAttestationRecordHeight = Rule + 1;
            // In the bonds' own block a transaction is still judged without them: this validator's stake leaves.
            var leaver = _old[7];
            var leaving = Send(leaver, _friend, 5_000M);
            var (ok, why) = await Verify(leaving, Rule);
            Assert.True(ok, why);
            await Apply(Rule, leaving);
            // From the next block (where the first set is taken) nobody's stake can leave.
            Assert.False((await Verify(Send(_old[0], _friend, 5_000M), Rule + 1)).Ok);
            await Apply(Rule + 1);

            var set = BtcAttestStore.SignerSet(BtcAttestRules.EpochOf(Rule + 1))!.Addresses;
            Assert.DoesNotContain(leaver.Address, set);
            Assert.Equal(Sorted(_old.Where(v => v != leaver)), set);
            Assert.All(_old.Where(v => v != leaver), v => Assert.Equal(Stake, ValidatorBond.LockedAmount(v.Address, Rule + 2)));
        }

        [Fact]
        public async Task BeforeTheBondsStart_NoExitTransactionIsSent()
        {
            var priorAddress = Globals.ValidatorAddress;
            try
            {
                var v = _old[5];
                AccountData.GetAccounts().InsertSafe(new Account { Address = v.Address, PublicKey = v.Pub, PrivateKey = "0" + v.Key.secret.ToString("x"), IsValidating = true });
                Globals.ValidatorAddress = v.Address;
                Globals.LastBlock = new Block { Height = Rule - 10 };
                var (sent, why) = await ValidatorService.SendValidatorExit(v.Address);
                Assert.False(sent);
                Assert.Contains($"starts at block {Rule}", why);
                Assert.Empty(TransactionData.GetPool().FindAll());
                Assert.False(Globals.ValidatorExitSent);
            }
            finally { Globals.ValidatorAddress = priorAddress; }
        }

        // ── The graceful exit from the node itself ──────────────────────────────────────────────────────────

        /// <summary>
        /// The node's own exit: built, signed with the wallet's key, judged like any transaction and put in the pool.
        /// From then on the node does not announce the validator again (its heartbeat loop would otherwise "heal" the
        /// exit away and the stake would never unlock).
        /// </summary>
        [Fact]
        public async Task TheNodesOwnExit_IsSentAndMined_AndTheNodeStopsAnnouncingItself()
        {
            var priorAddress = Globals.ValidatorAddress;
            var priorSent = Globals.ValidatorExitSent;
            try
            {
                await StartTheRule();
                var v = _old[5];
                AccountData.GetAccounts().InsertSafe(new Account { Address = v.Address, PublicKey = v.Pub, PrivateKey = "0" + v.Key.secret.ToString("x"), IsValidating = true });
                Globals.ValidatorAddress = v.Address;
                Globals.ValidatorExitSent = false;

                var (sent, hash) = await ValidatorService.SendValidatorExit(v.Address);
                Assert.True(sent, hash);
                Assert.True(Globals.ValidatorExitSent);
                var pooled = TransactionData.GetPool().FindOne(x => x.Hash == hash);
                Assert.NotNull(pooled);
                Assert.Equal(TransactionType.VBTC_V2_VALIDATOR_EXIT, pooled!.TransactionType);

                await Apply(Tip + 1, pooled);
                Assert.Equal(Tip, ValidatorBond.Row(v.Address)!.ExitHeight);
                // A second one has nothing to do, and says when the stake unlocks.
                var (again, why) = await ValidatorService.SendValidatorExit(v.Address);
                Assert.False(again);
                Assert.Contains($"unlocks at block {Tip + ValidatorBond.ExitWaitBlocks}", why);
                // A wallet that does not hold the key cannot exit somebody else's validator.
                Assert.False((await ValidatorService.SendValidatorExit(_old[6].Address)).Ok);
            }
            finally
            {
                Globals.ValidatorAddress = priorAddress;
                Globals.ValidatorExitSent = priorSent;
            }
        }

        // ── What the API says ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheApi_ShowsABond_WhatCanBeSent_AndWhenItUnlocks()
        {
            await StartTheRule();
            var v = _old[4];
            var api = new VBTCController();
            var bond = JObject.Parse(await api.GetValidatorBond(v.Address))["Bond"]!;
            Assert.True((bool)bond["Bonded"]!);
            Assert.Equal(5_000M, (decimal)bond["BondedAmount"]!);
            Assert.Equal(10M, (decimal)bond["Spendable"]!);
            Assert.True((bool)bond["CountsForKeyCeremoniesNow"]!);
            Assert.Equal(JTokenType.Null, bond["UnlocksAtHeight"]!.Type);

            await Apply(400, LifecycleTx(v, TransactionType.VBTC_V2_VALIDATOR_EXIT));
            bond = JObject.Parse(await api.GetValidatorBond(v.Address))["Bond"]!;
            Assert.True((bool)bond["Bonded"]!);
            Assert.Equal(400, (long)bond["ExitHeight"]!);
            Assert.Equal(1_600, (long)bond["UnlocksAtHeight"]!);
            Assert.False((bool)bond["CountsForKeyCeremoniesNow"]!);

            var none = JObject.Parse(await api.GetValidatorBond(_owner.Address))["Bond"]!;
            Assert.False((bool)none["Bonded"]!);
            Assert.Equal(100M, (decimal)none["Spendable"]!);
        }
    }
}
