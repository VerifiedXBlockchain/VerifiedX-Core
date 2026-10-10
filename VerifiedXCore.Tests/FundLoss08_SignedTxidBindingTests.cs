using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NBitcoin;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.FROST;
using VerifiedXCore.Bitcoin.FROST.Models;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Block = VerifiedXCore.Models.Block;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 8, fifth review. The review read the signing session as storing "the txid the leader sends, not
    /// one derived from what they signed". The sign/start handler runs FrostSigningAuthorization.Authorize on every request
    /// before it builds the session, and Authorize (a) rebuilds every input's sighash from the unsigned transaction and
    /// refuses a MessageHash that is not that transaction's, (b) refuses a leader txid or outpoint list that differs from
    /// the transaction, and (c) overwrites both request fields with the values it derived. The session, the contract pin
    /// and the durable signing record are then filled from those fields. These tests pin (a)-(c) on the real function, with
    /// a real unsigned Taproot transaction and chain state, and follow the derived value into the signing record that the
    /// item 8 checks read. (The handler's own lines between Authorize and the session are not executed here: that needs a
    /// FROST key package and the native signing library.)
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss08_SignedTxidBindingTests : IDisposable
    {
        private static readonly Network Net = Network.TestNet;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Network? _priorNetwork;
        private readonly Block _priorLastBlock;
        private readonly BitcoinAddress _vaultAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net);
        private readonly BitcoinAddress _destAddr = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
        private readonly string _vault = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
        private const string Owner = "xWithdrawalOwner000000000000000001";
        private const string Wrh = "withdrawal-request-hash-1";
        private const decimal Amount = 0.5M;

        public FundLoss08_SignedTxidBindingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl08b_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorNetwork = Globals.BTCNetwork;
            Globals.BTCNetwork = Net;
            _priorLastBlock = Globals.LastBlock;
            Globals.LastBlock = new Block { Height = 500 };
            DbContext.Initialize();
            CompletedWithdrawalConfirmation.ResetForTests();

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = _vault, ContractData = VbtcTestContracts.VaultContractData(_vault, Owner, _vaultAddr.ToString(), "02" + new string('a', 64)),
                MinterAddress = Owner, OwnerAddress = Owner, IsLocked = false, Nonce = 0,
            });
            Assert.True(VBTCWithdrawalRequest.Save(new VBTCWithdrawalRequest
            {
                RequestorAddress = Owner, OriginalUniqueId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault, TransactionHash = Wrh,
                Amount = Amount, BTCDestination = _destAddr.ToString(), FeeRate = 10, Timestamp = TimeUtil.GetTime(), RequestBlockHeight = 400,
                Status = VBTCWithdrawalStatus.Requested, IsCompleted = false,
            }));
        }

        public void Dispose()
        {
            CompletedWithdrawalConfirmation.ResetForTests();
            try { DbContext.CloseDB(); } catch { }
            Globals.BTCNetwork = _priorNetwork;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        /// <summary>The transaction the coordinator builds: one vault coin in, destination gets amount less fee, change back to the vault.</summary>
        private (NBitcoin.Transaction Tx, List<PinnedWithdrawalCoin> Prevouts, List<string> Sighashes) Withdrawal(decimal coinBtc = 2.0M, long feeSats = 2_000)
        {
            var prevTxid = new uint256(RandomUtils.GetBytes(32));
            var coinSats = (long)(coinBtc * 100_000_000M);
            var amountSats = (long)(Amount * 100_000_000M);
            var tx = Net.CreateTransaction();
            tx.Inputs.Add(new OutPoint(prevTxid, 1));
            tx.Outputs.Add(Money.Satoshis(amountSats - feeSats), _destAddr);
            tx.Outputs.Add(Money.Satoshis(coinSats - amountSats), _vaultAddr);
            var prevouts = new List<PinnedWithdrawalCoin> { new() { TxId = prevTxid.ToString(), Vout = 1, ValueSats = (ulong)coinSats, ScriptPubKeyHex = _vaultAddr.ScriptPubKey.ToHex() } };
            var spent = new[] { new TxOut(Money.Satoshis(coinSats), _vaultAddr.ScriptPubKey) };
            var pre = tx.PrecomputeTransactionData(spent);
            var sighash = Convert.ToHexString(tx.GetSignatureHashTaproot(pre, new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant();
            return (tx, prevouts, new List<string> { sighash });
        }

        private FrostSigningStartRequest Request(NBitcoin.Transaction tx, List<PinnedWithdrawalCoin> prevouts, List<string> sighashes) => new()
        {
            SessionId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault, WithdrawalRequestHash = Wrh, LeaderAddress = Owner, Timestamp = TimeUtil.GetTime(),
            LeaderSignature = "sig", SignerAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 2,
            InputIndex = 0, InputCount = 1, UnsignedTxHex = tx.ToHex(), Prevouts = prevouts, AllInputSighashes = sighashes, MessageHash = sighashes[0],
        };

        [Fact]
        public void TheTxidAValidatorRecords_IsDerivedFromTheTransactionItAuthorised()
        {
            var (tx, prevouts, sighashes) = Withdrawal();
            var realTxid = tx.GetHash().ToString();
            var realOutpoints = new List<string> { $"{tx.Inputs[0].PrevOut.Hash}:1" };

            // The leader names nothing: the fields are filled from the transaction.
            var silent = Request(tx, prevouts, sighashes);
            var (ok, reason) = FrostSigningAuthorization.Authorize(silent);
            Assert.True(ok, reason);
            Assert.Equal(realTxid, silent.BtcTxId);
            Assert.Equal(realOutpoints, silent.TxInputOutpoints);

            // The leader names the right txid in another spelling: accepted, and stored as derived.
            var shouting = Request(tx, prevouts, sighashes);
            shouting.BtcTxId = "  " + realTxid.ToUpperInvariant() + " ";
            shouting.TxInputOutpoints = new List<string> { realOutpoints[0].ToUpperInvariant() };
            (ok, reason) = FrostSigningAuthorization.Authorize(shouting);
            Assert.True(ok, reason);
            Assert.Equal(realTxid, shouting.BtcTxId);
            Assert.Equal(realOutpoints, shouting.TxInputOutpoints);

            // The leader names a different txid (the review's case): refused. Nothing is signed, nothing recorded.
            var lying = Request(tx, prevouts, sighashes);
            lying.BtcTxId = new string('b', 64);
            (ok, reason) = FrostSigningAuthorization.Authorize(lying);
            Assert.False(ok);
            Assert.Equal("BtcTxId does not match the transaction", reason);

            // ...or different outpoints (they drive the contract pin).
            var wrongCoins = Request(tx, prevouts, sighashes);
            wrongCoins.TxInputOutpoints = new List<string> { new string('c', 64) + ":0" };
            (ok, reason) = FrostSigningAuthorization.Authorize(wrongCoins);
            Assert.False(ok);
            Assert.Equal("TxInputOutpoints do not match the transaction", reason);
        }

        /// <summary>The share is produced over MessageHash, and MessageHash must be the sighash of the transaction the txid was derived from.</summary>
        [Fact]
        public void AShareCannotBeRequestedForOneTransaction_WhileNamingAnother()
        {
            var (tx, prevouts, sighashes) = Withdrawal();
            var (other, _, otherSighashes) = Withdrawal(coinBtc: 3.0M);

            // Honest txid and transaction, but the hash to sign belongs to a different transaction.
            var swapped = Request(tx, prevouts, sighashes);
            swapped.MessageHash = otherSighashes[0];
            var (ok, reason) = FrostSigningAuthorization.Authorize(swapped);
            Assert.False(ok);
            Assert.Equal("MessageHash does not match the sighash of the requested input", reason);

            // The announced sighash list replaced as well: it no longer matches the transaction.
            var swappedList = Request(tx, prevouts, otherSighashes);
            (ok, reason) = FrostSigningAuthorization.Authorize(swappedList);
            Assert.False(ok);
            Assert.Contains("does not match the transaction", reason);

            // A transaction of another shape is not an authorised spend at all (so it has no txid to record).
            var stranger = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net);
            var (theft, theftPrev, _) = Withdrawal();
            theft.Outputs[0].ScriptPubKey = stranger.ScriptPubKey;
            var spent = new[] { new TxOut(Money.Satoshis((long)theftPrev[0].ValueSats), _vaultAddr.ScriptPubKey) };
            var theftSighash = Convert.ToHexString(theft.GetSignatureHashTaproot(theft.PrecomputeTransactionData(spent), new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant();
            (ok, reason) = FrostSigningAuthorization.Authorize(Request(theft, theftPrev, new List<string> { theftSighash }));
            Assert.False(ok);
            Assert.Contains("neither the authorized destination nor vault change", reason);
            Assert.NotEqual(tx.GetHash(), other.GetHash());
        }

        /// <summary>
        /// From authorisation to the item 8 checks: the handler hands the authorised request's txid and outpoints to the
        /// signing tracker when the share is generated; that is the durable record every signer holds, and it is what a
        /// COMPLETE and the owner add-back are judged against.
        /// </summary>
        [Fact]
        public void TheDerivedTxid_IsWhatACompleteIsJudgedAgainst_OnEverySigner()
        {
            var (tx, prevouts, sighashes) = Withdrawal();
            var realTxid = tx.GetHash().ToString();
            var request = Request(tx, prevouts, sighashes);
            var (ok, reason) = FrostSigningAuthorization.Authorize(request);
            Assert.True(ok, reason);

            // What FrostStartup's round 2 does once this validator's share exists (session fields come from the authorised request).
            FrostWithdrawalSigningTracker.RecordSigningCompleted(request.SmartContractUID, request.WithdrawalRequestHash!, request.SessionId,
                request.InputIndex, request.MessageHash, request.TxInputOutpoints, request.BtcTxId);

            var evidence = FrostSignedWithdrawalEvidence.Get(_vault, Wrh);
            Assert.NotNull(evidence);
            Assert.Equal(realTxid, evidence!.BtcTxId);
            Assert.Equal(realTxid, CompletedWithdrawalConfirmation.SignedTxIdFor(_vault, Wrh, null));   // no coordinator row field involved

            // A COMPLETE naming the signed transaction is admitted; one naming anything else is refused by this signer.
            Assert.Null(CompletedWithdrawalConfirmation.AdmissionRefusal(_vault, Wrh, realTxid.ToUpperInvariant(), null));
            var refusal = CompletedWithdrawalConfirmation.AdmissionRefusal(_vault, Wrh, new string('d', 64), null);
            Assert.NotNull(refusal);
            Assert.Contains("This node signed Bitcoin transaction " + realTxid, refusal);

            try { FrostWithdrawalSigningTracker.ClearContractPin(_vault, Wrh, "test cleanup"); } catch { }
        }
    }
}
