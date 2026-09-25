using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
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

        // ── Retries (Greptile follow-up): an unconfirmed companion must not block later attempts ──

        [Fact]
        public void UnconfirmedCompanionIsNotRediscovered_AConfirmedOneIs()
        {
            Assert.Null(S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1"));      // local record only: a retry creates a new one
            Confirm();
            Assert.Equal(Companion, S3CAutoBridgeService.DiscoverCompanion("xRequester", "s3c:1")?.SmartContractUID);
        }

        [Fact]
        public void CompanionWhoseCreationIsGone_IsForgotten()
        {
            Assert.True(S3CAutoBridgeService.ForgetUnconfirmedCompanion(Companion, "createtx1"));  // not on chain, not pending
            Assert.Null(VBTCContractV2.GetContract(Companion));
        }

        [Fact]
        public void CompanionWhoseCreationIsStillPending_IsKept()
        {
            TransactionData.GetPool().InsertSafe(new Transaction { Hash = "createtx1", FromAddress = "xRequester", ToAddress = "xRequester", Data = "x" });
            Assert.False(S3CAutoBridgeService.ForgetUnconfirmedCompanion(Companion, "createtx1"));
            Assert.NotNull(VBTCContractV2.GetContract(Companion));
        }

        [Fact]
        public void ConfirmedCompanion_IsNeverForgotten()
        {
            Confirm();
            Assert.False(S3CAutoBridgeService.ForgetUnconfirmedCompanion(Companion, "createtx1"));
            Assert.NotNull(VBTCContractV2.GetContract(Companion));
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
