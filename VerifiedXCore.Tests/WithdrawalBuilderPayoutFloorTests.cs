using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Attestation;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The withdrawal request builders check a share's payout against the vault's own floor: 546 sats for a vault the chain
    /// watches (what BtcWithdrawals.Refusal refuses below), 330 for a legacy vault (what its send path needs). They used
    /// 330 for every vault, so for a watched vault they built a request whose payout of 330 to 545 sats the vault's own
    /// rule then refused. A share of 900 sats at 3 sat/vB pays 462 sats in fees and leaves 438.
    /// </summary>
    [Collection("DbContextSequential")]
    public class WithdrawalBuilderPayoutFloorTests : IDisposable
    {
        private const string Watched = "watched0000000000000000000000000:1792851413";
        private const string Legacy = "legacy00000000000000000000000000:1792851414";
        private const decimal Share = 0.000009M;
        private const int FeeRate = 3;

        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _tempRoot;

        public WithdrawalBuilderPayoutFloorTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"payout_floor_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            // The fee floor is in force (VbtcWithdrawalConcurrencyHeight).
            Globals.LastBlock = new Block { Height = Globals.VbtcWithdrawalConcurrencyHeight };
            BtcAttestStore.Db.GetCollection<VaultDepositScriptRow>(BtcAttestStore.Scripts).Insert(new VaultDepositScriptRow
            {
                ScriptHex = "5120" + new string('a', 64), SmartContractUID = Watched, DepositAddress = "watched-deposit", Generation = 0,
            });
        }

        public void Dispose()
        {
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Task<(bool Ok, string Error)> Preflight(string scUID) =>
            VBTCService.ValidateWithdrawalAllocations("a-requester",
                new List<VBTCV2MultiWithdrawalInput> { new() { SCUID = scUID, Amount = Share } }, Share, FeeRate);

        [Fact]
        public async Task AWatchedVaultShare_LeavingLessThan546Sats_IsRefusedByTheBuilder()
        {
            Assert.True(BtcAttestBalance.IsAttestedVault(Watched));

            var (ok, error) = await Preflight(Watched);

            Assert.False(ok);
            Assert.Contains($"the payout must be at least {BtcCanonicalWithdrawal.DustSats} sats", error);
        }

        [Fact]
        public async Task ALegacyVaultShare_LeavingMoreThan330Sats_PassesTheFloor()
        {
            Assert.False(BtcAttestBalance.IsAttestedVault(Legacy));

            var (ok, error) = await Preflight(Legacy);

            // It goes on to the state checks (this test holds no contract state), past the fee floor.
            Assert.False(ok);
            Assert.DoesNotContain("cannot pay a Bitcoin withdrawal", error);
        }

        [Fact]
        public void TheFloorFollowsTheVault_AndTheConsensusRuleKeepsItsOwn()
        {
            Assert.Equal(BtcCanonicalWithdrawal.DustSats, VBTCService.MinWithdrawalPayoutSats(Watched));
            Assert.Equal(VBTCService.TaprootDustSats, VBTCService.MinWithdrawalPayoutSats(Legacy));
            // Without a floor named, the shared check is the consensus rule's, unchanged.
            Assert.Null(VBTCService.GetWithdrawalFeeFloorError(Share, FeeRate, "consensus"));
        }
    }
}
