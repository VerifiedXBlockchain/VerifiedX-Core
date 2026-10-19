using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A reserve account's delayed moves are judged again when they fall due (second path audit, 14 October 2026).
    ///
    /// A reserve account's transfer is mined now and takes effect at its unlock time, a day or more later. Nothing is
    /// taken from the sender meanwhile, and at the unlock time the move was applied without looking again:
    /// - a vBTC transfer made as a vault's owner was applied after the vault had a new owner, who could spend the
    ///   same balance: one Bitcoin claimed twice;
    /// - a called-back row cleared the lock of whatever contract it named, so a reserve could leave a second, later
    ///   transfer of a contract pending and take the contract back from whoever owned it by then.
    ///
    /// Transactions are judged by VerifyTX as in a block, applied with UpdateTreis, and fall due through
    /// ReserveService.Run, as after every block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit2_ReserveTests : IDisposable
    {
        private const long Rule = 300;
        private const long Hour = 3600;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly long _priorRecord = Globals.BitcoinAttestationRecordHeight;
        private readonly long _priorFreeze = Globals.BitcoinAttestationHeight;
        private readonly long _now = TimeUtil.GetTime();
        private readonly (PrivateKey Key, string Pub, string Address) _reserve;
        private readonly (PrivateKey Key, string Pub, string Address) _firstBuyer = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _secondBuyer = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _recipient = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _later = NewKey();
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":1755000001";
        private long _height = Rule;

        public PathAudit2_ReserveTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa2reserve_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.BitcoinAttestationRecordHeight = long.MaxValue;
            Globals.BitcoinAttestationHeight = long.MaxValue;

            // Not every key yields the xRBX prefix; keep drawing until one does.
            PrivateKey key; string pub, address;
            do
            {
                key = new PrivateKey("secp256k1");
                pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                address = ReserveAccount.GetHumanAddress(pub);
            } while (!address.StartsWith("xRBX", StringComparison.Ordinal));
            _reserve = (key, pub, address);

            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve.Address, Balance = 100M, Nonce = 0, RecoveryAccount = _later.Address });
            foreach (var who in new[] { _firstBuyer, _secondBuyer, _recipient, _later })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = who.Address, Balance = 10M, Nonce = 0 });
            // A vault (of the earlier kind: its Bitcoin is not in state) owned by the reserve account.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = _vault, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = _reserve.Address, OwnerAddress = _reserve.Address, Nonce = 0,
            });
            // The block the rules start at: everything below falls due after it.
            BlockchainData.GetBlocks().InsertSafe(new Block { Height = Rule, Hash = "rule-block", Timestamp = _now - 60, Transactions = new List<Transaction>() });
            Globals.LastBlock = new Block { Height = Rule, Timestamp = _now - 60 };
            Globals.PathAuditRulesHeight = Rule;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.BitcoinAttestationRecordHeight = _priorRecord;
            Globals.BitcoinAttestationHeight = _priorFreeze;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>The rules as they were before: the height is far above the chain, so nothing here is under them.</summary>
        private static void BeforeTheRule() => Globals.PathAuditRulesHeight = 1_000_000;

        private SmartContractStateTrei Vault => SmartContractStateTrei.GetSmartContractState(_vault)!;
        private decimal Held(string address) =>
            Vault.SCStateTreiTokenizationTXes?.Where(x => x.FromAddress == address || x.ToAddress == address).Sum(x => x.Amount) ?? 0M;

        private Transaction FromReserve(TransactionType type, string to, object data, long? unlockInHours)
        {
            var tx = new Transaction
            {
                Timestamp = _now, FromAddress = _reserve.Address, ToAddress = to, Amount = 0.0M, Fee = 0,
                Nonce = StateData.GetSpecificAccountStateTrei(_reserve.Address)!.Nonce,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
                UnlockTime = unlockInHours == null ? null : _now + unlockInHours.Value * Hour,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _reserve.Key, _reserve.Pub);
            return tx;
        }

        private Transaction SendVbtc(string to, decimal amount, long unlockInHours) =>
            FromReserve(TransactionType.VBTC_V2_TRANSFER, to, new { Function = "TransferVBTCV2()", ContractUID = _vault, FromAddress = _reserve.Address, ToAddress = to, Amount = amount }, unlockInHours);

        private Transaction TransferVault(string to, long unlockInHours) =>
            FromReserve(TransactionType.TKNZ_TX, to, new[] { new { Function = "Transfer()", ContractUID = _vault, ToAddress = to, Data = Vault.ContractData, MD5List = "NA" } }, unlockInHours);

        private Transaction CallBack(Transaction pending) =>
            FromReserve(TransactionType.RESERVE, "Reserve_Base", new { Function = "CallBack()", Hash = pending.Hash }, null);

        /// <summary>Mines a block at a time (hours from now): its transactions are judged and applied, then what has fallen due takes effect.</summary>
        private async Task Mine(long atHours, params Transaction[] txs)
        {
            _height++;
            foreach (var tx in txs)
            {
                var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, _height);
                Assert.True(ok, $"{tx.TransactionType} refused at block {_height}: {why}");
                tx.Height = _height;
            }
            var block = new Block { Height = _height, Hash = $"b{_height}", StateRoot = "root" + _height, Timestamp = _now + atHours * Hour, Transactions = txs.ToList() };
            BlockchainData.GetBlocks().InsertSafe(block);
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
            await ReserveService.Run();
        }

        private async Task<string> Refusal(Transaction tx)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, _height + 1);
            Assert.False(ok, $"{tx.TransactionType} was accepted");
            return why;
        }

        private static ReserveTransactionStatus StatusOf(Transaction tx) => ReserveTransactions.GetTransactions(tx.Hash)!.ReserveTransactionStatus;

        // ── A delayed vBTC transfer and a change of the vault's owner ───────────────────────────────────────

        private async Task SendAsOwner_ThenTheVaultChangesHands(Transaction send)
        {
            // As the vault's owner the reserve sends 1 vBTC, to take effect in three days. Only a pending row is written.
            await Mine(0, send);
            Assert.Equal(ReserveTransactionStatus.Pending, StatusOf(send));
            Assert.Equal(0M, Held(_reserve.Address));
            // It then transfers the vault itself, to take effect in a day: a pending vBTC transfer does not hold the vault.
            await Mine(0, TransferVault(_firstBuyer.Address, 25));
            await Mine(26);
            Assert.Equal(_firstBuyer.Address, Vault.OwnerAddress);
        }

        [Fact]
        public async Task BeforeTheRule_AVbtcTransferMadeAsOwner_WasAppliedAfterTheVaultHadANewOwner()
        {
            BeforeTheRule();
            var send = SendVbtc(_recipient.Address, 1.0M, 72);
            await SendAsOwner_ThenTheVaultChangesHands(send);
            await Mine(73);
            // The hole: the recipient holds 1 vBTC, on a vault whose whole balance is now its new owner's to spend.
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(send));
            Assert.Equal(1.0M, Held(_recipient.Address));
            Assert.Equal(-1.0M, Held(_reserve.Address));
        }

        [Fact]
        public async Task ADelayedVbtcTransfer_IsNotApplied_OnceItsSenderNoLongerOwnsTheVault()
        {
            var send = SendVbtc(_recipient.Address, 1.0M, 72);
            await SendAsOwner_ThenTheVaultChangesHands(send);
            await Mine(73);
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(send));
            Assert.Equal(0M, Held(_recipient.Address));
            Assert.Equal(0M, Held(_reserve.Address));
        }

        [Fact]
        public async Task ADelayedVbtcTransfer_IsApplied_WhenItsSenderStillOwnsTheVault_OrHoldsTheVbtcItself()
        {
            // Still the owner when it falls due: applied as before.
            var send = SendVbtc(_recipient.Address, 1.0M, 30);
            await Mine(0, send);
            await Mine(31);
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(send));
            Assert.Equal(1.0M, Held(_recipient.Address));

            // The vault is transferred away. What the former owner then receives as an ordinary holder is its own, and a
            // delayed transfer of it is applied; one for more than it holds is not.
            await Mine(31, TransferVault(_firstBuyer.Address, 31 + 25));
            await Mine(31 + 26);
            Assert.Equal(_firstBuyer.Address, Vault.OwnerAddress);
            var contract = Vault;
            contract.SCStateTreiTokenizationTXes!.Add(new SmartContractStateTreiTokenizationTX { Amount = 1.5M, FromAddress = "+", ToAddress = _reserve.Address });
            contract.SCStateTreiTokenizationTXes.Add(new SmartContractStateTreiTokenizationTX { Amount = -1.5M, FromAddress = _firstBuyer.Address, ToAddress = "-" });
            SmartContractStateTrei.UpdateSmartContract(contract);
            Assert.Equal(0.5M, Held(_reserve.Address));   // -1 from its owner days, +1.5 received since

            var within = SendVbtc(_later.Address, 0.5M, 60 + 25);
            await Mine(60, within);
            await Mine(60 + 26);
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(within));
            Assert.Equal(0.5M, Held(_later.Address));
            Assert.Equal(0M, Held(_reserve.Address));
        }

        /// <summary>
        /// A reserve's vBTC transfer is judged against a vault's holder limit when it is sent, and others may fill the
        /// vault before it falls due: judged again then, a transfer to an address the full vault does not name is called
        /// back and the reserve keeps its vBTC (fourth review, round 6).
        /// </summary>
        [Fact]
        public async Task ADelayedVbtcTransfer_ToANewAddress_IsCalledBack_IfTheWatchedVaultFilledUpMeanwhile()
        {
            var watched = Guid.NewGuid().ToString("N") + ":1755000002";
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = watched, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = _firstBuyer.Address, OwnerAddress = _firstBuyer.Address, Nonce = 0,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new() { Amount = 1.5M, FromAddress = "+", ToAddress = _reserve.Address },
                    new() { Amount = -1.5M, FromAddress = _firstBuyer.Address, ToAddress = "-" },
                },
            });
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveScript(new VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow
            {
                ScriptHex = "5120" + new string('b', 64), SmartContractUID = watched, DepositAddress = "bc1pwatched", CreatedAtVfxHeight = Rule, WatchFromBitcoinHeight = 1,
            });
            Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcAttestBalance.IsAttestedVault(watched));
            decimal HeldOn(string address) => SmartContractStateTrei.GetSmartContractState(watched)!.SCStateTreiTokenizationTXes!
                .Where(x => x.FromAddress == address || x.ToAddress == address).Sum(x => x.Amount);
            Transaction Send(string to, decimal amount, long unlockInHours) => FromReserve(TransactionType.VBTC_V2_TRANSFER, to,
                new { Function = "TransferVBTCV2()", ContractUID = watched, FromAddress = _reserve.Address, ToAddress = to, Amount = amount }, unlockInHours);

            var first = Send(_recipient.Address, 0.5M, 30);
            await Mine(0, first);
            VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.MaxHoldersOverride = VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.LedgerAddresses(SmartContractStateTrei.GetSmartContractState(watched));
            try { await Mine(31); }
            finally { VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.MaxHoldersOverride = null; }
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(first));
            Assert.Equal(0M, HeldOn(_recipient.Address));
            Assert.Equal(1.5M, HeldOn(_reserve.Address));

            // With room on the vault, the same transfer goes through.
            var second = Send(_recipient.Address, 0.5M, 60);
            await Mine(32, second);
            await Mine(61);
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(second));
            Assert.Equal(0.5M, HeldOn(_recipient.Address));
            Assert.Equal(1.0M, HeldOn(_reserve.Address));
        }

        /// <summary>
        /// Rows falling due together were read in the order of their ids, random Guids made on each node, and the checks
        /// when a row falls due read what the rows before it left: two transfers competing for the last place on a vault
        /// came out differently on different nodes. They are taken in one order everywhere: the earlier due first
        /// (fourth review, round 7).
        /// </summary>
        [Fact]
        public async Task DelayedTransfersFallingDueTogether_AreTakenInOneOrderOnEveryNode()
        {
            var watched = Guid.NewGuid().ToString("N") + ":1755000003";
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = watched, ContractData = VbtcTestContracts.VbtcV2ContractData, MinterAddress = _firstBuyer.Address, OwnerAddress = _firstBuyer.Address, Nonce = 0,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new() { Amount = 1.5M, FromAddress = "+", ToAddress = _reserve.Address },
                    new() { Amount = -1.5M, FromAddress = _firstBuyer.Address, ToAddress = "-" },
                },
            });
            VerifiedXCore.Bitcoin.Attestation.BtcAttestStore.SaveScript(new VerifiedXCore.Bitcoin.Attestation.VaultDepositScriptRow
            {
                ScriptHex = "5120" + new string('c', 64), SmartContractUID = watched, DepositAddress = "bc1pwatched3", CreatedAtVfxHeight = Rule, WatchFromBitcoinHeight = 1,
            });
            // A second reserve holding vBTC on the vault (one transfer of a reserve may be pending on a vault at a time).
            PrivateKey key; string pub, second;
            do
            {
                key = new PrivateKey("secp256k1");
                pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                second = ReserveAccount.GetHumanAddress(pub);
            } while (!second.StartsWith("xRBX", StringComparison.Ordinal));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = second, Balance = 100M, Nonce = 0, RecoveryAccount = _later.Address });
            var contract = SmartContractStateTrei.GetSmartContractState(watched)!;
            contract.SCStateTreiTokenizationTXes!.Add(new SmartContractStateTreiTokenizationTX { Amount = 1.0M, FromAddress = "+", ToAddress = second });
            contract.SCStateTreiTokenizationTXes.Add(new SmartContractStateTreiTokenizationTX { Amount = -1.0M, FromAddress = _firstBuyer.Address, ToAddress = "-" });
            SmartContractStateTrei.GetSCST().Update(contract);
            Transaction Send(string to, long unlockInHours, bool fromSecond)
            {
                if (!fromSecond)
                    return FromReserve(TransactionType.VBTC_V2_TRANSFER, to, new { Function = "TransferVBTCV2()", ContractUID = watched, FromAddress = _reserve.Address, ToAddress = to, Amount = 0.5M }, unlockInHours);
                var tx = new Transaction
                {
                    Timestamp = _now, FromAddress = second, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0, TransactionType = TransactionType.VBTC_V2_TRANSFER,
                    Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = watched, FromAddress = second, ToAddress = to, Amount = 0.5M }),
                    UnlockTime = _now + unlockInHours * Hour,
                };
                tx.Fee = FeeCalcService.CalculateTXFee(tx);
                tx.Build();
                tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
                return tx;
            }

            var later = Send(_recipient.Address, 31, fromSecond: false);
            var sooner = Send(_later.Address, 30, fromSecond: true);
            await Mine(0, later, sooner);
            // Room for one more address: both fall due in the same block, and the one due first takes the place.
            VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.MaxHoldersOverride = VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.LedgerAddresses(SmartContractStateTrei.GetSmartContractState(watched)) + 1;
            try { await Mine(32); }
            finally { VerifiedXCore.Bitcoin.Attestation.BtcVaultLedger.MaxHoldersOverride = null; }
            Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(sooner));
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(later));
        }

        // ── A called-back row and somebody else's lock ──────────────────────────────────────────────────────

        private Transaction UnknownCall(string to, long unlockInHours) =>
            FromReserve(TransactionType.TKNZ_TX, to, new[] { new { Function = "X()", ContractUID = _vault } }, unlockInHours);

        [Fact]
        public async Task BeforeTheRule_ACalledBackRow_ClearedTheLockOfAnotherTransfer_AndTheVaultWasTakenBack()
        {
            BeforeTheRule();
            await Mine(0, TransferVault(_firstBuyer.Address, 25));
            Assert.True(Vault.IsLocked);
            // A row that names the vault and does nothing, and its callback: the vault is unlocked while the first transfer is still pending.
            var nothing = UnknownCall(_firstBuyer.Address, 25);
            await Mine(0, nothing);
            await Mine(0, CallBack(nothing));
            Assert.False(Vault.IsLocked);
            Assert.Null(Vault.NextOwner);
            // So a second transfer, to fall due later, is accepted.
            await Mine(0, TransferVault(_secondBuyer.Address, 50));
            await Mine(26);
            Assert.Equal(_firstBuyer.Address, Vault.OwnerAddress);   // the first buyer has it, and state shows nothing pending
            Assert.False(Vault.IsLocked);
            await Mine(51);
            Assert.Equal(_secondBuyer.Address, Vault.OwnerAddress);  // the hole: taken from the first buyer a day later
        }

        [Fact]
        public async Task ACallback_UndoesOnlyTheLockItsOwnTransferSet()
        {
            var first = TransferVault(_firstBuyer.Address, 25);
            await Mine(0, first);
            var nothing = UnknownCall(_firstBuyer.Address, 25);
            await Mine(0, nothing);
            await Mine(0, CallBack(nothing));
            // The row is called back; the first transfer's lock stands.
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(nothing));
            Assert.True(Vault.IsLocked);
            Assert.Equal(_firstBuyer.Address, Vault.NextOwner);
            Assert.Contains("locked", await Refusal(TransferVault(_secondBuyer.Address, 50)));
            await Mine(26);
            Assert.Equal(_firstBuyer.Address, Vault.OwnerAddress);
        }

        [Fact]
        public async Task ATransferCalledBackBeforeItFallsDue_UnlocksTheContract_AsBefore()
        {
            var transfer = TransferVault(_firstBuyer.Address, 25);
            await Mine(0, transfer);
            Assert.True(Vault.IsLocked);
            await Mine(1, CallBack(transfer));
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(transfer));
            Assert.False(Vault.IsLocked);
            Assert.Null(Vault.NextOwner);
            Assert.Equal(_reserve.Address, Vault.OwnerAddress);
            await Mine(26);
            Assert.Equal(_reserve.Address, Vault.OwnerAddress);
        }

        private Transaction TransferVaultAs(TransactionType type, string to, long unlockInHours) =>
            FromReserve(type, to, new[] { new { Function = "Transfer()", ContractUID = _vault, ToAddress = to, Data = Vault.ContractData, MD5List = "NA" } }, unlockInHours);

        /// <summary>
        /// The dispatcher runs a reserve's Transfer() under any contract type, and it locks the contract with the
        /// recipient as NextOwner. From ReserveRecoverReadsEveryRowHeight a call-back unlocks it under every such type;
        /// below it only NFT_TX, TKNZ_TX and SC_TX were unlocked.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ATransferCarriedAsFtknTx_IsUnlockedByItsCallBack_FromTheRecoverRulesHeight(bool fromTheHeight)
        {
            var prior = Globals.ReserveRecoverReadsEveryRowHeight;
            try
            {
                Globals.ReserveRecoverReadsEveryRowHeight = fromTheHeight ? Rule : long.MaxValue;
                var transfer = TransferVaultAs(TransactionType.FTKN_TX, _firstBuyer.Address, 25);
                await Mine(0, transfer);
                Assert.True(Vault.IsLocked);
                Assert.Equal(_firstBuyer.Address, Vault.NextOwner);

                await Mine(1, CallBack(transfer));
                Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(transfer));
                Assert.Equal(!fromTheHeight, Vault.IsLocked);
                Assert.Equal(fromTheHeight ? null : _firstBuyer.Address, Vault.NextOwner);
                Assert.Equal(_reserve.Address, Vault.OwnerAddress);
            }
            finally { Globals.ReserveRecoverReadsEveryRowHeight = prior; }
        }

        /// <summary>
        /// A call-back naming a pending transaction's hash with a space in it found no row, then (in a block) the chain
        /// search took the spaces out, found the transaction and saved a second pending row under the spaced text: the
        /// payment was refunded twice (fifth review, round 27).
        /// </summary>
        [Fact]
        public async Task ACallBackNamingAHashRespelled_IsRefused_AndSavesNoRow()
        {
            var transfer = TransferVault(_firstBuyer.Address, 25);
            await Mine(0, transfer);
            var spaced = transfer.Hash.Insert(10, " ");
            var respelled = FromReserve(TransactionType.RESERVE, "Reserve_Base", new { Function = "CallBack()", Hash = spaced }, null);
            var (ok, why) = await TransactionValidatorService.VerifyTX(respelled, false, true, false, null, false, _height + 1);
            Assert.False(ok);
            Assert.Contains("hex", why);
            Assert.Null(ReserveTransactions.GetTransactions(spaced));
            Assert.Equal(ReserveTransactionStatus.Pending, StatusOf(transfer));

            // A row of a RESERVE transaction (only ever written by the old block-path search, on some nodes) counts as no row (round 29).
            var phantom = new string('d', 64);
            ReserveTransactions.GetReserveTransactionsDb()!.Insert(new ReserveTransactions { Id = Guid.NewGuid(), Hash = phantom, FromAddress = _reserve.Address, ToAddress = "Reserve_Base",
                TransactionType = TransactionType.RESERVE, ReserveTransactionStatus = ReserveTransactionStatus.Pending, ConfirmTimestamp = _now + 100 * Hour, UnlockTime = _now + 100 * Hour, Signature = "s" });
            var ofPhantom = FromReserve(TransactionType.RESERVE, "Reserve_Base", new { Function = "CallBack()", Hash = phantom }, null);
            var (okPhantom, whyPhantom) = await TransactionValidatorService.VerifyTX(ofPhantom, false, true, false, null, false, Math.Max(_height + 1, Globals.PathAuditRulesHeight));
            Assert.False(okPhantom);
            Assert.Contains("Could not find", whyPhantom);

            // A call-back of a hash with no row is refused in a block too, without searching the chain (round 28).
            var unknown = FromReserve(TransactionType.RESERVE, "Reserve_Base", new { Function = "CallBack()", Hash = new string('b', 64) }, null);
            var (okUnknown, whyUnknown) = await TransactionValidatorService.VerifyTX(unknown, false, true, false, null, false, Math.Max(_height + 1, Globals.PathAuditRulesHeight));
            Assert.False(okUnknown);
            Assert.Contains("Could not find", whyUnknown);
        }

        [Fact]
        public async Task ALeftoverTransferRow_DoesNotMoveAContractThatHasMovedOn()
        {
            // A pending transfer whose lock is gone and whose contract has since been sold on by its new owner (the state
            // the earlier build could be left in): when it falls due it is not applied.
            var leftover = TransferVault(_secondBuyer.Address, 50);
            await Mine(0, leftover);
            var contract = Vault;
            contract.OwnerAddress = _later.Address;
            contract.NextOwner = null;
            contract.IsLocked = false;
            SmartContractStateTrei.UpdateSmartContract(contract);

            await Mine(51);
            Assert.Equal(ReserveTransactionStatus.CalledBack, StatusOf(leftover));
            Assert.Equal(_later.Address, Vault.OwnerAddress);
        }
    
        // ── The legacy freeze (BitcoinAttestationHeight): what a reserve's settlements still move on a frozen vault ──

        private Transaction FromHolder((PrivateKey Key, string Pub, string Address) holder, TransactionType type, string to, object data)
        {
            var tx = new Transaction
            {
                Timestamp = _now, FromAddress = holder.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, holder.Key, holder.Pub);
            return tx;
        }

        /// <summary>Every debit a holder could make on the vault is refused once it is frozen.</summary>
        private async Task NothingLeavesTheFrozenVaultFrom((PrivateKey Key, string Pub, string Address) holder)
        {
            foreach (var tx in new[]
            {
                FromHolder(holder, TransactionType.VBTC_V2_TRANSFER, _firstBuyer.Address, new { Function = "TransferVBTCV2()", ContractUID = _vault, FromAddress = holder.Address, ToAddress = _firstBuyer.Address, Amount = 0.1M }),
                FromHolder(holder, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST, holder.Address, new { Function = "VBTCWithdrawalRequest()", ContractUID = _vault, BTCAddress = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4", Amount = 0.1M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString("N") }),
                FromHolder(holder, TransactionType.VBTC_V2_BRIDGE_LOCK, holder.Address, new { Function = "VBTCBridgeLock()", ContractUID = _vault, LockId = Guid.NewGuid().ToString("N"), Amount = 0.1M, AmountSats = 10_000_000L, EvmDestination = "0x" + new string('a', 40) }),
            })
                Assert.Contains("has been frozen", await Refusal(tx));
        }

        private decimal VaultTotal() => Vault.SCStateTreiTokenizationTXes?.Sum(x => x.Amount) ?? 0M;

        /// <summary>
        /// A reserve's delayed vBTC send mined before the freeze and due after it settles to its recipient on the older vault:
        /// a move inside the vault, whose total is unchanged; the recipient can then take nothing off it.
        /// </summary>
        [Fact]
        public async Task ADelayedSendDueAfterTheFreeze_SettlesInsideTheFrozenVault_AndItsRecipientCanMoveNothing()
        {
            var (priorFreeze, priorEscrow) = (Globals.BitcoinAttestationHeight, Globals.WithdrawalEscrowHeight);
            Globals.WithdrawalEscrowHeight = 1;   // a request holds its vBTC (both networks are past this height)
            try
            {
                var send = SendVbtc(_recipient.Address, 1.0M, 30);
                await Mine(0, send);
                var total = VaultTotal();
                Globals.BitcoinAttestationHeight = _height + 1;   // frozen from the next block
                Assert.True(VerifiedXCore.Bitcoin.Attestation.BtcLegacyFreeze.IsLegacyVault(_vault));

                await Mine(31);
                Assert.Equal(ReserveTransactionStatus.Confirmed, StatusOf(send));
                Assert.Equal(1.0M, Held(_recipient.Address));
                Assert.Equal(total, VaultTotal());
                await NothingLeavesTheFrozenVaultFrom(_recipient);
            }
            finally { (Globals.BitcoinAttestationHeight, Globals.WithdrawalEscrowHeight) = (priorFreeze, priorEscrow); }
        }

        /// <summary>
        /// A Recover() at the freeze height moves the reserve's vBTC on the older vault to its recovery address: a move inside
        /// the vault, whose total is unchanged; the recovery address can then take nothing off it.
        /// </summary>
        [Fact]
        public async Task ARecoverAtTheFreeze_MovesTheReservesVbtcInsideTheFrozenVault_AndTheRecoveryAddressCanMoveNothing()
        {
            var (priorFreeze, priorEscrow) = (Globals.BitcoinAttestationHeight, Globals.WithdrawalEscrowHeight);
            Globals.WithdrawalEscrowHeight = 1;   // a request holds its vBTC (both networks are past this height)
            try
            {
                var contract = Vault;
                contract.SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { Amount = 1.5M, FromAddress = "+", ToAddress = _reserve.Address },
                };
                SmartContractStateTrei.UpdateSmartContract(contract);
                var total = VaultTotal();
                Globals.BitcoinAttestationHeight = _height + 1;

                var recover = FromReserve(TransactionType.RESERVE, "Reserve_Base", new
                {
                    Function = "Recover()", RecoveryAddress = _later.Address,
                    RecoverySigScript = SignatureService.CreateSignature($"{_now}{_later.Address}", _later.Key, _later.Pub), SignatureTime = _now,
                }, null);
                await Mine(1, recover);
                Assert.Equal(1.5M, Held(_later.Address));
                Assert.Equal(0M, Held(_reserve.Address));
                Assert.Equal(total, VaultTotal());
                await NothingLeavesTheFrozenVaultFrom(_later);
            }
            finally { (Globals.BitcoinAttestationHeight, Globals.WithdrawalEscrowHeight) = (priorFreeze, priorEscrow); }
        }
}
}
