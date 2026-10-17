using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;
using BtcTx = NBitcoin.Transaction;
using VfxTx = VerifiedXCore.Models.Transaction;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Bitcoin facts in consensus, part 12: a bridge exit that pays Bitcoin out of vaults the chain watches
    /// (docs/PLAN-vBTC-Bitcoin-Attestation.md, 5.6). The casters send the exit transaction and nothing after it. The
    /// Bitcoin transaction of every vault the exit draws on is fixed in that block (all of them or none), the vaults'
    /// signers sign only those, Bitcoin showing them completes the exit, and an exit that could not be pinned is failed
    /// by rule with its locks restored. Transactions are judged as block validation judges them and applied with
    /// UpdateTreis, under the bridge rules that are live on the network (submitter must be a caster).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcAttest12_BridgeExitTests : BtcAttestTestBase
    {
        private const long Checkpoint = 4032;
        private const string BtcDest = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
        private readonly Btc _btc = new(Checkpoint - 11);
        private readonly V _holder = NewKey();
        private readonly Network? _priorNetwork = Globals.BTCNetwork;
        private readonly long _priorGuard = Globals.BridgeIntraBlockGuardHeight;
        private readonly Func<VaultWithdrawalRow, VaultPin, Task<(bool, string)>> _realSign = BtcWithdrawalSigner.SignAndBroadcast;
        private readonly Func<string, Task<bool?>> _realKnown = BtcWithdrawalSigner.KnownOnBitcoin;

        /// <summary>The caster that sends the exits.</summary>
        private V Caster => Signers[0];

        public BtcAttest12_BridgeExitTests() : base(realMainnetRules: false)
        {
            _btc.MineEmpty(12);
            Globals.BitcoinCheckpointHeightMainnet = Checkpoint;
            Globals.BitcoinCheckpointHeadersMainnet = string.Concat(_btc.HeaderHexes(Checkpoint - 11, Checkpoint));
            Globals.BTCNetwork = Network.Main;
            Globals.BridgeIntraBlockGuardHeight = 1;
            Globals.BootstrapCasterAddresses.Add(Caster.Address);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _holder.Address, Balance = 100M, Nonce = 0 });
            BtcWithdrawalSigner.ForgetAttempts();
        }

        public override void Dispose()
        {
            BtcWithdrawalSigner.SignAndBroadcast = _realSign;
            BtcWithdrawalSigner.KnownOnBitcoin = _realKnown;
            BtcWithdrawalSigner.ForgetAttempts();
            Globals.BootstrapCasterAddresses.Remove(Caster.Address);
            Globals.BridgeIntraBlockGuardHeight = _priorGuard;
            Globals.BTCNetwork = _priorNetwork;
            base.Dispose();
        }

        private static long Tip => Globals.LastBlock.Height;

        // ── Bitcoin side ────────────────────────────────────────────────────────────────────────────────────

        private async Task<BtcTx> Deposit(NBitcoin.Script vault, long sats)
        {
            var tx = Pay(vault, sats);
            await Report(_btc.Mine(tx));
            return tx;
        }

        /// <summary>Buries a Bitcoin block and has the validators report it.</summary>
        private async Task Report(long bitcoinHeight)
        {
            _btc.MineEmpty(BtcAttestRules.Depth);
            var meta = BtcAttestStore.GetMeta()!;
            var headers = _btc.HeaderHexes(meta.TipHeight + 1, bitcoinHeight + BtcAttestRules.Depth);
            var report = Assert.Single(BtcBlockScanner.BuildForward(_btc.Block(bitcoinHeight), bitcoinHeight, headers, BtcAttestRules.EpochOf(Tip + 1)));
            var tx = Signed(report);
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
        }

        /// <summary>What the vault's signers do with a pin: sign exactly that transaction and broadcast it. Here it is mined.</summary>
        private async Task<BtcTx> SignAndConfirm(VaultPin pin)
        {
            var tx = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(pin.Txid, tx.GetHash().ToString());
            await Report(_btc.Mine(tx));
            return tx;
        }

        // ── VFX side ────────────────────────────────────────────────────────────────────────────────────────

        private static VfxTx SignedBy(V signer, string to, TransactionType type, object data, bool free = false)
        {
            var tx = new VfxTx
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0.0M, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            if (!free) tx.Fee = VerifiedXCore.Services.FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private VfxTx Transfer(string vault, decimal amount, V? from = null, V? to = null) =>
            SignedBy(from ?? Minter, (to ?? _holder).Address, TransactionType.VBTC_V2_TRANSFER,
                new { Function = "TransferVBTCV2()", ContractUID = vault, FromAddress = (from ?? Minter).Address, ToAddress = (to ?? _holder).Address, Amount = amount });

        private VfxTx Request(string vault, decimal amount, V? from = null) =>
            SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "VBTCWithdrawalRequest()", ContractUID = vault, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") });

        /// <summary>A holder locks vBTC of a vault for the bridge; returns the lock's id.</summary>
        private async Task<string> Lock(string vault, decimal amount, V? from = null, string? id = null)
        {
            var lockId = id ?? Guid.NewGuid().ToString("N");
            await Mine(SignedBy(from ?? Minter, (from ?? Minter).Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault, LockId = lockId, Amount = amount, AmountSats = (long)(amount * 100_000_000M), EvmDestination = "0x" + new string('a', 40) }));
            Assert.NotNull(VBTCBridgeLockState.GetByLockId(lockId));
            return lockId;
        }

        private static string NewBurn() => "0x" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>The exit transaction a caster sends for a burn on Base: which locks pay it, and where the Bitcoin goes.</summary>
        private VfxTx Exit(string burn, string destination, params (string LockId, string Vault, decimal Amount)[] allocations)
        {
            var total = allocations.Sum(a => a.Amount);
            return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = allocations[0].Vault, LockId = allocations[0].LockId,
                Amount = total, AmountSats = (long)(total * 100_000_000M), TotalAmount = total, TotalAmountSats = (long)(total * 100_000_000M),
                BtcDestination = destination, BaseBurnTxHash = burn,
                Allocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);
        }

        private VfxTx ExitComplete(string burn, string btcTxid) =>
            SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE,
                new { Function = "VBTCBridgeExitToBTCComplete()", BaseBurnTxHash = burn, BtcTxHash = btcTxid }, free: true);

        private VfxTx ExitFail(string burn, VfxTx exit, params (string LockId, string Vault, decimal Amount)[] allocations) =>
            SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL, new
            {
                Function = "VBTCBridgeExitToBTCFail()", BaseBurnTxHash = burn, ExitTxHash = exit.Hash, Reason = "test",
                FailedAllocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);

        /// <summary>Judged as a block's transaction, then mined in the next block.</summary>
        private async Task<VfxTx> Mine(VfxTx tx)
        {
            var (ok, why) = await Verify(tx, Tip + 1);
            Assert.True(ok, why);
            await Apply(Tip + 1, tx);
            return tx;
        }

        private async Task RefusedInBlock(VfxTx tx, string because, long? height = null)
        {
            var (ok, why) = await Verify(tx, height ?? Tip + 1);
            Assert.False(ok, "accepted in a block");
            Assert.Contains(because, why);
        }

        /// <summary>One vault's part of the exit the chain holds for a burn (its latest attempt).</summary>
        private static VaultWithdrawalRow Part(string burn, string vault) => BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(Record(burn), vault))!;
        private static VBTCBridgeBtcExitState Record(string burn) => VBTCBridgeBtcExitState.GetByBurnHash(burn)!;

        private async Task<decimal> Spendable(string vault, V who) =>
            (await VBTCService.TryGetAvailableTransparentVbtcBalance(vault, who.Address)).availableBalance;

        /// <summary>What a ceremony leader announces to the signers for a transaction: the bytes, the coins it spends, and every input's sighash.</summary>
        private static FrostSigningStartRequest Announce(string vault, NBitcoin.Script vaultScript, string reference, BtcTx tx, string leader)
        {
            var prevouts = tx.Inputs.Select(i =>
            {
                var coin = BtcAttestStore.Coin(BtcAttestStore.OutpointKey(i.PrevOut.Hash.ToString(), i.PrevOut.N));
                return new PinnedWithdrawalCoin { TxId = i.PrevOut.Hash.ToString(), Vout = i.PrevOut.N, ValueSats = (ulong)(coin?.ValueSats ?? 77_777), ScriptPubKeyHex = vaultScript.ToHex() };
            }).ToList();
            var spent = prevouts.Select(p => new TxOut(Money.Satoshis((long)p.ValueSats), NBitcoin.Script.FromHex(p.ScriptPubKeyHex))).ToArray();
            var precomputed = tx.PrecomputeTransactionData(spent);
            var sighashes = Enumerable.Range(0, tx.Inputs.Count)
                .Select(i => Convert.ToHexString(tx.GetSignatureHashTaproot(precomputed, new TaprootExecutionData(i) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant())
                .ToList();
            return new FrostSigningStartRequest
            {
                SessionId = Guid.NewGuid().ToString(), SmartContractUID = vault, WithdrawalRequestHash = reference, LeaderAddress = leader,
                MessageHash = sighashes[0], InputIndex = 0, InputCount = tx.Inputs.Count, AllInputSighashes = sighashes,
                UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, Timestamp = TimeUtil.GetTime(),
            };
        }

        // ── The ordinary path ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A lock's look-up trims its id and ignores case, while the duplicate check (in the exit and across a block)
        /// compared ids exactly: "L1" and "l1 " drew on one lock twice, the second draw failed at apply and the part was
        /// paid in full anyway. A lock is named by its own id exactly, and asked for no more than it holds (fourth review,
        /// round 11).
        /// </summary>
        [Fact]
        public async Task AnExit_NamesEachLockByItsOwnId_AndAsksNoMoreThanItHolds()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.002M), (lockId.ToUpperInvariant(), vault.Uid, 0.002M)), "a lock is named by its own id");
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (lockId + " ", vault.Uid, 0.004M)), "a lock is named by its own id");
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.002M), (lockId, vault.Uid, 0.002M)), "twice");
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.00400001M)), "it holds");
            // The first version's single-lock shape, naming no watched contract but a watched vault's lock: refused, as an
            // exit from that vault (it was recorded with no part, and its burn was used for good; round 12).
            var single = SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = Guid.NewGuid().ToString("N") + ":1", LockId = lockId,
                Amount = 0.004M, AmountSats = 400_000L, BtcDestination = BtcDest, BaseBurnTxHash = NewBurn(),
            }, free: true);
            await RefusedInBlock(single, "carries its allocations");
            // ... also when the lock's id is given as a JSON number, which validation and apply read as its digits (round 13).
            var numbered = await Lock(vault.Uid, 0.002M, id: "777" + Random.Shared.Next(100_000, 999_999));
            var numeric = SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = Guid.NewGuid().ToString("N") + ":1", LockId = long.Parse(numbered),
                Amount = 0.002M, AmountSats = 200_000L, BtcDestination = BtcDest, BaseBurnTxHash = NewBurn(),
            }, free: true);
            await RefusedInBlock(numeric, "carries its allocations");
            // ... and naming an older vault and a lock that does not exist: it drew no lock and had that vault's signers
            // pay the burn (round 14). The shape is refused from the recording height.
            var older = SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, new
            {
                Function = "VBTCBridgeExitToBTC()", ContractUID = Guid.NewGuid().ToString("N") + ":1", LockId = "made-up",
                Amount = 0.002M, AmountSats = 200_000L, BtcDestination = BtcDest, BaseBurnTxHash = NewBurn(),
            }, free: true);
            await RefusedInBlock(older, "carries its allocations");
            // Allocations that add up to one satoshi more than the burn: refused (they took it from the pool; round 14).
            var over = JObject.Parse(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.002M)).Data);
            over["TotalAmount"] = 0.00199999M;
            over["TotalAmountSats"] = 199_999L;
            await RefusedInBlock(SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, over, free: true), "add up to");
            // An allocation's fields named in another case, or twice in two cases: refused (apply reads either; round 16).
            foreach (var spelled in new[] { "\"lockId\":\"" + lockId + "\"", "\"LockId\":\"other\",\"lockId\":\"" + lockId + "\"" })
            {
                var data = JObject.Parse(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.002M)).Data);
                data["Allocations"] = JArray.Parse("[{" + spelled + ",\"UnlockAmount\":0.002,\"SmartContractUID\":\"" + vault.Uid + "\"}]");
                await RefusedInBlock(SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true), "named exactly");
            }
            // ... or a fraction of a satoshi more, in the total and the allocation alike (round 15).
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (lockId, vault.Uid, 0.002000009M)), "decimal places");
        }

        /// <summary>
        /// The block guard keyed an exit to Bitcoin on ExitBurnTxHash, a field the exit never reads: one committee caster
        /// gave two copies of an exit their own made-up ExitBurnTxHash and spelled the burn differently, both passed in one
        /// block, and the second drew its locks, found the burn's record taken and was never paid or restored (fifth
        /// review, round 13). The guard keys the exit on the burn it redeems, and apply draws nothing for a burn that has
        /// an exit already.
        /// </summary>
        [Fact]
        public async Task TwoCopiesOfOneBurnsExit_InOneBlock_TheSecondIsRefused_AndDrawsNothingIfApplied()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var first = await Lock(vault.Uid, 0.002M);
            var second = await Lock(vault.Uid, 0.002M);
            var burn = NewBurn();
            VfxTx Copy(string spelling, string lockId)
            {
                var data = JObject.Parse(Exit(spelling, BtcDest, (lockId, vault.Uid, 0.002M)).Data);
                data["ExitBurnTxHash"] = NewBurn();
                return SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC, data, free: true);
            }
            var one = Copy(burn, first);
            var two = Copy("0x" + burn[2..].ToUpperInvariant(), second);
            // Each passes on the chain's stored state; the block's guard takes the first and refuses the second.
            Assert.True((await Verify(one, Tip + 1)).Ok);
            Assert.True((await Verify(two, Tip + 1)).Ok);
            var guard = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(one, guard, Tip + 1).Ok);
            var (ok, why) = BridgeIntraBlockGuard.TryRegister(two, guard, Tip + 1);
            Assert.False(ok);
            Assert.Contains("Duplicate Base burn", why);

            // Applied anyway (as an older node would have): the second copy draws nothing.
            await Apply(Tip + 1, one, two);
            Assert.True(VBTCBridgeLockState.GetByLockId(first)!.IsUnlocked);
            var untouched = VBTCBridgeLockState.GetByLockId(second)!;
            Assert.False(untouched.IsUnlocked);
            Assert.Equal(200_000, untouched.RemainingAmountSats);
            Assert.Equal(one.Hash, Record(burn).ExitTxHash);
        }

        /// <summary>A bridge lock whose id is blank or has spaces around it is refused: it was debited and stored where no look-up finds it (round 17).</summary>
        [Fact]
        public async Task ABridgeLockId_IsNotBlank_AndHasNoSpacesAroundIt()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            VfxTx LockAs(string id) => SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                new { Function = "VBTCBridgeLock()", ContractUID = vault.Uid, LockId = id, Amount = 0.001M, AmountSats = 100_000L, EvmDestination = "0x" + new string('a', 40) });
            foreach (var id in new[] { " ", "   ", " abc", "abc " })
                await RefusedInBlock(LockAs(id), "not blank");
            Assert.True((await Verify(LockAs("abc"), Tip + 1)).Ok);
        }

        /// <summary>
        /// A validator signed a Base mint over the lock id the caller gave, after finding the lock by a look-up that trims
        /// and ignores case; Base refuses a repeat only by the exact bytes of that id, so "abc", "ABC" and "abc " each
        /// minted one lock again (fourth review, round 12). Only the lock's own id is signed.
        /// </summary>
        [Fact]
        public async Task AMintAttestation_IsGivenOnlyForTheLocksOwnId()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var priorValidator = Globals.ValidatorAddress;
            var priorCastersOnly = Globals.BaseValidatorSetCastersOnly;
            Globals.ValidatorAddress = "xNotLoadedHere";
            Globals.BaseValidatorSetCastersOnly = false;
            try
            {
                VerifiedXCore.Bitcoin.Models.MintAttestationRequest Ask(string id) => new()
                {
                    LockId = id, EvmDestination = "0x" + new string('a', 40), AmountSats = 400_000, Nonce = 1,
                    ChainId = Globals.BaseEvmChainId, ContractAddress = "0x" + new string('b', 40),
                };
                foreach (var variant in new[] { lockId.ToUpperInvariant(), lockId + " ", " " + lockId })
                {
                    var (ok, _, error) = await VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.HandleMintAttestationRequest(Ask(variant));
                    Assert.False(ok);
                    Assert.Contains("own id exactly", error);
                }
                // The lock's own id passes this check (and goes on to the signing key, not loaded in this test).
                var (_, _, exactError) = await VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.HandleMintAttestationRequest(Ask(lockId));
                Assert.DoesNotContain("own id exactly", exactError ?? "");
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                Globals.BaseValidatorSetCastersOnly = priorCastersOnly;
            }
        }

        /// <summary>
        /// The vBTCb contract's admin messages (ADD, ADD_BATCH, REMOVE, REMOVE_BATCH, UPGRADE) begin with a text tag and
        /// are signed by the same validators the same way as a mint, whose message begins with the destination. A mint
        /// attestation is never given for a destination that begins with one of those tags (round 37); any other
        /// destination is judged as before. A lock to such a destination is refused itself (it would debit vBTC that is
        /// never minted); the signer still refuses one already held in state, as a lock made before that rule would be.
        /// </summary>
        [Fact]
        public async Task AMintAttestation_IsNotGivenForADestinationThatBeginsWithAnAdminTag()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var priorValidator = Globals.ValidatorAddress;
            var priorCastersOnly = Globals.BaseValidatorSetCastersOnly;
            Globals.ValidatorAddress = "xNotLoadedHere";
            Globals.BaseValidatorSetCastersOnly = false;
            try
            {
                VfxTx LockTo(string destination, string lockId) => SignedBy(Minter, Minter.Address, TransactionType.VBTC_V2_BRIDGE_LOCK,
                    new { Function = "VBTCBridgeLock()", ContractUID = vault.Uid, LockId = lockId, Amount = 0.001M, AmountSats = 100_000L, EvmDestination = destination });
                async Task<string?> AskFor(string destination)
                {
                    var lockId = Guid.NewGuid().ToString("N");
                    if (VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.DestinationStartsWithAdminTag(destination))
                    {
                        // The lock itself is refused, in a block and at admission.
                        await RefusedInBlock(LockTo(destination, lockId), VerifiedXCore.Services.TransactionValidatorService.BridgeLockDestinationIsAdminTagReason);
                        var (admitted, why) = await VerifiedXCore.Services.TransactionValidatorService.VerifyTX(LockTo(destination, lockId));
                        Assert.False(admitted);
                        Assert.Equal(VerifiedXCore.Services.TransactionValidatorService.BridgeLockDestinationIsAdminTagReason, why);
                        // One already in state (made before the rule) is still never signed for.
                        Assert.True(VBTCBridgeLockState.TryInsertFromLockTx(new VerifiedXCore.Models.Transaction { Hash = "pre" + lockId, FromAddress = Minter.Address, Height = Tip },
                            vault.Uid, lockId, 0.001M, 100_000L, destination));
                    }
                    else
                        await Mine(LockTo(destination, lockId));
                    Assert.NotNull(VBTCBridgeLockState.GetByLockId(lockId));
                    var (_, _, error) = await VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.HandleMintAttestationRequest(new VerifiedXCore.Bitcoin.Models.MintAttestationRequest
                    {
                        LockId = lockId, EvmDestination = destination, AmountSats = 100_000, Nonce = 1,
                        ChainId = Globals.BaseEvmChainId, ContractAddress = "0x" + new string('b', 40),
                    });
                    return error;
                }

                foreach (var tag in new[] { "414444", "52454d4f5645", "55504752414445" }) // ADD, REMOVE, UPGRADE
                    Assert.Equal(VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.DestinationIsAdminTagReason,
                        await AskFor("0x" + tag + new string('1', 40 - tag.Length)));

                // An ordinary destination passes this check (and goes on to the signing key, not loaded in this test).
                Assert.NotEqual(VerifiedXCore.Bitcoin.Services.BaseBridgeAttestationService.DestinationIsAdminTagReason,
                    await AskFor("0x" + new string('a', 40)));
            }
            finally
            {
                Globals.ValidatorAddress = priorValidator;
                Globals.BaseValidatorSetCastersOnly = priorCastersOnly;
            }
        }

        [Fact]
        public async Task AnExit_IsPinnedInItsOwnBlock_BitcoinCompletesIt_AndTheOwnerIsChargedOnce()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var deposit = await Deposit(vault.Script, 1_000_000);
            Assert.Equal(0.01M, await Spendable(vault.Uid, Minter));

            // The owner locks 0.004 for the bridge: debited at once, the Bitcoin stays in the vault.
            var lockId = await Lock(vault.Uid, 0.004M);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));

            // That vBTC.b is burned on Base and a caster sends the exit.
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var part = Part(burn, vault.Uid);
            Assert.True(part.IsBridgeExit);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            Assert.Equal(400_000, part.AmountSats);
            Assert.Equal(BtcWithdrawals.ExitFeeRate, part.FeeRate);
            Assert.Equal(Tip, part.LastModifiedHeight);
            var pin = Assert.Single(part.Pins);
            Assert.Equal(new[] { Outpoint(deposit, 0) }, pin.Inputs);
            Assert.Equal(154 * 10, pin.FeeSats);
            Assert.Equal(400_000 - 1_540, pin.DestinationSats);
            Assert.Equal(600_000, pin.ChangeSats);
            var btc = BtcTx.Parse(pin.TxHex, Network.Main);
            Assert.Equal(BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, btc.Outputs[0].ScriptPubKey);
            Assert.Equal(vault.Script, btc.Outputs[1].ScriptPubKey);

            // The lock is reserved, the exit is open, nothing changed for the owner.
            var reserved = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.True(reserved.IsUnlocked);
            Assert.Equal(0.004M, reserved.UnlockedAmount);
            Assert.False(Record(burn).IsComplete);
            Assert.Equal(exit.Hash, Record(burn).ExitTxHash);
            Assert.False(VBTCBridgeConsumedBurn.IsConsumed(burn));
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));

            // No caster completes or fails it by hand, and the same burn cannot be exited twice.
            await RefusedInBlock(ExitComplete(burn, pin.Txid), "completed by the chain when Bitcoin shows its transactions");
            await RefusedInBlock(ExitFail(burn, exit, (lockId, vault.Uid, 0.004M)), "failed by the chain");
            await RefusedInBlock(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)), "Duplicate Base burn");

            // The signers sign the pinned transaction; Bitcoin confirms it; the validators report it.
            await SignAndConfirm(pin);
            part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Settled, part.State);
            Assert.Equal(pin.Txid, part.SettledTxid);
            var done = Record(burn);
            Assert.True(done.IsComplete);
            Assert.False(done.IsFailedRecord);
            Assert.Equal(pin.Txid, done.BtcTxHash);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
            await RefusedInBlock(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)), "Duplicate Base burn");

            // The vault holds 0.006 and all of it is the owner's: the 0.004 was debited once, at the lock.
            Assert.Equal(600_000, BtcAttestStore.Total(vault.Uid).UnspentSats);
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).PinnedSats);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            await RefusedInBlock(Transfer(vault.Uid, 0.00600001M), "Insufficient vBTC balance");
            await Mine(Transfer(vault.Uid, 0.006M));
            Assert.Equal(0M, await Spendable(vault.Uid, Minter));
        }

        [Fact]
        public async Task AHoldersLock_PaidOutByAnExit_LeavesTheOwnerWhatTheVaultStillHolds()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            await Mine(Transfer(vault.Uid, 0.004M));                    // the owner pays a holder
            var lockId = await Lock(vault.Uid, 0.004M, _holder);         // who takes it to Base
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            await SignAndConfirm(Part(burn, vault.Uid).Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.Equal(0.006M, BtcAttestBalance.DepositBtc(vault.Uid));
            Assert.Equal(0.006M, await Spendable(vault.Uid, Minter));
            Assert.Equal(0M, await Spendable(vault.Uid, _holder));
        }

        // ── Several vaults ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExitOnTwoVaults_IsPinnedOnBothInOneBlock_OrOnNeither()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var b = await CreateVault(H1 + 2);
            await Deposit(a.Script, 1_000_000);
            await Deposit(b.Script, 1_000_000);
            var lockA = await Lock(a.Uid, 0.003M);
            var lockB = await Lock(b.Uid, 0.002M);

            // Vault B's only coin is held by a withdrawal that Bitcoin has not confirmed yet.
            var withdrawal = await Mine(Request(b.Uid, 0.001M));
            var withdrawalRow = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(withdrawal.Hash, b.Uid))!;
            Assert.Equal(VaultWithdrawalState.Pinned, withdrawalRow.State);

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockA, a.Uid, 0.003M), (lockB, b.Uid, 0.002M)));
            // Vault A could pay its part now. It is not pinned: an exit is paid in full or failed in full.
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, a.Uid).State);
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, b.Uid).State);
            Assert.Contains("other vaults cannot pay", Part(burn, a.Uid).WaitingReason);
            Assert.Contains("free coins do not cover", Part(burn, b.Uid).WaitingReason);
            Assert.Equal(VaultCoinStatus.Unspent, Assert.Single(BtcAttestStore.CoinsOf(a.Uid)).Status);

            // The withdrawal confirms; its change is a free coin on vault B; the exit is pinned on both in that block.
            await SignAndConfirm(withdrawalRow.Pins[0]);
            var partA = Part(burn, a.Uid);
            var partB = Part(burn, b.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, partA.State);
            Assert.Equal(VaultWithdrawalState.Pinned, partB.State);
            Assert.Equal(Tip, partA.Pins[0].PinnedAtVfxHeight);
            Assert.Equal(Tip, partB.Pins[0].PinnedAtVfxHeight);
            Assert.Equal(300_000 - 1_540, partA.Pins[0].DestinationSats);
            Assert.Equal(200_000 - 1_540, partB.Pins[0].DestinationSats);

            // One vault's transaction confirms: the burn is used up, the exit is not complete yet.
            await SignAndConfirm(partA.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Settled, Part(burn, a.Uid).State);
            Assert.False(Record(burn).IsComplete);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));

            await SignAndConfirm(partB.Pins[0]);
            var done = Record(burn);
            Assert.True(done.IsComplete);
            Assert.False(done.IsFailedRecord);
            var inVaultOrder = new[] { (a.Uid, partA.Pins[0].Txid), (b.Uid, partB.Pins[0].Txid) }.OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => p.Item2);
            Assert.Equal(string.Join(",", inVaultOrder), done.BtcTxHash);

            // Each owner figure is what its vault still holds.
            Assert.Equal(0.007M, await Spendable(a.Uid, Minter));
            Assert.Equal(0.007M, await Spendable(b.Uid, Minter)); // 0.01 - 0.002 locked - 0.001 withdrawn
            Assert.Equal(0.007M, BtcAttestBalance.DepositBtc(a.Uid));
            Assert.Equal(0.007M, BtcAttestBalance.DepositBtc(b.Uid));
        }

        // ── Failure by rule ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExitThatCannotBePinned_IsFailedByRule_ItsLocksComeBack_AndItCanBeMadeAgain()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var withdrawal = await Mine(Request(vault.Uid, 0.001M));   // holds the vault's only coin
            var withdrawalRow = BtcAttestStore.Withdrawal(BtcWithdrawals.RefOf(withdrawal.Hash, vault.Uid))!;

            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var mined = Tip;
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault.Uid).State);
            Assert.True(VBTCBridgeLockState.GetByLockId(lockId)!.IsUnlocked);

            // No caster can fail it, and no signer will sign anything for it.
            await RefusedInBlock(ExitFail(burn, exit, (lockId, vault.Uid, 0.004M)), "failed by the chain");
            var anything = Pay(new[] { new OutPoint(uint256.Parse(withdrawalRow.Pins[0].Inputs[0].Split(':')[0]), 0) },
                (BitcoinAddress.Create(BtcDest, Network.Main).ScriptPubKey, 398_460), (vault.Script, 600_000));
            var (signs, why) = FrostSigningAuthorization.Authorize(
                Announce(vault.Uid, vault.Script, BtcWithdrawals.ExitSigningReference(burn, vault.Uid), anything, Signers[1].Address));
            Assert.False(signs);
            Assert.Contains("No Bitcoin transaction is fixed in state", why);

            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS);
            Assert.Equal(VaultWithdrawalState.Waiting, Part(burn, vault.Uid).State); // the last block of its window
            Assert.False(Record(burn).IsComplete);
            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);

            var firstAttempt = BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid);
            var expired = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Expired, expired.State);
            Assert.Equal(Tip, expired.ClosedAtVfxHeight);
            var failed = Record(burn);
            Assert.True(failed.IsFailedRecord);
            Assert.StartsWith("FAILED:expired@", failed.BtcTxHash);
            var restored = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.False(restored.IsUnlocked);
            Assert.False(restored.IsBlacklisted);
            Assert.Equal(0M, restored.UnlockedAmount);
            Assert.Equal(0.004M, restored.RemainingAmount);
            Assert.False(VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).SettledExitSats);

            // The withdrawal confirms; the casters make the exit again from the restored lock; this time it is paid.
            await SignAndConfirm(withdrawalRow.Pins[0]);
            Assert.Equal(VaultWithdrawalState.Expired, Part(burn, vault.Uid).State);
            // Even the very same transaction, mined again, is a new attempt with parts of its own.
            await Mine(exit);
            Assert.Equal(Tip, Record(burn).ExitVfxHeight);
            Assert.NotEqual(firstAttempt, BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid));
            Assert.Equal(VaultWithdrawalState.Expired, BtcAttestStore.Withdrawal(firstAttempt)!.State); // the failed attempt stays closed
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            await SignAndConfirm(part.Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.Equal(0.005M, await Spendable(vault.Uid, Minter)); // 0.01 - 0.004 locked - 0.001 withdrawn
            Assert.Equal(0.005M, BtcAttestBalance.DepositBtc(vault.Uid));
        }

        /// <summary>
        /// Following an open watched exit, and making it again once failed, rested on a caster's memory: after a restart
        /// the catch-up marked recent bridge transactions complete and read nothing older, and an exit failed by rule
        /// afterwards was never made again (fifth review, round 23). A restarted caster takes them up from the chain.
        /// </summary>
        [Fact]
        public async Task ARestartedCaster_TakesUpTheWatchedExitsTheChainHoldsOpenOrHasFailed()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            await Mine(Request(vault.Uid, 0.001M));   // holds the vault's only coin: the exit cannot be pinned
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var mined = Tip;

            Assert.Equal(1, BurnExitConsensusService.ReloadUnfinishedWatchedExits(Tip));
            var open = BurnExitConsensusService.ProcessedBurnForTests(burn)!;
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.VfxTxCreated, open.Status);   // followed by the stuck-exit check
            Assert.Equal(exit.Hash, open.CompletionVfxTxHash);
            Assert.Equal(BtcDest, open.BtcDestination);
            Assert.Equal(0.004M, open.Amount);

            await Apply(mined + VBTCWithdrawalRequest.EXPIRY_BLOCKS + 1);
            Assert.True(Record(burn).IsFailedRecord);
            Assert.Equal(1, BurnExitConsensusService.ReloadUnfinishedWatchedExits(Tip));
            var failed = BurnExitConsensusService.ProcessedBurnForTests(burn)!;
            Assert.Equal(BurnExitConsensusService.BurnExitStatus.VfxTxCreated, failed.Status); // made again by the stuck-exit check
            Assert.Equal("", failed.CompletionVfxTxHash);

            // Paid: nothing to take up.
            var record = Record(burn);
            record.IsComplete = true; record.BtcTxHash = "aa";
            VBTCBridgeBtcExitState.Update(record);
            Assert.Equal(0, BurnExitConsensusService.ReloadUnfinishedWatchedExits(Tip));
        }

        /// <summary>
        /// The watcher starts before the node knows it is a caster: round 23's read-back ran on the first poll, handed no
        /// burn on, and moved past them (fifth review, round 24). It runs once the node handles burns, from where the
        /// cursor stood when it last did not.
        /// </summary>
        [Fact]
        public async Task TheWatcher_ReadsBaseAgain_OnceTheNodeHandlesBurns()
        {
            await Start();
            BaseBridgeExitWatchService.ResetRewindForTests();
            var state = new BridgeExitSyncState { LastScannedBlock = 10_000 };
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: false);   // started, not yet a caster
            state.LastScannedBlock = 10_050;                                       // polls move on meanwhile
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: false);
            Assert.Equal(10_050, state.LastScannedBlock);
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: true);
            Assert.Equal(10_000 - BaseBridgeExitWatchService.RestartRewindBlocks, state.LastScannedBlock);
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: true);    // once
            Assert.Equal(10_000 - BaseBridgeExitWatchService.RestartRewindBlocks, state.LastScannedBlock);

            // A caster that loses its place and gets it back reads again from where it stopped handling burns.
            state.LastScannedBlock = 12_000;
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: false);
            state.LastScannedBlock = 12_400;
            BaseBridgeExitWatchService.ConsumeRewind(state, handling: true);
            Assert.Equal(12_000 - BaseBridgeExitWatchService.RestartRewindBlocks, state.LastScannedBlock);
            BaseBridgeExitWatchService.ResetRewindForTests();
        }

        [Fact]
        public async Task APinnedExitCoinSpentByAnotherTransaction_VoidsThePart_AndTheExitCanBeMadeFromOtherLocks()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            var other = await CreateVault(H1 + 2);
            var deposit = await Deposit(vault.Script, 1_000_000);
            await Deposit(other.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var otherLock = await Lock(other.Uid, 0.004M);
            var burn = NewBurn();
            await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var attempt = BtcWithdrawals.ExitRefOf(Record(burn), vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, Part(burn, vault.Uid).State);

            // The coin leaves in a transaction that is not the pinned one (signers that signed something else).
            await Report(_btc.Mine(Pay(new[] { new OutPoint(deposit, 0) }, (Elsewhere(), 20_000), (vault.Script, 979_000))));

            var part = BtcAttestStore.Withdrawal(attempt)!;
            Assert.Equal(VaultWithdrawalState.Void, part.State);
            Assert.True(part.Pins[0].Dead);
            Assert.StartsWith("FAILED:void@", Record(burn).BtcTxHash);
            var restored = VBTCBridgeLockState.GetByLockId(lockId)!;
            Assert.Equal(0M, restored.UnlockedAmount);
            Assert.True(restored.IsBlacklisted);          // this vault's signers did what they must never do
            Assert.False(VBTCBridgeConsumedBurn.IsBurnUsedAnywhere(burn));
            Assert.Equal(0, BtcAttestStore.Total(vault.Uid).SettledExitSats);

            // The burn is still owed; another vault's lock pays it.
            Assert.Equal(otherLock, Assert.Single(BridgePoolUnlockService.ComputeExitAllocationPlan(0.004M, null, Tip + 1)!).LockId);
            await Mine(Exit(burn, BtcDest, (otherLock, other.Uid, 0.004M)));
            await SignAndConfirm(Part(burn, other.Uid).Pins[0]);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.True(VBTCBridgeConsumedBurn.IsConsumed(burn));
        }

        // ── What an exit may draw on ────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task AnExit_DrawsOnWatchedVaultsOrOlderOnes_NeverBoth_AndNeverOnAPartTooSmallToPay()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var watched = await CreateVault(H1 + 1);
            await Deposit(watched.Script, 1_000_000);
            var olderLock = await Lock(older.Uid, 0.002M);
            var watchedLock = await Lock(watched.Uid, 0.003M);

            await RefusedInBlock(Exit(NewBurn(), BtcDest, (olderLock, older.Uid, 0.001M), (watchedLock, watched.Uid, 0.001M)), "not on both");
            // The lock's own record says which vault it belongs to, whatever the transaction claims.
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (olderLock, watched.Uid, 0.001M), (watchedLock, watched.Uid, 0.001M)), "not on both");
            await RefusedInBlock(Exit(NewBurn(), BtcDest, (watchedLock, watched.Uid, 0.00025185M)), "at least 25186 sats");
            await RefusedInBlock(Exit(NewBurn(), "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx", (watchedLock, watched.Uid, 0.001M)), "not an address on this network");
            // To the address of the vault that would pay it: the payment could not be told from the change, and no signer would sign it.
            await RefusedInBlock(Exit(NewBurn(), watched.Address, (watchedLock, watched.Uid, 0.001M)), "is the deposit address of vault");

            // The smallest part a vault can pay is accepted and pinned: what is left after its first fee (10 sat/vB) can
            // still pay four doublings of it and an output of the dust limit.
            var smallBurn = NewBurn();
            await Mine(Exit(smallBurn, BtcDest, (watchedLock, watched.Uid, 0.00025186M)));
            var pin = Assert.Single(Part(smallBurn, watched.Uid).Pins);
            Assert.Equal(25_186 - 1_540, pin.DestinationSats);

            // An exit on older vaults only is not touched by these rules: it is paid the earlier way, and nothing is pinned for it.
            var burn = NewBurn();
            var old = await Mine(Exit(burn, BtcDest, (olderLock, older.Uid, 0.001M)));
            Assert.Null(BtcAttestStore.Withdrawal(BtcWithdrawals.ExitRefOf(Record(burn), older.Uid)));
            Assert.Empty(BtcAttestStore.ExitParts(old.Hash, Tip));
            Assert.Null(BtcWithdrawals.ExitRefusal(ExitComplete(burn, new string('b', 64))));
            Assert.True((await Verify(ExitComplete(burn, new string('b', 64)), Tip + 1)).Ok);
            // Closed only by the caster that made it: another could fail an exit this one had paid (round 20).
            var otherCaster = Signers[1];
            var notMine = SignedBy(otherCaster, otherCaster.Address, TransactionType.VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL, new
            {
                Function = "VBTCBridgeExitToBTCFail()", BaseBurnTxHash = burn, ExitTxHash = old.Hash, Reason = "test",
                FailedAllocations = new[] { new PoolUnlockAllocation { LockId = olderLock, SmartContractUID = older.Uid, UnlockAmount = 0.001M } },
            }, free: true);
            await RefusedInBlock(notMine, "only by the caster that made it");
            // From the freeze no failure is taken either.
            Assert.Contains("no longer accepted", BtcLegacyFreeze.Refusal(notMine, Globals.BitcoinAttestationHeight));

            // The caster fails an older-vault exit whose vault could not sign, and makes the exit again from what is left:
            // watched vaults' locks too (the handler's own retry; round 14's rule against it stranded honest burns and was
            // withdrawn in round 15).
            var failedBurn = NewBurn();
            var failedExit = await Mine(Exit(failedBurn, BtcDest, (olderLock, older.Uid, 0.0005M)));
            await Mine(ExitFail(failedBurn, failedExit, (olderLock, older.Uid, 0.0005M)));
            Assert.True(Record(failedBurn).IsFailedRecord);
            var (retryOk, retryWhy) = await Verify(Exit(failedBurn, BtcDest, (watchedLock, watched.Uid, 0.0005M)), Tip + 1);
            Assert.True(retryOk, retryWhy);
        }

        /// <summary>
        /// The handler fails the part of an older exit some vaults could not sign and completes the rest. A failure marked
        /// the whole exit failed: a completion mined later was refused, the burn was never consumed, and the burn could be
        /// exited again, the paid part paid twice (fifth review, round 21). A partial failure leaves the exit open.
        /// </summary>
        [Fact]
        public async Task APartialFailureOfAnOlderExit_LeavesItOpenForItsCompletion()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var first = await Lock(older.Uid, 0.002M);
            var second = await Lock(older.Uid, 0.002M);
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (first, older.Uid, 0.001M), (second, older.Uid, 0.001M)));
            await Mine(ExitFail(burn, exit, (second, older.Uid, 0.001M)));

            var record = Record(burn);
            Assert.False(record.IsFailedRecord);
            Assert.False(record.IsComplete);
            Assert.Equal(first, Assert.Single(JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(record.AllocationsJson!)!).LockId);
            await RefusedInBlock(Exit(burn, BtcDest, (second, older.Uid, 0.001M)), "Duplicate Base burn");
            var (ok, why) = await Verify(ExitComplete(burn, new string('c', 64)), Tip + 1);
            Assert.True(ok, why);
        }

        /// <summary>
        /// Two failures of the same part of an older exit in one block: each is judged on the plan the block before left,
        /// so both passed, and applied together the second found the trimmed plan, matched nothing and failed the whole
        /// exit - whose paid part could then be exited and paid again. The block takes one failure per exit; the second
        /// is refused there, and in a later block it is refused because the part is no longer in the plan.
        /// </summary>
        [Fact]
        public async Task TwoFailuresOfOnePartOfAnOlderExit_InOneBlock_TheSecondIsRefused()
        {
            var older = await CreateVault(H1 - 50);
            await Start();
            var first = await Lock(older.Uid, 0.002M);
            var second = await Lock(older.Uid, 0.002M);
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (first, older.Uid, 0.001M), (second, older.Uid, 0.001M)));
            var one = ExitFail(burn, exit, (second, older.Uid, 0.001M));
            var two = ExitFail("0x" + burn[2..].ToUpperInvariant(), exit, (second, older.Uid, 0.001M));   // another spelling: another transaction
            Assert.NotEqual(one.Hash, two.Hash);
            Assert.True((await Verify(one, Tip + 1)).Ok);
            Assert.True((await Verify(two, Tip + 1)).Ok);   // each alone passes on the block before's state

            // The block's guard (BlockValidatorService.ValidateBlock and ProcessTxPool offer it every bridge transaction).
            Assert.True(BridgeIntraBlockGuard.AppliesTo(two.TransactionType));
            var guard = new BridgeIntraBlockGuard.State();
            Assert.True(BridgeIntraBlockGuard.TryRegister(one, guard, Tip + 1).Ok);
            var (ok, why) = BridgeIntraBlockGuard.TryRegister(two, guard, Tip + 1);
            Assert.False(ok);
            Assert.StartsWith(BridgeIntraBlockGuard.ExitAlreadyFailedPrefix, why);
            Assert.False(VerifiedXCore.Services.BlockValidatorService.IsStateCorruptionSignal(why));   // a producer's fault: no restore or rebuild

            await Apply(Tip + 1, one);
            var record = Record(burn);
            Assert.False(record.IsFailedRecord);                 // the paid part stays open for its completion
            Assert.False(record.IsComplete);
            Assert.Equal(first, Assert.Single(JsonConvert.DeserializeObject<List<PoolUnlockAllocation>>(record.AllocationsJson!)!).LockId);
            await RefusedInBlock(two, "not part of this exit's plan");
        }

        /// <summary>
        /// The plan takes the oldest locks first and gave up when a vault's share came out under the smallest part an
        /// exit may have. One small lock at the front of the queue (a remnant of an earlier exit, or one made on purpose)
        /// then ended the plan for every exit that reached it. The vault is left out and the plan made without it.
        /// </summary>
        [Fact]
        public async Task ASmallLockAtTheFrontOfTheQueue_IsLeftOutOfThePlan_NotTheEndOfIt()
        {
            await Start();
            var small = await CreateVault(H1 + 1);
            var large = await CreateVault(H1 + 2);
            await Deposit(small.Script, 500_000);
            await Deposit(large.Script, 1_000_000);
            var smallLock = await Lock(small.Uid, 0.0001M);      // 10,000 sats: under the smallest part
            var largeLock = await Lock(large.Uid, 0.005M);

            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.002M, null, Tip + 1);
            Assert.NotNull(plan);
            Assert.Equal(largeLock, Assert.Single(plan!).LockId);
            Assert.Equal(0.002M, plan[0].UnlockAmount);
            Assert.DoesNotContain(plan, a => a.LockId == smallLock);
            // More than the rest can pay is still not planned.
            Assert.Null(BridgePoolUnlockService.ComputeExitAllocationPlan(0.00505M, null, Tip + 1));
        }

        /// <summary>
        /// An exit paying a vault it draws on is refused, and the plan never looked at the destination: a burn to a watched
        /// vault's deposit address was refused at every attempt and never paid (fifth review, round 23).
        /// </summary>
        [Fact]
        public async Task AnExitToAVaultsOwnDepositAddress_IsPlannedOnOtherVaults()
        {
            await Start();
            var first = await CreateVault(H1 + 1);
            var second = await CreateVault(H1 + 2);
            await Deposit(first.Script, 1_000_000);
            await Deposit(second.Script, 1_000_000);
            var firstLock = await Lock(first.Uid, 0.005M);
            var secondLock = await Lock(second.Uid, 0.005M);
            var firstAddress = BtcAttestStore.ScriptRowsForVault(first.Uid).First().DepositAddress;

            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.002M, null, Tip + 1, firstAddress)!;
            Assert.Equal(secondLock, Assert.Single(plan).LockId);
            var (ok, why) = await Verify(Exit(NewBurn(), firstAddress, plan.Select(a => (a.LockId, a.SmartContractUID, a.UnlockAmount)).ToArray()), Tip + 1);
            Assert.True(ok, why);
            // The first vault's lock (the queue may take it first: both locks can share a second) would be refused.
            Assert.False((await Verify(Exit(NewBurn(), firstAddress, (firstLock, first.Uid, 0.002M)), Tip + 1)).Item1);
        }

        /// <summary>
        /// A lock made before the path-audit rules could carry an id consensus no longer takes in a transaction (not
        /// printable ASCII, spaces around): every plan reaching it was refused, freezing unlocks and exits (round 26).
        /// </summary>
        [Fact]
        public async Task ALockWhoseIdCannotBeNamed_IsLeftOutOfThePlan()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var odd = await Lock(vault.Uid, 0.002M);
            var next = await Lock(vault.Uid, 0.005M);
            foreach (var id in new[] { "été", " padded " })
            {
                var record = VBTCBridgeLockState.GetByLockId(odd)!;
                VBTCBridgeLockState.GetCollection().Delete(record.LockId);
                record.LockId = id; odd = id;
                VBTCBridgeLockState.GetCollection().Insert(record);
                var plan = BridgePoolUnlockService.ComputeAllocationPlan(0.003M)!;
                Assert.Equal(next, Assert.Single(plan).LockId);
            }
        }

        /// <summary>
        /// A plan is one transaction under the size cap: tiny locks at the front of the queue made every plan reaching
        /// them too large, and those burns went unpaid (round 27). Dust is passed over first; a plan names 100 locks at most.
        /// </summary>
        [Fact]
        public async Task TinyLocksAtTheFront_DoNotMakeEveryPlanTooLarge()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 2_000_000);
            for (var i = 0; i < 5; i++) await Lock(vault.Uid, 0.00001M);
            var big = await Lock(vault.Uid, 0.01M);
            Assert.Equal(big, Assert.Single(BridgePoolUnlockService.ComputeAllocationPlan(0.005M)!).LockId);   // dust passed over
            Assert.Equal(6, BridgePoolUnlockService.ComputeAllocationPlan(0.01005M)!.Count);                  // needed: used
            Assert.False(BridgePoolUnlockService.Nameable(new string('a', BridgePoolUnlockService.MaxLockIdLength + 1)));
        }

        /// <summary>
        /// Passing over dust could leave a vault's part under the exit minimum that its dust would have lifted over it;
        /// the vault was then left out and the exit could not be planned (round 28). Each kind of plan is tried first.
        /// </summary>
        [Fact]
        public async Task AVaultsDust_IsDrawnWhenItLiftsTheVaultsPartOverTheMinimum()
        {
            await Start();
            var b = await CreateVault(H1 + 1);
            var c = await CreateVault(H1 + 2);
            await Deposit(b.Script, 1_000_000);
            await Deposit(c.Script, 1_000_000);
            var b1 = await Lock(b.Uid, 0.00009M);   // dust
            var b2 = await Lock(b.Uid, 0.0002M);
            var c1 = await Lock(c.Uid, 0.001M);
            long t = VBTCBridgeLockState.GetByLockId(b1)!.LockTimestamp;
            foreach (var (id, at) in new[] { (b1, t), (b2, t + 1), (c1, t + 2) })   // b1, b2, c1 in that order
            {
                var rec = VBTCBridgeLockState.GetByLockId(id)!;
                rec.LockTimestamp = at;
                VBTCBridgeLockState.GetCollection().Update(rec);
            }
            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.0012M, null, Tip + 1);
            Assert.NotNull(plan);
            Assert.Equal(0.0012M, plan!.Sum(a => a.UnlockAmount));
            Assert.Contains(plan, a => a.LockId == b1);
        }

        /// <summary>
        /// A vault's part under the exit minimum is lifted by moving the shortfall from another vault that can spare it,
        /// where the plan was given up (round 29).
        /// </summary>
        [Fact]
        public async Task AVaultsPartUnderTheMinimum_IsLiftedFromAnotherVault()
        {
            await Start();
            var a = await CreateVault(H1 + 1);
            var b = await CreateVault(H1 + 2);
            await Deposit(a.Script, 2_000_000);
            await Deposit(b.Script, 2_000_000);
            var la = await Lock(a.Uid, 0.01M);
            var lb = await Lock(b.Uid, 0.01M);
            var at = VBTCBridgeLockState.GetByLockId(la)!.LockTimestamp;
            var rb = VBTCBridgeLockState.GetByLockId(lb)!;
            rb.LockTimestamp = at + 1; VBTCBridgeLockState.GetCollection().Update(rb);                   // a, then b
            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.0101M, null, Tip + 1)!;
            Assert.NotNull(plan);
            Assert.Equal(0.0101M, plan.Sum(x => x.UnlockAmount));
            Assert.All(plan.GroupBy(x => x.SmartContractUID), g => Assert.True(g.Sum(x => x.UnlockAmount) * 100_000_000M >= BtcWithdrawals.MinExitPartSats));
            var (ok, why) = await Verify(Exit(NewBurn(), BtcDest, plan.Select(x => (x.LockId, x.SmartContractUID, x.UnlockAmount)).ToArray()), Tip + 1);
            Assert.True(ok, why);
        }

        /// <summary>A plan is measured as it is carried (escapes included) and one over the room is not offered (round 29).</summary>
        [Fact]
        public async Task APlanTooLargeToCarry_IsNotOffered()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 5_000_000);
            var small = VBTCBridgeLockState.GetByLockId(await Lock(vault.Uid, 0.0001M))!;
            for (var i = 0; i < 90; i++)
            {
                small.LockId = new string('"', 60) + i.ToString("D3");                                   // an id that escapes to four bytes a character
                VBTCBridgeLockState.GetCollection().Insert(small);
            }
            var big = await Lock(vault.Uid, 0.03M);
            var bigRecord = VBTCBridgeLockState.GetByLockId(big)!;
            bigRecord.LockTimestamp = small.LockTimestamp + 1_000;
            VBTCBridgeLockState.GetCollection().Update(bigRecord);
            var plan = BridgePoolUnlockService.ComputeAllocationPlan(0.02M);
            Assert.NotNull(plan);
            Assert.True(BridgePoolUnlockService.PlanBytes(plan!) <= BridgePoolUnlockService.MaxPlanBytes);
        }

        /// <summary>More locks at the front than one plan names: the largest are taken, and the burn is paid (round 28).</summary>
        [Fact]
        public async Task MoreSmallLocksAtTheFrontThanOnePlanNames_TheLargestArePlanned()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 5_000_000);
            var small = VBTCBridgeLockState.GetByLockId(await Lock(vault.Uid, 0.0001M))!;   // just above the dust line
            for (var i = 0; i < BridgePoolUnlockService.MaxPlanAllocations; i++)
            {
                small.LockId = Guid.NewGuid().ToString("N");
                VBTCBridgeLockState.GetCollection().Insert(small);                               // as many again at the front
            }
            var big = await Lock(vault.Uid, 0.03M);
            var bigRecord = VBTCBridgeLockState.GetByLockId(big)!;
            bigRecord.LockTimestamp = small.LockTimestamp + 1_000;                              // behind every small lock
            VBTCBridgeLockState.GetCollection().Update(bigRecord);
            var plan = BridgePoolUnlockService.ComputeAllocationPlan(0.02M);
            Assert.NotNull(plan);                                                  // paid (the cap returned no plan)
            Assert.Equal(0.02M, plan!.Sum(a => a.UnlockAmount));
            Assert.True(plan.Count <= BridgePoolUnlockService.MaxPlanAllocations);
            Assert.Contains(plan, a => a.LockId == big);
        }

        /// <summary>
        /// Locks made before amounts were held to 8 places (VX-01) can hold a fraction of a satoshi; the plan drew it, and
        /// consensus, which takes whole satoshis from the path-audit rules (round 15), refused every plan that reached it
        /// (fifth review, round 16). A lock is drawn down to its last whole satoshi.
        /// </summary>
        [Fact]
        public async Task ALockWithAFractionOfASatoshi_IsPlannedInWholeSatoshis()
        {
            await Start();
            var vault = await CreateVault(H1 + 1);
            await Deposit(vault.Script, 1_000_000);
            var odd = await Lock(vault.Uid, 0.002M);
            var next = await Lock(vault.Uid, 0.005M);
            var record = VBTCBridgeLockState.GetByLockId(odd)!;
            record.Amount = 0.002000005M; // as an older lock could be
            VBTCBridgeLockState.GetCollection().Update(record);

            // An amount that needs both locks, whichever the queue takes first.
            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.006M, null, Tip + 1)!;
            Assert.Contains(plan, a => a.LockId == odd);
            Assert.Equal(0.006M, plan.Sum(a => a.UnlockAmount));
            Assert.All(plan, a => Assert.Equal(Math.Round(a.UnlockAmount, 8), a.UnlockAmount));
            var (ok, why) = await Verify(Exit(NewBurn(), BtcDest, plan.Select(a => (a.LockId, a.SmartContractUID, a.UnlockAmount)).ToArray()), Tip + 1);
            Assert.True(ok, why);
        }

        [Fact]
        public async Task TheCastersPlan_UsesOlderVaultsFirst_ThenWatchedOnes_AndOnlyWatchedOnesOnceOldVaultsAreFrozen()
        {
            const long h2 = H1 + 300;
            var older = await CreateVault(H1 - 50);
            await Start();
            var watched = await CreateVault(H1 + 1);
            await Deposit(watched.Script, 1_000_000);
            var watchedLock = await Lock(watched.Uid, 0.003M);
            var olderLock = await Lock(older.Uid, 0.002M);
            Globals.BitcoinAttestationHeight = h2;                // restored by the base class

            // Before the enforcement height: older vaults pay while they can, so that they empty.
            var plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.001M, null, h2 - 1)!;
            Assert.Equal(olderLock, Assert.Single(plan).LockId);
            // More than the older vaults hold: watched vaults pay all of it. The two are never combined.
            plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.0025M, null, h2 - 1)!;
            Assert.Equal(watchedLock, Assert.Single(plan).LockId);
            Assert.Equal(0.0025M, plan[0].UnlockAmount);
            Assert.Null(BridgePoolUnlockService.ComputeExitAllocationPlan(0.004M, null, h2 - 1));
            // A part no Bitcoin transaction could pay is not planned.
            Assert.Null(BridgePoolUnlockService.ComputeExitAllocationPlan(0.00002M, new HashSet<string> { older.Uid }, h2 - 1));

            // From the enforcement height: watched vaults only, and consensus refuses an exit on a frozen one.
            plan = BridgePoolUnlockService.ComputeExitAllocationPlan(0.001M, null, h2)!;
            Assert.Equal(watchedLock, Assert.Single(plan).LockId);
            var onFrozen = Exit(NewBurn(), BtcDest, (olderLock, older.Uid, 0.001M));
            Assert.True((await Verify(onFrozen, h2 - 1)).Ok);
            await RefusedInBlock(onFrozen, "has been frozen since block " + h2, h2);

            // An unlock back to VFX is planned and judged the same way: it would hand out vBTC on a frozen vault.
            // (Both locks, in whichever order the pool holds them: they were mined in the same second.)
            Assert.Equal(new[] { watchedLock, olderLock }.OrderBy(l => l, StringComparer.Ordinal),
                BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.005M, h2 - 1)!.Select(a => a.LockId).OrderBy(l => l, StringComparer.Ordinal));
            Assert.Null(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.004M, h2));
            Assert.Equal(watchedLock, Assert.Single(BridgePoolUnlockService.ComputeUnlockAllocationPlan(0.003M, h2)!).LockId);
            VfxTx Unlock(params (string LockId, string Vault, decimal Amount)[] allocations) => SignedBy(Caster, Caster.Address, TransactionType.VBTC_V2_BRIDGE_POOL_UNLOCK, new
            {
                Function = "VBTCBridgePoolUnlock()", ExitBurnTxHash = NewBurn(), VfxDestinationAddress = _holder.Address,
                TotalAmount = allocations.Sum(a => a.Amount), TotalAmountSats = (long)(allocations.Sum(a => a.Amount) * 100_000_000M),
                Allocations = allocations.Select(a => new PoolUnlockAllocation { LockId = a.LockId, SmartContractUID = a.Vault, UnlockAmount = a.Amount }).ToList(),
            }, free: true);
            Assert.Null(BtcLegacyFreeze.Refusal(Unlock((olderLock, older.Uid, 0.001M)), h2 - 1));
            Assert.Contains("has been frozen since block " + h2, BtcLegacyFreeze.Refusal(Unlock((olderLock, older.Uid, 0.001M)), h2));
            Assert.Contains("has been frozen since block " + h2, BtcLegacyFreeze.Refusal(Unlock((watchedLock, watched.Uid, 0.001M), (olderLock, watched.Uid, 0.001M)), h2));
            Assert.Null(BtcLegacyFreeze.Refusal(Unlock((watchedLock, watched.Uid, 0.001M)), h2));
        }

        // ── Signing ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheVaultsSigners_SignThePinnedExitTransaction_AndNoOther_WithoutACaster()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();
            var exit = await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var part = Part(burn, vault.Uid);
            var pin = Assert.Single(part.Pins);
            var pinned = BtcTx.Parse(pin.TxHex, Network.Main);
            var reference = BtcWithdrawals.ExitSigningReference(burn, vault.Uid);

            // Any signer of the vault may lead: the transaction is the same whoever does. Somebody who is neither a
            // signer of the vault nor a caster may not.
            var (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Signers[2].Address));
            Assert.True(ok, why);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Unfunded.Address));
            Assert.False(ok);
            Assert.Contains("led by a committee caster", why);

            // A caster leading a transaction of its own making: right destination, within the vault's share, change to
            // the vault. Every earlier rule passes it. It is not the pinned one.
            var another = BtcTx.Parse(pin.TxHex, Network.Main);
            another.Outputs[0].Value -= Money.Satoshis(20_000);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, another, Caster.Address));
            Assert.False(ok, "a signer would have signed an exit transaction that is not the pinned one");
            Assert.Contains("Only the Bitcoin transaction fixed in state", why);

            // The signer loop takes the exit's part as it takes a withdrawal.
            var led = new List<(VaultWithdrawalRow Row, VaultPin Pin)>();
            BtcWithdrawalSigner.SignAndBroadcast = (row, p) => { led.Add((row, p)); return Task.FromResult((true, p.Txid)); };
            BtcWithdrawalSigner.KnownOnBitcoin = _ => Task.FromResult<bool?>(false);
            var txids = await BtcWithdrawalSigner.TickAsync(Signers[1].Address, pin.PinnedAtVfxHeight + 4 * BtcWithdrawalSigner.TurnBlocks);
            Assert.Equal(new[] { pin.Txid }, txids);
            var (ledRow, ledPin) = Assert.Single(led);
            Assert.True(ledRow.IsBridgeExit);
            Assert.Equal(burn, ledRow.ExitBurnHash);
            Assert.Equal(pin.Txid, ledPin.Txid);

            // Once Bitcoin shows it, nothing more is signed for this exit.
            await SignAndConfirm(pin);
            (ok, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, pinned, Signers[2].Address));
            Assert.False(ok);
            Assert.Contains("already completed", why);
        }

        /// <summary>
        /// An exit has no requester on this chain who could raise its Bitcoin fee, and a pinned part cannot be given
        /// up. A part fixed at 10 sat/vB while Bitcoin asks for more would hold its vault's coins and its locks for as
        /// long as that lasted. So the chain raises the fee itself, by rule: after 2,160 blocks unconfirmed, a
        /// replacement at twice the rate on the same coins, the fee out of the amount paid.
        /// </summary>
        [Fact]
        public async Task AnExitPartThatDoesNotConfirm_HasItsFeeRaisedByRule_NobodySendsAnything()
        {
            await Start();
            var vault = await CreateVault(H1 + 1, keyHolders: Signers);
            await Deposit(vault.Script, 1_000_000);
            var lockId = await Lock(vault.Uid, 0.004M);
            var burn = NewBurn();
            await Mine(Exit(burn, BtcDest, (lockId, vault.Uid, 0.004M)));
            var first = Assert.Single(Part(burn, vault.Uid).Pins);
            Assert.Equal(10, first.FeeRate);
            Assert.Equal(400_000 - 1_540, first.DestinationSats);

            // Not before 2,160 blocks, and then at the next block the rule looks (every thirtieth).
            long Looks(long from) => from % BtcAttestRules.TakeRetryBlocks == 0 ? from : from + BtcAttestRules.TakeRetryBlocks - from % BtcAttestRules.TakeRetryBlocks;
            var due = Looks(first.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks);
            await AdvanceTo(due - BtcAttestRules.TakeRetryBlocks);
            Assert.Single(Part(burn, vault.Uid).Pins);
            await AdvanceTo(due);
            var part = Part(burn, vault.Uid);
            Assert.Equal(VaultWithdrawalState.Pinned, part.State);
            Assert.Equal(2, part.Pins.Count);
            Assert.True(part.Pins[0].Superseded);
            var second = part.Pins[1];
            Assert.Equal(20, second.FeeRate);
            Assert.Equal(154 * 20, second.FeeSats);
            Assert.Equal(400_000 - 154 * 20, second.DestinationSats);   // the larger fee comes out of what the exit pays
            Assert.Equal(first.ChangeSats, second.ChangeSats);           // the vault gets back exactly what it would have
            Assert.Equal(first.Inputs, second.Inputs);
            Assert.Equal(due, second.PinnedAtVfxHeight);

            // The signers sign the replacement (and the first, should a miner take that one), and nothing else.
            var replacement = BtcTx.Parse(second.TxHex, Network.Main);
            var reference = BtcWithdrawals.ExitSigningReference(burn, vault.Uid);
            var (signs, why) = FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, replacement, Signers[2].Address));
            Assert.True(signs, why);
            Assert.True(FrostSigningAuthorization.Authorize(Announce(vault.Uid, vault.Script, reference, BtcTx.Parse(first.TxHex, Network.Main), Signers[2].Address)).Item1);

            // Still nothing: twice again, 2,160 blocks later.
            var again = Looks(second.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks);
            await AdvanceTo(again);
            Assert.Equal(40, Part(burn, vault.Uid).Pins[2].FeeRate);

            // Bitcoin takes the second one. The exit is complete, and the owner is added back the amount, whatever the fee was.
            await SignAndConfirm(second);
            Assert.Equal(VaultWithdrawalState.Settled, Part(burn, vault.Uid).State);
            Assert.Equal(second.Txid, Part(burn, vault.Uid).SettledTxid);
            Assert.True(Record(burn).IsComplete);
            Assert.False(Record(burn).IsFailedRecord);
            Assert.Equal(400_000, BtcAttestStore.Total(vault.Uid).SettledExitSats);
            Assert.Equal(600_000, BtcAttestStore.Total(vault.Uid).HeldSats);

            // The smallest part an exit may have bears four doublings (10 to 160 sat/vB), and then keeps what it has:
            // a fifth would leave nothing to pay.
            Assert.Equal(25_186, BtcWithdrawals.MinExitPartSats);
            var smallLock = await Lock(vault.Uid, 0.00025186M);
            var smallBurn = NewBurn();
            await Mine(Exit(smallBurn, BtcDest, (smallLock, vault.Uid, 0.00025186M)));
            for (var doubling = 1; doubling <= BtcWithdrawals.ExitPartBearsDoublings; doubling++)
            {
                var newest = Part(smallBurn, vault.Uid).Pins[^1];
                await AdvanceTo(Looks(newest.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks));
                Assert.Equal(doubling + 1, Part(smallBurn, vault.Uid).Pins.Count);
            }
            var last = Part(smallBurn, vault.Uid).Pins[^1];
            Assert.Equal(160, last.FeeRate);
            Assert.Equal(546, last.DestinationSats);                                         // the fee at 160 sat/vB and an output above dust
            await AdvanceTo(Looks(last.PinnedAtVfxHeight + BtcAttestRules.StuckPinBlocks));
            Assert.Equal(BtcWithdrawals.ExitPartBearsDoublings + 1, Part(smallBurn, vault.Uid).Pins.Count);
        }
    }
}
