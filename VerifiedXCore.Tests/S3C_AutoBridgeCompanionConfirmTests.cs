using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-26 (follow-up, Greptile P1): S3C AutoBridge took the public companion's deposit address from the create response
    /// - before the companion's creation was mined - and immediately withdrew BTC from the S3C contract to it. If the
    /// creation never made it on chain, that BTC had no contract and no withdrawal path. The same held for a reused
    /// companion found only in local records. AutoBridge now waits until the companion is in chain state and takes the
    /// address from the confirmation-gated GetMPCDepositAddress; it gives up before any withdrawal if it never confirms.
    /// </summary>
    [Collection("DbContextSequential")]
    public class S3C_AutoBridgeCompanionConfirmTests : IDisposable
    {
        private const string Companion = "8c8c8c8c8c8c8c8c8c8c8c8c8c8c8c8c:1790800000";
        private const string Deposit = "bc1pcompanionvaultaddress00000000000000000000000000000000000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public S3C_AutoBridgeCompanionConfirmTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"s3cab_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            // What the wallet holds right after CreateVBTCContract: a local record with the deposit address.
            VBTCContractV2.SaveContract(new VBTCContractV2 { SmartContractUID = Companion, OwnerAddress = "xRequester", DepositAddress = Deposit, LinkedContractUID = "s3c:1" });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static void Confirm() => SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
        {
            SmartContractUID = Companion, ContractData = "x", MinterAddress = "xRequester", OwnerAddress = "xRequester",
        });

        [Fact]
        public async Task CompanionOnlyInLocalRecords_NoDepositAddress()
        {
            // The creation was admitted but never mined: no address, so AutoBridge withdraws nothing.
            Assert.Null(await S3CAutoBridgeService.WaitForCompanionOnChain(Companion, 0, 1));
        }

        [Fact]
        public async Task CompanionOnChain_DepositAddressReturned()
        {
            Confirm();
            Assert.Equal(Deposit, await S3CAutoBridgeService.WaitForCompanionOnChain(Companion, 0, 1));
        }

        [Fact]
        public async Task CompanionConfirmedWhileWaiting_DepositAddressReturned()
        {
            var wait = S3CAutoBridgeService.WaitForCompanionOnChain(Companion, 20, 1);
            await Task.Delay(1500);
            Confirm();
            Assert.Equal(Deposit, await wait);
        }

        // ── Retries (Greptile follow-ups): on chain / pending / dead by time; never deleted ──

        private static void SetCreated(string uid, long? createdAt)
        {
            var rec = VBTCContractV2.GetContract(uid)!;
            rec.CreateTxTimestamp = createdAt;
            rec.CreateTxHash = createdAt.HasValue ? "createtx-" + uid : null;
            VBTCContractV2.UpdateContract(rec);
        }

        [Fact]
        public void PendingCompanion_IsReused_NoDuplicate()
        {
            // A retry while the creation is still pending must wait for it, not create another companion.
            SetCreated(Companion, TimeUtil.GetTime() - 100);
            Assert.Equal(Companion, S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1")?.SmartContractUID);
        }

        [Fact]
        public void DeadCompanion_IsIgnored_SoARetryCreatesANewOne()
        {
            // Older than the transaction age limit: no honest node will mine its creation.
            SetCreated(Companion, TimeUtil.GetTime() - Globals.MaxTxAgeSeconds - Globals.MaxFutureSkewSeconds - 10);
            Assert.Null(S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1"));
            Assert.NotNull(VBTCContractV2.GetContract(Companion));                    // the record is kept
        }

        [Fact]
        public void CompanionConfirmedThroughAPeer_IsFoundEvenAfterItsDeadline()
        {
            // This node dropped the creation, a peer mined it: chain state decides, and the record was never deleted.
            SetCreated(Companion, TimeUtil.GetTime() - 10 * 3600);
            Confirm();
            Assert.Equal(Companion, S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1")?.SmartContractUID);
        }

        [Fact]
        public void OnChainCompanion_PreferredOverAPendingOne()
        {
            const string second = "9d9d9d9d9d9d9d9d9d9d9d9d9d9d9d9d:1790800100";
            SetCreated(Companion, TimeUtil.GetTime() - 100);                              // pending
            VBTCContractV2.SaveContract(new VBTCContractV2 { SmartContractUID = second, OwnerAddress = "xRequester", DepositAddress = "bc1psecond", LinkedContractUID = "s3c:1", CreateTxTimestamp = TimeUtil.GetTime() - 100 });
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei { SmartContractUID = second, ContractData = "x", MinterAddress = "xRequester", OwnerAddress = "xRequester" });
            Assert.Equal(second, S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1")?.SmartContractUID);
        }

        [Fact]
        public void RecordWithoutAStoredTimestamp_IsJudgedFromTheCeremonyStartInItsUid()
        {
            var now = TimeUtil.GetTime();
            var old = new VBTCContractV2 { SmartContractUID = $"aa:{now - 5 * 3600}" };      // 5 h ago: past 2 h + 1 h + skew
            var recent = new VBTCContractV2 { SmartContractUID = $"bb:{now - 3600}" };       // 1 h ago: may still be mined
            Assert.Equal(S3CAutoBridgeService.CompanionState.Dead, S3CAutoBridgeService.StateOf(old, now));
            Assert.Equal(S3CAutoBridgeService.CompanionState.Pending, S3CAutoBridgeService.StateOf(recent, now));
        }

        [Fact]
        public void CompanionRecordsAreNeverDeleted_AndEveryCreationPathRecordsItsTransaction()
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var service = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Services", "S3CAutoBridgeService.cs"));
            Assert.DoesNotContain("DeleteContract(", service);
            Assert.DoesNotContain("DeleteSmartContract(", service);
            var controller = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Controllers", "VBTCController.cs"));
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(controller, @"RecordCreationTx\(scUID, ").Count);
        }

        [Fact]
        public void TheWithdrawalWaitsForTheConfirmedCompanion()
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(ThisFile()))!;
            var src = File.ReadAllText(Path.Combine(root, "VerifiedXCore", "Bitcoin", "Services", "S3CAutoBridgeService.cs"));
            var wait = src.IndexOf("await WaitForCompanionOnChain(s.PublicScUID!", StringComparison.Ordinal);
            var assign = src.IndexOf("s.PublicDepositAddress = confirmedDeposit;", StringComparison.Ordinal);
            var withdraw = src.IndexOf("await VBTCService.RequestWithdrawal(", StringComparison.Ordinal);
            Assert.True(wait > 0 && assign > wait && withdraw > assign, "the S3C withdrawal must come after the companion is confirmed on chain");
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
