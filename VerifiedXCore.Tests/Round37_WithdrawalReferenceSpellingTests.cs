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
using Microsoft.AspNetCore.Hosting;
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

        /// <summary>
        /// C-027: a bridge exit has one signing reference, the one the casters send (BtcWithdrawals.ExitSigningReference:
        /// the burn's first 16 characters and the vault's first 8). Every other spelling the resolver would also find
        /// (the whole hash, without 0x, another case, a shorter burn prefix, the whole vault UID, surrounding whitespace)
        /// is refused as non-canonical before anything is keyed on it, so one exit cannot be signed under two keys.
        /// </summary>
        [Fact]
        public void ABridgeExit_IsSignedUnderItsOneReference_EveryOtherSpellingIsRefused()
        {
            const string burn = "0x9c2e4b7a1d3f5e6c8b0a2d4f6e8c1a3b5d7f9e0c2a4b6d8f1e3c5a7b9d0f2e4c";
            VBTCBridgeLockState.GetCollection().Insert(new VBTCBridgeLockState { LockId = "L27", SmartContractUID = _vault, OwnerAddress = Owner, Amount = Amount, AmountSats = (long)(Amount * 100_000_000M), EvmDestination = "0xabc", LockTxHash = "lt27", LockTimestamp = 1 });
            Assert.True(VBTCBridgeBtcExitState.TryInsert(new VBTCBridgeBtcExitState
            {
                BaseBurnTxHash = burn, LockId = "L27", SmartContractUID = _vault, OwnerAddress = "xCaster", Amount = Amount,
                AmountSats = (long)(Amount * 100_000_000M), BtcDestination = _destAddr.ToString(), ExitTxHash = "exit-tx-27", CreatedTimestamp = 1,
                AllocationsJson = Newtonsoft.Json.JsonConvert.SerializeObject(new List<PoolUnlockAllocation> { new() { LockId = "L27", SmartContractUID = _vault, UnlockAmount = Amount } }),
            }));

            var canonical = VerifiedXCore.Bitcoin.Attestation.BtcWithdrawals.ExitSigningReference(burn, _vault);
            Assert.NotEqual(FrostSigningAuthorization.NonCanonicalReference, FrostSigningAuthorization.Authorize(Request(canonical)).Reason);

            foreach (var spelling in new[]
            {
                $"btcexit_{burn}_{_vault[..8]}",                              // the whole hash
                $"btcexit_{burn[2..18]}_{_vault[..8]}",                       // without 0x
                $"BTCEXIT_{burn[..16]}_{_vault[..8]}",                        // the prefix in another case
                $"btcexit_{burn[..16].ToUpperInvariant()}_{_vault[..8]}",     // the burn in another case
                $"btcexit_{burn[..10]}_{_vault[..8]}",                        // a shorter burn prefix
                $"btcexit_{burn[..16]}_{_vault}",                             // the whole vault UID
                " " + canonical,                                              // surrounding whitespace
                canonical + " ",
            })
            {
                var (ok, reason) = FrostSigningAuthorization.Authorize(Request(spelling));
                Assert.False(ok, spelling);
                Assert.Equal(FrostSigningAuthorization.NonCanonicalReference, reason);
            }
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

        /// <summary>
        /// A ceremony started while the withdrawal was open; before its Round 2 the withdrawal's cancellation was approved
        /// (a session outlives the time a cancellation vote waits for a ceremony). Round 2 gives no share.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Round2_GivesNoShare_ForAWithdrawalRefundedSinceTheCeremonyStarted(bool refunded)
        {
            if (refunded)
                VBTCWithdrawalCancellation.SaveCancellation(new VBTCWithdrawalCancellation
                {
                    CancellationUID = VBTCWithdrawalCancellation.OnChainUidPrefix + "c2", SmartContractUID = _vault, OwnerAddress = Owner, WithdrawalRequestHash = Wrh,
                    BTCTxHash = "", FailureProof = "", RequestTime = TimeUtil.GetTime(), ValidatorVotes = new Dictionary<string, bool>(),
                    ApproveCount = 3, IsApproved = true, IsProcessed = true, RequestBlockHeight = 450,
                });
            var (code, body) = await Round2OnThisWithdrawal();
            if (refunded)
            {
                Assert.Equal(System.Net.HttpStatusCode.Conflict, code);
                Assert.Contains("cancelled and refunded", body);
            }
            else Assert.DoesNotContain("refunded", body);
        }

        /// <summary>
        /// A validator gives no share for a vault the chain does not watch unless it is at the network's height: after a
        /// snapshot restore or a resync its store may not yet show that a withdrawal was refunded.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]   // a fork recovery's download phase: IsResyncing is already cleared
        [InlineData(false, false)]
        public async Task Round2_GivesNoShare_WhileThisValidatorIsNotAtTheNetworksHeight(bool resyncing, bool recovering)
        {
            var (synced, wasResyncing, stopped, tip) = (Globals.IsChainSynced, Globals.IsResyncing, Globals.StopAllTimers, Globals.LastBlock);
            try
            {
                Globals.IsChainSynced = true;
                Globals.IsResyncing = resyncing;
                Globals.StopAllTimers = false;
                Globals.LastBlock = new Block { Height = 500, Timestamp = TimeUtil.GetTime() };
                VerifiedXCore.Utilities.ForkRecoveryUtility.SetRecoveryInProgressForTests(recovering);
                var (code, body) = await Round2OnThisWithdrawal();
                if (resyncing || recovering)
                {
                    Assert.Equal(System.Net.HttpStatusCode.Conflict, code);
                    Assert.Contains("not at the network's height", body);
                }
                else Assert.DoesNotContain("network's height", body);
            }
            finally
            {
                VerifiedXCore.Utilities.ForkRecoveryUtility.SetRecoveryInProgressForTests(false);
                (Globals.IsChainSynced, Globals.IsResyncing, Globals.StopAllTimers, Globals.LastBlock) = (synced, wasResyncing, stopped, tip);
            }
        }

        /// <summary>Round 2 of a ceremony this validator accepted for the withdrawal, sent by its leader.</summary>
        private async Task<(System.Net.HttpStatusCode Code, string Body)> Round2OnThisWithdrawal()
        {
            var key = new VerifiedXCore.EllipticCurve.PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var leader = AccountData.GetHumanAddress(pub);
            var session = new VerifiedXCore.Bitcoin.FROST.Models.SigningSession
            {
                SessionId = Guid.NewGuid().ToString("N"), LeaderAddress = leader, LeaderStartTimestamp = TimeUtil.GetTime(), StartTimestamp = TimeUtil.GetTime(),
                SmartContractUID = _vault, WithdrawalRequestHash = Wrh, MessageHash = new string('a', 64), MyKeyPackage = "key-package",
            };
            Assert.True(FrostSessionStorage.SigningSessions.TryAdd(session.SessionId, session));
            try
            {
                using var server = new Microsoft.AspNetCore.TestHost.TestServer(new Microsoft.AspNetCore.Hosting.WebHostBuilder().UseStartup<FrostStartup>());
                var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"/frost/sign/round2/{session.SessionId}")
                { Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("X-Frost-Leader", leader);
                req.Headers.TryAddWithoutValidation("X-Frost-Timestamp", session.LeaderStartTimestamp.ToString());
                req.Headers.TryAddWithoutValidation("X-Frost-Signature", VerifiedXCore.Services.SignatureService.CreateSignature($"{session.SessionId}.{leader}.{session.LeaderStartTimestamp}", key, pub));
                var response = await server.CreateClient().SendAsync(req);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Leader auth failed", body);
                return (response.StatusCode, body);
            }
            finally { FrostSessionStorage.SigningSessions.TryRemove(session.SessionId, out _); }
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
