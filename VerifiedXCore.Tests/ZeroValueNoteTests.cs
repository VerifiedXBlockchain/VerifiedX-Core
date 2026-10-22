using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore.BrowserWalletServices;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A zero-value VFX note (the change output the epoch circuit always has, so every consolidation leaves one) stays
    /// in the wallet row but is never counted as a note or merged by a consolidation.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ZeroValueNoteTests : IDisposable
    {
        private const string AccountKey = "0c28fca386c7a227600b2fe50b7cae11ec86d3bf1fbe471be89827e19d72aa1d";
        private const string Password = "test-password";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string _zfx;

        public ZeroValueNoteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"zero_value_note_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            _zfx = ShieldedWalletService.UpsertFromAccountKey(AccountKey, "xZeroValueNoteSource", Password).ZfxAddress;
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void WalletNotes(params decimal[] amounts)
        {
            var w = ShieldedWalletService.FindByZfxAddress(_zfx)!;
            w.UnspentCommitments = amounts.Select((a, i) =>
            {
                Assert.True(PrivacyPedersenAmount.TryCommitAmount(a, out var r32, out var g1, out var err), err);
                return new UnspentCommitment { Commitment = Convert.ToBase64String(g1), AssetType = "VFX", Amount = a, Randomness = r32, TreePosition = i, BlockHeight = 10 };
            }).ToList();
            w.ShieldedBalances = new Dictionary<string, decimal> { ["VFX"] = amounts.Sum() };
            ShieldedWalletService.Upsert(w, notesAsGiven: true);
        }

        private static JObject Json(string s) => JObject.Parse(s);

        private Task<string> Consolidate() =>
            new PrivacyV1Controller().ConsolidateShieldedVFX(new ConsolidateShieldedVfxRequest { ZfxAddress = _zfx, WalletPassword = Password });

        [Fact]
        public async Task TheBalanceAfterAConsolidation_CountsOneNote_AndKeepsTheZeroNoteStored()
        {
            WalletNotes(1.499997M, 0M);

            var result = Json(await new PrivacyV1Controller().GetShieldedBalance(_zfx, includeCommitments: true))["Result"]!;
            Assert.Equal(1, result["UnspentCommitments"]!.Value<int>());
            Assert.Equal(1.499997M, result["UnspentSum"]!.Value<decimal>());
            Assert.Single(result["Commitments"]!);

            var browser = JObject.FromObject(WalletPrivacyVfxService.GetShieldedBalance(_zfx));
            Assert.Equal(1, browser["unspentNotes"]!.Value<int>());
            Assert.Equal(1.499997M, browser["vfxShieldedBalance"]!.Value<decimal>());

            Assert.Equal(2, ShieldedWalletService.FindByZfxAddress(_zfx)!.UnspentCommitments.Count);
        }

        [Fact]
        public async Task OneRealNoteAndAZeroNote_AreNotOfferedForConsolidation()
        {
            WalletNotes(1.499997M, 0M);

            var j = Json(await Consolidate());

            Assert.False(j["Success"]!.Value<bool>());
            Assert.Equal("Need at least two unspent VFX notes to consolidate.", j["Message"]!.Value<string>());
            Assert.All(ShieldedWalletService.FindByZfxAddress(_zfx)!.UnspentCommitments, n => Assert.False(n.IsSpent));
        }

        [Fact]
        public async Task TwoRealNotes_AreCountedAndConsolidated()
        {
            WalletNotes(0.5M, 1.0M);

            var balance = Json(await new PrivacyV1Controller().GetShieldedBalance(_zfx))["Result"]!;
            Assert.Equal(2, balance["UnspentCommitments"]!.Value<int>());

            var j = Json(await Consolidate());
            Assert.NotEqual("Need at least two unspent VFX notes to consolidate.", j["Message"]?.Value<string>());
        }

        [Fact]
        public async Task TwoRealNotesBesideAZeroNote_AreTheOnesMerged()
        {
            // Merged with the zero note, the dust note alone would not cover the fee.
            WalletNotes(0M, Globals.PrivateTxFixedFee / 2, 1.0M);

            var balance = Json(await new PrivacyV1Controller().GetShieldedBalance(_zfx))["Result"]!;
            Assert.Equal(2, balance["UnspentCommitments"]!.Value<int>());

            var j = Json(await Consolidate());
            Assert.NotEqual("Need at least two unspent VFX notes to consolidate.", j["Message"]?.Value<string>());
            Assert.NotEqual("Combined VFX amount must exceed the fixed shielded fee.", j["Message"]?.Value<string>());
        }
    }
}
