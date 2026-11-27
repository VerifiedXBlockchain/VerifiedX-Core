using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;
using Xunit;
using static VerifiedXCore.Bitcoin.Services.UserBridgeMintService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A bridge mint ends minted or able to be tried again: a lock is minted once Base holds the mint in a block, a
    /// mint Base refuses before it is sent has its signatures collected again, a send whose answer was lost is neither
    /// failed nor sent twice, and one mint of a lock runs at a time.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BridgeMintRecoveryTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly int _priorPolls = MintReceiptPolls, _priorPollMs = MintReceiptPollMs, _priorRetryMs = MintRetryDelayMs;

        public BridgeMintRecoveryTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"bridge_mint_recovery_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MintReceiptPolls = 3;
            MintReceiptPollMs = 1;
            MintRetryDelayMs = 1;
        }

        public void Dispose()
        {
            SendMintForTests = null;
            MintReceiptForTests = null;
            LockMintedOnBaseForTests = null;
            MintReceiptPolls = _priorPolls;
            MintReceiptPollMs = _priorPollMs;
            MintRetryDelayMs = _priorRetryMs;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static BridgeLockRecord NewLock(BridgeLockStatus status = BridgeLockStatus.AttestationReady, string? baseTxHash = null)
        {
            var record = new BridgeLockRecord
            {
                LockId = "lock-" + Guid.NewGuid().ToString("N"), OwnerAddress = "xOwner", SmartContractUID = "vault:1", Amount = 0.001M, AmountSats = 100_000,
                EvmDestination = "0x00000000000000000000000000000000000000aa", VfxLockBlockHeight = 100, MintNonce = 100, Status = status,
                ValidatorSignatures = new Dictionary<string, string> { ["0xsigner"] = "0xsig" }, BaseTxHash = baseTxHash ?? "",
            };
            BridgeLockRecord.Save(record);
            return record;
        }

        /// <summary>Base's node answering that it does not take the mint.</summary>
        private static Exception Refusal(string why) => new Nethereum.JsonRpc.Client.RpcResponseException(new Nethereum.JsonRpc.Client.RpcError(3, "execution reverted: " + why));

        private static BridgeLockRecord Stored(BridgeLockRecord record) => BridgeLockRecord.GetByLockId(record.LockId)!;

        // ── A lock is minted once Base holds the mint in a block ────────────────────────────────────────────

        [Fact]
        public async Task AMintBaseTookButNeverPutInABlock_IsNotMinted_AndCanBeSubmittedAgain()
        {
            var record = NewLock();
            SendMintForTests = _ => Task.FromResult("0xsent");
            MintReceiptForTests = _ => Task.FromResult<bool?>(null); // Base never has a receipt for it

            var (outcome, _) = await SubmitMintWithProofAsUser(record, "xOwner", "0xbase");

            Assert.Equal(MintSend.Sent, outcome);
            var stored = Stored(record);
            Assert.Equal(BridgeLockStatus.ProofSubmitted, stored.Status);
            Assert.Equal("0xsent", stored.BaseTxHash);
            Assert.Null(stored.FinalizedAtUtc);

            // The owner's retry is not turned away as already minted.
            var (_, message) = await RetryMintForLock(record.LockId, "xOwner");
            Assert.DoesNotContain("already been minted", message);
        }

        [Fact]
        public async Task AMintBaseHoldsInABlock_IsMinted_AlsoWhenTheBlockCameAfterTheWait()
        {
            var confirmed = NewLock();
            SendMintForTests = _ => Task.FromResult("0xconfirmed");
            var asked = 0;
            MintReceiptForTests = _ => Task.FromResult<bool?>(++asked < 2 ? null : true); // in a block on the second look
            Assert.Equal(MintSend.Confirmed, (await SubmitMintWithProofAsUser(confirmed, "xOwner", "0xbase")).Outcome);
            Assert.Equal(BridgeLockStatus.Minted, Stored(confirmed).Status);
            Assert.True(Stored(confirmed).FinalizedAtUtc > 0);

            // Sent, in no block within the wait; in one by the time the owner retries.
            var late = NewLock();
            SendMintForTests = _ => Task.FromResult("0xlate");
            MintReceiptForTests = _ => Task.FromResult<bool?>(null);
            await SubmitMintWithProofAsUser(late, "xOwner", "0xbase");
            MintReceiptForTests = _ => Task.FromResult<bool?>(true);

            var (ok, message) = await RetryMintForLock(late.LockId, "xOwner");
            Assert.False(ok);
            Assert.Contains("already been minted", message);
            Assert.Equal(BridgeLockStatus.Minted, Stored(late).Status);
        }

        [Fact]
        public async Task ALockAnEarlierBuildMarkedMintedOnTheHashAlone_CanBeRetriedWhenBaseHasNoBlockForIt()
        {
            var stuck = NewLock(BridgeLockStatus.MintedOnBase, "0xnever-included");
            MintReceiptForTests = _ => Task.FromResult<bool?>(null);
            Assert.DoesNotContain("already been minted", (await RetryMintForLock(stuck.LockId, "xOwner")).Message);

            var minted = NewLock(BridgeLockStatus.MintedOnBase, "0xincluded");
            MintReceiptForTests = _ => Task.FromResult<bool?>(true);
            Assert.Contains("already been minted", (await RetryMintForLock(minted.LockId, "xOwner")).Message);
            Assert.Equal(BridgeLockStatus.Minted, Stored(minted).Status);
        }

        [Fact]
        public async Task AMintThatRevertedInABlock_IsFailed()
        {
            var record = NewLock();
            SendMintForTests = _ => Task.FromResult("0xreverted");
            MintReceiptForTests = _ => Task.FromResult<bool?>(false);

            Assert.Equal(MintSend.Reverted, (await SubmitMintWithProofAsUser(record, "xOwner", "0xbase")).Outcome);
            Assert.Equal(BridgeLockStatus.Failed, Stored(record).Status);
        }

        // ── A mint Base refuses before it is sent ───────────────────────────────────────────────────────────

        [Fact]
        public async Task AMintBaseRefuses_HasItsSignaturesCollectedAgain_AndIsMintedWithTheNewOnes()
        {
            var record = NewLock(BridgeLockStatus.Locked);
            var collected = 0;
            var sentWith = new List<string>();
            SendMintForTests = r =>
            {
                var set = string.Join(",", r.ValidatorSignatures!.Keys);
                sentWith.Add(set);
                // The contract's signer lists changed after the first signatures were collected: it refuses them.
                return set == "old-signer" ? throw Refusal("Insufficient valid signatures") : Task.FromResult("0xminted");
            };
            MintReceiptForTests = _ => Task.FromResult<bool?>(true);

            await CollectAndMint(record, "",
                () => Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { [++collected == 1 ? "old-signer" : "new-signer"] = "0xsig" }, 1)),
                () => SubmitMintWithProofAsUser(record, "xOwner", "0xbase"));

            Assert.Equal(2, collected);
            Assert.Equal(new[] { "old-signer", "new-signer" }, sentWith);
            Assert.Equal(BridgeLockStatus.Minted, Stored(record).Status);
        }

        [Fact]
        public async Task AMintBaseKeepsRefusing_IsFailedOnlyAfterTheLastAttempt_AndOneWithTooFewSignaturesWaits()
        {
            var refused = NewLock(BridgeLockStatus.Locked);
            var collected = 0;
            SendMintForTests = _ => throw Refusal("Insufficient valid signatures");
            await CollectAndMint(refused, "",
                () => { collected++; return Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { ["s"] = "0xsig" }, 1)); },
                () => SubmitMintWithProofAsUser(refused, "xOwner", "0xbase"));
            Assert.Equal(MintAttempts, collected);
            Assert.Equal(BridgeLockStatus.Failed, Stored(refused).Status);
            Assert.Contains("Insufficient valid signatures", Stored(refused).ErrorMessage);

            // Too few signatures: nothing is submitted and the lock stays to be collected for.
            var shortOf = NewLock(BridgeLockStatus.Locked);
            var submitted = 0;
            await CollectAndMint(shortOf, "",
                () => Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { ["s"] = "0xsig" }, 2)),
                () => { submitted++; return Task.FromResult<(MintSend, string?)>((MintSend.Confirmed, null)); });
            Assert.Equal(0, submitted);
            Assert.Equal(BridgeLockStatus.AttestationPending, Stored(shortOf).Status);
        }

        [Fact]
        public async Task AMintRefusedOrRevertedBecauseTheLockWasMintedAlready_IsMinted_NotFailed()
        {
            // An earlier submission of the lock went through; this one is refused at the estimate.
            var refused = NewLock(BridgeLockStatus.Locked);
            var collected = 0;
            SendMintForTests = _ => throw Refusal("LockId already used");
            LockMintedOnBaseForTests = _ => Task.FromResult<bool?>(true);
            await CollectAndMint(refused, "",
                () => { collected++; return Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { ["s"] = "0xsig" }, 1)); },
                () => SubmitMintWithProofAsUser(refused, "xOwner", "0xbase"));
            Assert.Equal(1, collected); // no signatures are collected for a lock that is minted
            Assert.Equal(BridgeLockStatus.Minted, Stored(refused).Status);

            // Two submissions raced: this one is in a block, reverted, because the other was there first.
            var reverted = NewLock();
            SendMintForTests = _ => Task.FromResult("0xsecond");
            MintReceiptForTests = _ => Task.FromResult<bool?>(false);
            Assert.Equal(MintSend.Confirmed, (await SubmitMintWithProofAsUser(reverted, "xOwner", "0xbase")).Outcome);
            Assert.Equal(BridgeLockStatus.Minted, Stored(reverted).Status);

            // And a lock this node recorded as failed, which the contract has minted, is not submitted again.
            var failed = NewLock(BridgeLockStatus.Failed);
            var (ok, message) = await RetryMintForLock(failed.LockId, "xOwner");
            Assert.False(ok);
            Assert.Contains("already been minted", message);
            Assert.Equal(BridgeLockStatus.Minted, Stored(failed).Status);
        }

        [Fact]
        public async Task ASendWhoseAnswerWasLost_IsNeitherFailedNorSentAgain()
        {
            // The connection broke after the transaction went out: Base's node may have it.
            var record = NewLock(BridgeLockStatus.Locked);
            var collected = 0;
            SendMintForTests = _ => throw new System.Net.Http.HttpRequestException("The response ended prematurely");
            LockMintedOnBaseForTests = _ => Task.FromResult<bool?>(false);

            await CollectAndMint(record, "",
                () => { collected++; return Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { ["s"] = "0xsig" }, 1)); },
                () => SubmitMintWithProofAsUser(record, "xOwner", "0xbase"));

            Assert.Equal(1, collected);
            Assert.Equal(BridgeLockStatus.ProofSubmitted, Stored(record).Status);
            Assert.Contains("may have been sent", Stored(record).ErrorMessage);
            Assert.True(BaseAnsweredNo(Refusal("anything")));
            Assert.False(BaseAnsweredNo(new TimeoutException()));
        }

        [Fact]
        public async Task OneMintOfALockRunsAtATime()
        {
            var record = NewLock(BridgeLockStatus.Locked);
            var firstIsCollecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var letFirstGoOn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = 0;
            SendMintForTests = _ => { sends++; return Task.FromResult("0xonly"); };
            MintReceiptForTests = _ => Task.FromResult<bool?>(true);

            var first = CollectAndMint(record, "",
                async () =>
                {
                    firstIsCollecting.SetResult(true);
                    await letFirstGoOn.Task;
                    return (new Dictionary<string, string> { ["s"] = "0xsig" }, 1);
                },
                () => SubmitMintWithProofAsUser(record, "xOwner", "0xbase"));
            await firstIsCollecting.Task;

            // A second run of the same lock, and the owner's retry, while the first is under way.
            var secondCollected = 0;
            await CollectAndMint(record, "",
                () => { secondCollected++; return Task.FromResult<(Dictionary<string, string>?, int)>((new Dictionary<string, string> { ["s"] = "0xsig" }, 1)); },
                () => SubmitMintWithProofAsUser(record, "xOwner", "0xbase"));
            var (ok, message) = await RetryMintForLock(record.LockId, "xOwner");

            Assert.Equal(0, secondCollected);
            Assert.False(ok);
            Assert.Contains("already under way", message);

            letFirstGoOn.SetResult(true);
            await first;
            Assert.Equal(1, sends);
            Assert.Equal(BridgeLockStatus.Minted, Stored(record).Status);
            Assert.False(MintIsUnderWay(record.LockId));
        }

    }
}
