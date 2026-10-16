using System;
using System.Collections.Generic;
using System.IO;
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
    /// Round 37: a signing request names its withdrawal exactly as the chain records it. The sign/start handler runs
    /// FrostSigningAuthorization.Authorize before the durable evidence check, the tracker, the session and the pins, and
    /// all of those key on the request's string as sent, so Authorize refuses any other spelling of the same withdrawal
    /// (surrounding whitespace, another letter case). The recorded spelling is still authorised.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_WithdrawalReferenceSpellingTests : IDisposable
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
        private const string Wrh = "4f1e3a9c7b2d5e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9a0b1c2d3e4f50617";
        private const decimal Amount = 0.5M;

        public Round37_WithdrawalReferenceSpellingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37wrh_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        /// <summary>One vault coin in; the destination gets the amount less the fee; change back to the vault.</summary>
        private FrostSigningStartRequest Request(string withdrawalRequestHash)
        {
            var prevTxid = new uint256(RandomUtils.GetBytes(32));
            const long coinSats = 200_000_000;
            var amountSats = (long)(Amount * 100_000_000M);
            var tx = Net.CreateTransaction();
            tx.Inputs.Add(new OutPoint(prevTxid, 1));
            tx.Outputs.Add(Money.Satoshis(amountSats - 2_000), _destAddr);
            tx.Outputs.Add(Money.Satoshis(coinSats - amountSats), _vaultAddr);
            var spent = new[] { new TxOut(Money.Satoshis(coinSats), _vaultAddr.ScriptPubKey) };
            var pre = tx.PrecomputeTransactionData(spent);
            var sighash = Convert.ToHexString(tx.GetSignatureHashTaproot(pre, new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default }).ToBytes()).ToLowerInvariant();
            return new FrostSigningStartRequest
            {
                SessionId = Guid.NewGuid().ToString("N"), SmartContractUID = _vault, WithdrawalRequestHash = withdrawalRequestHash, LeaderAddress = Owner,
                Timestamp = TimeUtil.GetTime(), LeaderSignature = "sig", SignerAddresses = new List<string> { "xV1", "xV2", "xV3" }, RequiredThreshold = 2,
                InputIndex = 0, InputCount = 1, UnsignedTxHex = tx.ToHex(),
                Prevouts = new List<PinnedWithdrawalCoin> { new() { TxId = prevTxid.ToString(), Vout = 1, ValueSats = (ulong)coinSats, ScriptPubKeyHex = _vaultAddr.ScriptPubKey.ToHex() } },
                AllInputSighashes = new List<string> { sighash }, MessageHash = sighash,
            };
        }

        [Fact]
        public void TheRecordedSpelling_IsAuthorised()
        {
            var (ok, reason) = FrostSigningAuthorization.Authorize(Request(Wrh));
            Assert.True(ok, reason);
        }

        [Theory]
        [InlineData(Wrh + " ")]
        [InlineData(" " + Wrh)]
        [InlineData(Wrh + "\t")]
        [InlineData("4F1E3A9C7B2D5E8F0A1B2C3D4E5F60718293A4B5C6D7E8F9A0B1C2D3E4F50617")]
        public void AnotherSpellingOfTheSameWithdrawal_IsRefused(string spelling)
        {
            // The withdrawal row is found under this spelling too (the store's lookups trim and ignore case)...
            Assert.NotNull(VBTCWithdrawalRequest.GetByTransactionHash(spelling.Trim(), _vault));
            // ...but no share is given for it: every key downstream would treat it as a different withdrawal.
            var (ok, reason) = FrostSigningAuthorization.Authorize(Request(spelling));
            Assert.False(ok);
            Assert.Equal(FrostSigningAuthorization.NonCanonicalReference, reason);
        }

        /// <summary>
        /// A withdrawal whose cancellation was approved (and refunded) is not signed, though its row reads open: the
        /// withdrawal-store rebuild puts a lost row back as requested before it applies how the withdrawal ended.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void ARefundedWithdrawal_IsNotSignedThoughItsRowReadsOpen(bool approved, bool signed)
        {
            VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
            {
                CancellationUID = VBTCWithdrawalCancellation.OnChainUidPrefix + "c1", SmartContractUID = _vault, OwnerAddress = Owner, WithdrawalRequestHash = Wrh,
                BTCTxHash = "", FailureProof = "", RequestTime = TimeUtil.GetTime(), ValidatorVotes = new Dictionary<string, bool>(),
                ApproveCount = approved ? 3 : 0, RejectCount = approved ? 0 : 3, IsApproved = approved, IsProcessed = true, RequestBlockHeight = 450,
            });
            Assert.Equal(VBTCWithdrawalStatus.Requested, VBTCWithdrawalRequest.GetByTransactionHash(Wrh, _vault)!.Status);

            var (ok, reason) = FrostSigningAuthorization.Authorize(Request(Wrh));
            Assert.True(ok == signed, reason);
        }

        [Fact]
        public void WhileTheWithdrawalStoreIsRebuilt_NothingIsSignedFromIt()
        {
            try
            {
                VBTCWithdrawalStoreRebuildService.SetRunningForTests(true);
                var (ok, reason) = FrostSigningAuthorization.Authorize(Request(Wrh));
                Assert.False(ok);
                Assert.Contains("being rebuilt", reason);
            }
            finally { VBTCWithdrawalStoreRebuildService.SetRunningForTests(false); }
            Assert.True(FrostSigningAuthorization.Authorize(Request(Wrh)).Ok);
        }
    }
}
