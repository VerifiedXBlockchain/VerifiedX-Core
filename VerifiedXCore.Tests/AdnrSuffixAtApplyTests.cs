using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A registered name takes the suffix of the block that registered it (".rbx" below V4Height, ".vfx" from it), whatever
    /// the node's tip is while the block is applied: a rebuild replays with the tip unset, live apply with the tip at the block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class AdnrSuffixAtApplyTests : IDisposable
    {
        private const long V4 = 1_000;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorV4Height;

        public AdnrSuffixAtApplyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"adnr_suffix_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            _priorV4Height = Globals.V4Height;
            Globals.CustomPath = _tempRoot;
            Globals.V4Height = V4;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.V4Height = _priorV4Height;
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Transaction Create(string name, string from) => new Transaction
        {
            Hash = Guid.NewGuid().ToString("N"), FromAddress = from, ToAddress = "Adnr_Base", Amount = 5M, Fee = 0M, Nonce = 0,
            TransactionType = TransactionType.ADNR, Timestamp = 1_700_000_000, Signature = "sig",
            Data = JsonConvert.SerializeObject(new { Function = "AdnrCreate()", Name = name }),
        };

        private static async Task Apply(long height, Transaction tx)
        {
            tx.Height = height;
            Assert.True(await StateData.UpdateTreis(new Block { Height = height, StateRoot = "root", Timestamp = 1_700_000_000, Transactions = new List<Transaction> { tx } }));
        }

        private static string? StoredName(string from) => Adnr.GetAdnr()!.FindOne(x => x.Address == from)?.Name;

        [Theory]
        [InlineData(-1)]   // a rebuild: ResetTreis replays before the tip is set
        [InlineData(V4 + 5)] // live apply: the tip is the block being applied
        public async Task ANameRegisteredFromV4Height_IsStoredAsVfx_WhateverTheTip(long tip)
        {
            Globals.LastBlock = new Block { Height = tip };

            await Apply(V4 + 5, Create("alice", "xAlice"));

            Assert.Equal("alice.vfx", StoredName("xAlice"));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(V4 - 5)]
        [InlineData(V4 + 500)] // a tip past V4Height does not move an earlier block's name
        public async Task ANameRegisteredBelowV4Height_IsStoredAsRbx_WhateverTheTip(long tip)
        {
            Globals.LastBlock = new Block { Height = tip };

            await Apply(V4 - 5, Create("bob", "xBob"));

            Assert.Equal("bob.rbx", StoredName("xBob"));
        }

        [Fact]
        public async Task AfterARebuild_ASecondRegistrationOfTheSameName_IsStillRefused()
        {
            // Replayed with the tip unset, as ResetTreis does, then judged at the next block as every synced node judges it.
            Globals.LastBlock = new Block { Height = -1 };
            await Apply(V4 + 5, Create("alice", "xAlice"));
            Globals.LastBlock = new Block { Height = V4 + 5 };

            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var from = AccountData.GetHumanAddress(pub);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = from, Balance = 100M, Nonce = 0 });
            var second = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = from, ToAddress = "Adnr_Base", Amount = 5.0M, Nonce = 0,
                TransactionType = TransactionType.ADNR, Data = JsonConvert.SerializeObject(new { Function = "AdnrCreate()", Name = "alice" }),
            };
            second.Fee = FeeCalcService.CalculateTXFee(second);
            second.Build();
            second.Signature = SignatureService.CreateSignature(second.Hash, key, pub);

            var (ok, why) = await TransactionValidatorService.VerifyTX(second, false, true, false, null, false, V4 + 6);

            Assert.False(ok);
            Assert.Contains("already been taken", why);
        }

        [Fact]
        public async Task FromTheRule_ALabelHeldByItsLegacyRbxName_CannotBeRegisteredAgain()
        {
            var prior = Globals.LegacyNameReservedHeight;
            try
            {
                Globals.LastBlock = new Block { Height = V4 - 5 };
                await Apply(V4 - 5, Create("legacy", "xOldOwner")); // stored "legacy.rbx"
                Globals.LastBlock = new Block { Height = V4 + 100 };
                Globals.LegacyNameReservedHeight = V4 + 101;

                var key = new PrivateKey("secp256k1");
                var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
                var from = AccountData.GetHumanAddress(pub);
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = from, Balance = 100M, Nonce = 0 });
                var claim = new Transaction
                {
                    Timestamp = TimeUtil.GetTime(), FromAddress = from, ToAddress = "Adnr_Base", Amount = 5.0M, Nonce = 0,
                    TransactionType = TransactionType.ADNR, Data = JsonConvert.SerializeObject(new { Function = "AdnrCreate()", Name = "legacy" }),
                };
                claim.Fee = FeeCalcService.CalculateTXFee(claim);
                claim.Build();
                claim.Signature = SignatureService.CreateSignature(claim.Hash, key, pub);

                var (atRule, why) = await TransactionValidatorService.VerifyTX(claim, false, true, false, null, false, V4 + 101);
                Assert.False(atRule);
                Assert.Contains("already been taken", why);
                var (belowRule, belowWhy) = await TransactionValidatorService.VerifyTX(claim, false, true, false, null, false, V4 + 100);
                Assert.True(belowRule, belowWhy); // as the previous release judged it
            }
            finally { Globals.LegacyNameReservedHeight = prior; }
        }
    }
}
