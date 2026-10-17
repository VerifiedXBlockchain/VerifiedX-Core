using System.Numerics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Nodes;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A fork correction rolls back only for a block that is the one the message names, hashes to its own hash and
    /// carries a valid committee certificate, and it goes through the shared bounded reorg, which applies the block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ForkCorrectionServiceTests : IDisposable
    {
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorChainRef = BlockchainData.ChainRef;
        private readonly Func<Block, bool> _priorCert = ForkCorrectionService.HasValidCertificate;
        private readonly Func<long, string, string, string, Block?, Task<bool>> _priorReorg = ForkCorrectionService.Reorg;
        private readonly List<(long Height, string Hash, Block? Known)> _reorgs = new();
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;

        public ForkCorrectionServiceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fork_correction_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            BlockchainData.ChainRef = "test-chain";
            Globals.LastBlock = new Block { Height = 100, Hash = "our-tip-100" };
            ForkCorrectionService.Reorg = (height, hash, ip, caller, known) => { _reorgs.Add((height, hash, known)); return Task.FromResult(true); };
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            BlockchainData.ChainRef = _priorChainRef;
            ForkCorrectionService.HasValidCertificate = _priorCert;
            ForkCorrectionService.Reorg = _priorReorg;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Block SignedBlock(long height)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var block = new Block
            {
                Height = height, Version = 4, PrevHash = $"prev-{height - 1}", Timestamp = 1_700_000_000 + height,
                NumOfTx = 0, Validator = AccountData.GetHumanAddress(pub), ValidatorAnswer = "a", ChainRefId = "test-chain",
                Transactions = new List<Transaction>(),
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        [Fact]
        public async Task ASelfSignedBlockWithoutACertificate_CausesNoRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => false;

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(SignedBlock(100)), "1.2.3.4");
            await ForkCorrectionService.HandleForkCorrectionAsync(91, JsonConvert.SerializeObject(SignedBlock(91)), "1.2.3.4");

            Assert.Empty(_reorgs);
            Assert.Equal("our-tip-100", Globals.LastBlock.Hash);
        }

        [Fact]
        public async Task AMessageHeightOtherThanTheBlocks_OrAHashTheBlockDoesNotHaveTo_IsRefused()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;

            // The block is for 100, the message claims 91 (a deeper rollback).
            await ForkCorrectionService.HandleForkCorrectionAsync(91, JsonConvert.SerializeObject(SignedBlock(100)), "1.2.3.4");

            // A signed hash the block's content does not produce.
            var tampered = SignedBlock(100);
            tampered.Timestamp += 1;
            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(tampered), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        private static Transaction SignedReserveSend()
        {
            BigInteger scalar = 1;
            while (!ReserveAccount.ReserveAddressOf(scalar).StartsWith("xRBX")) scalar++;
            var key = new PrivateKey("secp256k1", scalar);
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var tx = new Transaction
            {
                Timestamp = 1_700_000_000, FromAddress = ReserveAccount.ReserveAddressOf(scalar), ToAddress = Producer, Amount = 2.5M,
                Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.TX, Data = "[]", UnlockTime = 1_700_090_000,
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
            return tx;
        }

        private static Transaction SignedTransfer(decimal amount = 2.5M, long? unlockTime = null, string? data = null, string? to = null)
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var tx = new Transaction
            {
                Timestamp = 1_700_000_000, FromAddress = AccountData.GetHumanAddress(pub), ToAddress = to ?? Producer, Amount = amount,
                Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.TX, UnlockTime = unlockTime, Data = data,
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
            return tx;
        }

        /// <summary>A certified-looking block at <paramref name="height"/> holding a block reward and <paramref name="tx"/>.</summary>
        private static Block SignedBlockWith(long height, Transaction tx, Action<Transaction>? alterAfterSigning = null)
        {
            var reward = new Transaction { Timestamp = 1_700_000_000, FromAddress = "Coinbase_BlkRwd", ToAddress = Producer, Amount = 32M, TransactionType = TransactionType.TX };
            reward.Build();
            alterAfterSigning?.Invoke(tx); // the carried hash, and so the merkle root and block hash, stay those of the signed body
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var block = new Block
            {
                Height = height, Version = 4, PrevHash = $"prev-{height - 1}", Timestamp = 1_700_000_000 + height,
                NumOfTx = 2, Validator = AccountData.GetHumanAddress(pub), ValidatorAnswer = "a", ChainRefId = "test-chain",
                Transactions = new List<Transaction> { reward, tx },
            };
            block.MerkleRoot = block.MerkleRootOf();
            block.Hash = block.GetBlockHash();
            block.ValidatorSignature = SignatureService.CreateSignature(block.Hash, key, pub);
            return block;
        }

        [Fact]
        public async Task ACertifiedCorrectionWithIntactTransactions_StillRollsBack()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var block = SignedBlockWith(100, SignedTransfer());

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(block), "1.2.3.4");

            Assert.Single(_reorgs);
        }

        [Fact]
        public async Task ACertifiedCorrectionWithAnAlteredTransactionBody_IsRefusedBeforeAnyRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var block = SignedBlockWith(100, SignedTransfer(), tx => tx.Amount = 2500M);

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(block), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        /// <summary>A checksum-valid reserve address of the "xRBX" form the validator refuses (the prefix after "xRB" varies by key).</summary>
        private static string ReserveAddress()
        {
            for (var scalar = new System.Numerics.BigInteger(1); ; scalar++)
            {
                var address = ReserveAccount.ReserveAddressOf(scalar);
                if (address.StartsWith("xRBX"))
                    return address;
            }
        }

        private static Transaction Unshield()
        {
            var key = new PrivateKey("secp256k1");
            var recipient = AccountData.GetHumanAddress("04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
            var payload = new VerifiedXCore.Models.Privacy.PrivateTxPayload
            {
                Asset = "VFX", Kind = "unshield", NullsB64 = { Convert.ToBase64String(new byte[32]) }, MerkleRootB64 = Convert.ToBase64String(new byte[32]),
                Fee = Globals.PrivateTxFixedFee, TransparentOutput = recipient, TransparentAmount = 1.0M,
            };
            var tx = new Transaction
            {
                TransactionType = TransactionType.VFX_UNSHIELD, FromAddress = VerifiedXCore.Privacy.PrivacyConstants.ShieldedPoolAddress, ToAddress = recipient,
                Amount = 1.0M, Fee = 0.0M, Nonce = 0, Timestamp = 1_700_000_000, Signature = VerifiedXCore.Privacy.PrivacyConstants.PlonkSignatureSentinel,
                Data = VerifiedXCore.Privacy.PrivateTxPayloadCodec.SerializeToJson(payload),
            };
            tx.BuildPrivate(100);
            return tx;
        }

        private static readonly string Producer = AccountData.GetHumanAddress("04" + Convert.ToHexString(new PrivateKey("secp256k1").publicKey().toString()).ToLowerInvariant());

        private static Transaction FeeReward()
        {
            var tx = new Transaction { Timestamp = 1_700_000_000, FromAddress = "Coinbase_TrxFees", ToAddress = Producer, Amount = 32M, Fee = 0.00M, TransactionType = TransactionType.TX }; // as BlockchainData builds it
            tx.Build();
            return tx;
        }

        private static Transaction SignedShield()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var tx = new Transaction
            {
                Timestamp = 1_700_000_000, FromAddress = AccountData.GetHumanAddress(pub), ToAddress = VerifiedXCore.Privacy.PrivacyConstants.ShieldedPoolAddress,
                Amount = 5.0M, Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.VFX_SHIELD, Data = "{}", // one decimal place, as wallets build it
            };
            tx.BuildPrivate(100);
            tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
            return tx;
        }

        [Fact]
        public void AVaultSizedTransfer_IsNotRefusedForSize_WithoutTheVaultStateInHand()
        {
            var prior = Globals.VbtcVaultTxSizeHeight;
            try
            {
                Globals.VbtcVaultTxSizeHeight = 1;
                // Larger than 30 KB, within a vault's cap; the vault it moves is in no state this node has before the rollback.
                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                var tx = new Transaction
                {
                    Timestamp = 1_700_000_000, FromAddress = AccountData.GetHumanAddress(pub), ToAddress = Producer, Amount = 0.0M, Fee = 0.00001M,
                    TransactionType = TransactionType.TKNZ_TX,
                    Data = JsonConvert.SerializeObject(new[] { new { Function = "Transfer()", ContractUID = "vault:1", Data = new string('c', 40 * 1024) } }),
                };
                tx.Build();
                tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
                var block = JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(SignedBlockWith(100, tx)))!;

                Assert.Null(ForkCorrectionService.TransactionIntegrityError(block));

                // An ordinary transfer of the same size, not shaped as a vault transfer, is over the 30 KB cap.
                var plain = new Transaction
                {
                    Timestamp = 1_700_000_000, FromAddress = AccountData.GetHumanAddress(pub), ToAddress = Producer, Amount = 0.0M, Fee = 0.00001M,
                    TransactionType = TransactionType.NFT_TX, Data = JsonConvert.SerializeObject(new[] { new { Function = "Transfer()", Pad = new string('c', 40 * 1024) } }),
                };
                plain.Build();
                plain.Signature = SignatureService.CreateSignature(plain.Hash, key, pub);
                Assert.NotNull(ForkCorrectionService.TransactionIntegrityError(JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(SignedBlockWith(100, plain)))!));
            }
            finally { Globals.VbtcVaultTxSizeHeight = prior; }
        }

        [Fact]
        public void AnAttestationOverItsCap_IsRefusedBeforeRollingBack()
        {
            var big = VerifiedXCore.Bitcoin.Attestation.BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT,
                JsonConvert.SerializeObject(new { pad = new string('r', VerifiedXCore.Bitcoin.Attestation.BtcAttestRules.MaxReportTxBytes) }), 1_700_000_000);
            var block = JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(SignedBlockWith(100, big)))!;

            Assert.NotNull(ForkCorrectionService.TransactionIntegrityError(block));
        }

        [Fact]
        public void AReorgOntoTheExpectedBlock_DropsAnApprovalOfAnotherHashThere()
        {
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                Globals.CasterApprovedBlockHashDict[95] = "replaced-95";
                Globals.CasterApprovedBlockHashDict[96] = "expected-96";
                VerifiedXCore.Utilities.ForkRecoveryUtility.DropApprovalOtherThan(95, "expected-95");
                VerifiedXCore.Utilities.ForkRecoveryUtility.DropApprovalOtherThan(96, "expected-96");
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(95));
                Assert.Equal("expected-96", Globals.CasterApprovedBlockHashDict[96]);
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public void ABlockHashVote_CountsOnlyInTheSpellingABlockHashHas()
        {
            var hash = SignedBlock(95).Hash;
            Assert.True(BlockcasterNode.IsBlockHashSpelling(hash));
            Assert.False(BlockcasterNode.IsBlockHashSpelling(hash.ToUpperInvariant()));
            Assert.False(BlockcasterNode.IsBlockHashSpelling(hash + " "));
            Assert.False(BlockcasterNode.IsBlockHashSpelling(null));
        }

        [Theory]
        [InlineData("Register()")]
        [InlineData("CallBack()")]
        [InlineData("Recover()")]
        public void AReserveControlTransactionWithoutAnUnlockTime_IsNotRefused(string function)
        {
            BigInteger scalar = 1;
            while (!ReserveAccount.ReserveAddressOf(scalar).StartsWith("xRBX")) scalar++;
            var key = new PrivateKey("secp256k1", scalar);
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var tx = new Transaction
            {
                Timestamp = 1_700_000_000, FromAddress = ReserveAccount.ReserveAddressOf(scalar), ToAddress = ReserveAccount.ReserveAddressOf(scalar),
                Amount = 0.0M, Fee = 0.00001M, Nonce = 0, TransactionType = TransactionType.RESERVE,
                Data = JsonConvert.SerializeObject(new { Function = function }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, key, pub);
            var block = JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(SignedBlockWith(100, tx)))!;

            Assert.Null(ForkCorrectionService.TransactionIntegrityError(block));
        }

        [Theory]
        [InlineData("zero-amount")]
        [InlineData("attestation-signature")]
        [InlineData("shield-recipient")]
        [InlineData("unshield-reserve-recipient")]
        [InlineData("unshield-recipient")]
        [InlineData("unshield-amount")]
        [InlineData("data-unlock-resplit")]
        [InlineData("fee-reward-amount")]
        [InlineData("reward-recipient-split")]
        [InlineData("shield-fee")]
        [InlineData("reserve-unlock-collapse")]
        [InlineData("signature-padding")]
        [InlineData("recipient-amount-split")]
        public async Task AFieldOutsideTheHash_ChangedInACertifiedCorrection_IsRefusedBeforeAnyRollback(string field)
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            Transaction AsSigned() => field switch
            {
                "zero-amount" => SignedTransfer(amount: 0M), // VerifyTX re-hashes a carried fraction as 0
                "attestation-signature" => VerifiedXCore.Bitcoin.Attestation.BtcAttestTxValidator.Create(TransactionType.VBTC_V2_BTC_REPORT, "{}", 1_700_000_000),
                "fee-reward-amount" or "reward-recipient-split" => FeeReward(),
                "shield-fee" => SignedShield(),
                "reserve-unlock-collapse" => SignedReserveSend(),
                "signature-padding" => SignedTransfer(data: new string('a', LedgerIntegrityRules.DefaultMaxTxSizeBytes - 1_000)),
                "recipient-amount-split" => SignedTransfer(amount: 32.0M, to: Producer),
                "unshield-reserve-recipient" or "unshield-recipient" or "unshield-amount" => Unshield(),
                "data-unlock-resplit" => SignedTransfer(unlockTime: 1790390189),
                _ => SignedShield(),
            };
            Action<Transaction> alter = field switch
            {
                "zero-amount" => t => t.Amount = 0.5M,
                "attestation-signature" => t => t.Signature = "not-the-sentinel",
                // A checksum-valid reserve address: the private hash leaves the recipient out, and only VerifyPrivateTX refuses it.
                "unshield-reserve-recipient" => t => t.ToAddress = ReserveAddress(),
                "unshield-recipient" => t => t.ToAddress = SignedTransfer().FromAddress, // another valid address; the payload names the first
                "unshield-amount" => t => t.Amount = 2.0M,
                // Same preimage, hash and signature: digits moved from UnlockTime into Data.
                "data-unlock-resplit" => t => { t.Data = "1"; t.UnlockTime = 790390189; },
                "fee-reward-amount" => t => t.Amount = 32.5M, // no signature covers a reward's amount
                "reward-recipient-split" => t => { t.ToAddress += "3"; t.Amount = 2M; }, // "...3" + "2" hashes like "..." + "32"
                "shield-fee" => t => t.Fee = 0M, // outside the hash below the shield-fee height
                "reserve-unlock-collapse" => t => { t.Data = (t.Data ?? "") + t.UnlockTime; t.UnlockTime = null; }, // same preimage
                // Whitespace inside the Base64 part still decodes and verifies; it pushes the transaction over the cap.
                "signature-padding" => t => t.Signature = t.Signature.Replace(".", new string(' ', 2_000) + "."),
                "recipient-amount-split" => t => { t.ToAddress += "3"; t.Amount = 2.0M; }, // "...3" + "2.0" hashes like "..." + "32.0"
                _ => t => t.ToAddress = "xSomewhereElse",
            };
            var asSigned = JsonConvert.DeserializeObject<Block>(JsonConvert.SerializeObject(SignedBlockWith(100, AsSigned())))!;
            Assert.Null(ForkCorrectionService.TransactionIntegrityError(asSigned)); // as signed, accepted after crossing the wire

            var block = SignedBlockWith(100, AsSigned(), alter);
            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(block), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        [Fact]
        public async Task ACertifiedCorrectionWithABadSignature_IsRefusedBeforeAnyRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var other = SignedTransfer();
            var block = SignedBlockWith(100, SignedTransfer(), tx => tx.Signature = other.Signature); // the signature is outside the hash

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(block), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        [Fact]
        public async Task ACertifiedHeaderWithOtherTransactions_IsRefusedBeforeAnyRollback()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var altered = SignedBlock(100);
            altered.Transactions = new List<Transaction> { new Transaction { Hash = "inserted", FromAddress = "xA", ToAddress = "xB", Amount = 1M } };

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(altered), "1.2.3.4");

            Assert.Empty(_reorgs);
        }

        [Fact]
        public async Task ACertifiedCorrection_ReplacesAStaleCasterApprovedHash()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                Globals.CasterApprovedBlockHashDict[95] = "stale-95";
                Globals.CasterApprovedBlockHashDict[97] = "stale-97";
                var below = SignedBlock(95);

                await ForkCorrectionService.HandleForkCorrectionAsync(95, JsonConvert.SerializeObject(below), "1.2.3.4");

                Assert.Equal(below.Hash, Globals.CasterApprovedBlockHashDict[95]);
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(97));
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public void AutomaticRecovery_ReplacesAStaleApproval_OnlyForACertifiedReplacement()
        {
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                var replacement = SignedBlock(95);

                ForkCorrectionService.HasValidCertificate = _ => false;
                Globals.CasterApprovedBlockHashDict[95] = "stale-95";
                VerifiedXCore.Utilities.ForkRecoveryUtility.ApproveIfCertified(replacement);
                Assert.Equal("stale-95", Globals.CasterApprovedBlockHashDict[95]); // an uncertified block changes nothing

                ForkCorrectionService.HasValidCertificate = _ => true;
                Globals.CasterApprovedBlockHashDict[97] = "stale-97";
                VerifiedXCore.Utilities.ForkRecoveryUtility.ApproveIfCertified(replacement);
                Assert.Equal(replacement.Hash, Globals.CasterApprovedBlockHashDict[95]);
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(97));
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public void ACertifiedBlockThatIsNotExactlyItsOwnHash_IsNotApproved()
        {
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                ForkCorrectionService.HasValidCertificate = _ => true;
                Globals.CasterApprovedBlockHashDict.Clear();
                var recased = SignedBlock(95);
                recased.Hash = recased.Hash.ToUpperInvariant(); // the same hash spelled otherwise
                VerifiedXCore.Utilities.ForkRecoveryUtility.ApproveIfCertified(recased);
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(95));

                var unsigned = SignedBlock(95);
                unsigned.ValidatorSignature = SignedBlock(95).ValidatorSignature; // another key's signature
                VerifiedXCore.Utilities.ForkRecoveryUtility.ApproveIfCertified(unsigned);
                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(95));
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public void ACastersRollbackOfItsOwnBlock_DropsOnlyTheApprovalThatNamedIt()
        {
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                Globals.CasterApprovedBlockHashDict[95] = "rolled-back-95";
                Globals.CasterApprovedBlockHashDict[96] = "agreed-96";

                VerifiedXCore.Utilities.ForkRecoveryUtility.DropApprovalOfRolledBack(95, "rolled-back-95");
                VerifiedXCore.Utilities.ForkRecoveryUtility.DropApprovalOfRolledBack(96, "rolled-back-96");

                Assert.False(Globals.CasterApprovedBlockHashDict.ContainsKey(95));
                Assert.Equal("agreed-96", Globals.CasterApprovedBlockHashDict[96]); // an approval of another block stays
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public void AutomaticRecovery_RefusesAnExpectedReplacementWithAnAlteredBody_BeforeRollingBack()
        {
            var intact = SignedBlockWith(100, SignedTransfer());
            Assert.Null(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(intact, 100, intact.Hash));

            var altered = SignedBlockWith(100, SignedTransfer(), tx => tx.Amount = 2500M);
            Assert.NotNull(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(altered, 100, altered.Hash));

            // Nor one whose producer signature is not the producer's.
            var unsigned = SignedBlockWith(100, SignedTransfer());
            unsigned.ValidatorSignature = SignedBlockWith(100, SignedTransfer()).ValidatorSignature;
            Assert.NotNull(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(unsigned, 100, unsigned.Hash));

            // Nor, once certificates are enforced, one without a valid certificate.
            var priorEnforce = Globals.CertEnforceHeight;
            try
            {
                Globals.CertEnforceHeight = 1;
                ForkCorrectionService.HasValidCertificate = _ => false;
                Assert.NotNull(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(intact, 100, intact.Hash));
                ForkCorrectionService.HasValidCertificate = _ => true;
                Assert.Null(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(intact, 100, intact.Hash));
            }
            finally { Globals.CertEnforceHeight = priorEnforce; }

            // A source that cannot serve the expected block does not start a reorg either.
            Assert.NotNull(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(null, 100, intact.Hash));
            Assert.NotNull(VerifiedXCore.Utilities.ForkRecoveryUtility.ReplacementRefusal(intact, 100, "another-hash"));
        }

        [Fact]
        public async Task ACertifiedCorrectionForTheNextHeight_IsApprovedBeforeItIsStaged()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var prior = new System.Collections.Generic.Dictionary<long, string>(Globals.CasterApprovedBlockHashDict);
            try
            {
                Globals.CasterApprovedBlockHashDict[101] = "stale-101";
                var next = SignedBlock(101);

                await ForkCorrectionService.HandleForkCorrectionAsync(101, JsonConvert.SerializeObject(next), "1.2.3.4");

                Assert.Equal(next.Hash, Globals.CasterApprovedBlockHashDict[101]);
                Assert.Empty(_reorgs);

                // Uncertified: staged for normal validation, the approval is left alone.
                ForkCorrectionService.HasValidCertificate = _ => false;
                Globals.CasterApprovedBlockHashDict[101] = "stale-101";
                await ForkCorrectionService.HandleForkCorrectionAsync(101, JsonConvert.SerializeObject(SignedBlock(101)), "1.2.3.4");
                Assert.Equal("stale-101", Globals.CasterApprovedBlockHashDict[101]);
            }
            finally
            {
                Globals.CasterApprovedBlockHashDict.Clear();
                foreach (var kv in prior) Globals.CasterApprovedBlockHashDict[kv.Key] = kv.Value;
            }
        }

        [Fact]
        public async Task ACertifiedCorrection_GoesThroughTheReorg_WithTheBlockItCarries()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var atTip = SignedBlock(100);
            var below = SignedBlock(95);

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(atTip), "1.2.3.4");
            await ForkCorrectionService.HandleForkCorrectionAsync(95, JsonConvert.SerializeObject(below), "1.2.3.4");

            Assert.Equal(2, _reorgs.Count);
            Assert.Equal((100L, atTip.Hash), (_reorgs[0].Height, _reorgs[0].Hash));
            Assert.Equal(atTip.Hash, _reorgs[0].Known!.Hash);
            Assert.Equal((95L, below.Hash), (_reorgs[1].Height, _reorgs[1].Hash));
        }

        [Fact]
        public async Task TheSameBlockWeHold_IsANoOp()
        {
            ForkCorrectionService.HasValidCertificate = _ => true;
            var ours = SignedBlock(100);
            Globals.LastBlock = ours;

            await ForkCorrectionService.HandleForkCorrectionAsync(100, JsonConvert.SerializeObject(ours), "1.2.3.4");

            Assert.Empty(_reorgs);
        }
    }
}
