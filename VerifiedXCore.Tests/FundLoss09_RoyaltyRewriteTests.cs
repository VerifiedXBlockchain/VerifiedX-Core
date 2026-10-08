using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 9 (Globals.RoyaltyRewriteRulesHeight): a contract's Royalty feature cannot be changed by
    /// a rewriting function. The original minter could Evolve() a sold NFT into a 99.99% royalty to itself whenever it
    /// was unlocked, and the next sale paid it. Below the height the rewrite still passes (the hole, for replay).
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss09_RoyaltyRewriteTests : IDisposable
    {
        private const long Gate = 1000;
        private const string Refused = "A contract's Royalty feature cannot be changed after creation";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly (PrivateKey Key, string Pub, string Address) _minter;
        private readonly (PrivateKey Key, string Pub, string Address) _owner;

        public FundLoss09_RoyaltyRewriteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"fl09_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.RoyaltyRewriteRulesHeight;
            Globals.RoyaltyRewriteRulesHeight = Gate;
            DbContext.Initialize();
            _minter = NewKey();
            _owner = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _minter.Address, Balance = 1000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.RoyaltyRewriteRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private string Body(string uid, decimal? royalty, string? payTo = null, string description = "fixture")
        {
            List<SmartContractFeatures>? features = null;
            if (royalty != null)
                features = new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.Royalty,
                        FeatureFeatures = JObject.FromObject(new RoyaltyFeature { RoyaltyType = RoyaltyType.Percent, RoyaltyAmount = royalty.Value, RoyaltyPayToAddress = payTo ?? _minter.Address }),
                    },
                };
            return VbtcTestContracts.BuildContractData(uid, _minter.Address, features, description: description);
        }

        /// <summary>An NFT minted by the minter with a 5% royalty, since sold to the owner, unlocked.</summary>
        private (string Uid, string Body) SoldNft()
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            var body = Body(uid, 0.05M);
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = body, MinterAddress = _minter.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            return (uid, body);
        }

        private Transaction Rewrite(string function, string scUid, string body, (PrivateKey Key, string Pub, string Address) sender, string to)
        {
            var tx = new Transaction
            {
                FromAddress = sender.Address, ToAddress = to, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = scUid, Data = body, MD5List = "NA", ToAddress = to } }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, sender.Key, sender.Pub);
            return tx;
        }

        private static void TipBelowGate() => Globals.LastBlock = new Block { Height = Gate - 2 };
        private static void TipAtGate() => Globals.LastBlock = new Block { Height = Gate - 1 };

        [Fact]
        public async Task MinterEvolvesSoldNftTo9999PercentRoyalty_PassesBelowGate_RefusedAtGate()
        {
            var (uid, _) = SoldNft();
            var greedy = Body(uid, 0.9999M, description: "evolved");

            TipBelowGate();
            var (okBelow, msgBelow) = await TransactionValidatorService.VerifyTX(Rewrite("Evolve()", uid, greedy, _minter, _owner.Address));
            Assert.True(okBelow, msgBelow); // the hole: Evolve checks only not-locked, minter and owner

            TipAtGate();
            var (okAt, msgAt) = await TransactionValidatorService.VerifyTX(Rewrite("Evolve()", uid, greedy, _minter, _owner.Address));
            Assert.False(okAt);
            Assert.StartsWith(Refused, msgAt);
        }

        [Fact]
        public async Task OwnerUpdateChangingTheRoyaltyPayee_RefusedAtGate()
        {
            var (uid, _) = SoldNft();
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Rewrite("Update()", uid, Body(uid, 0.05M, payTo: _owner.Address), _owner, _owner.Address));
            Assert.False(ok);
            Assert.StartsWith(Refused, msg);
        }

        [Fact]
        public async Task RemovingTheRoyalty_RefusedAtGate()
        {
            var (uid, _) = SoldNft();
            TipAtGate();
            var (ok, msg) = await TransactionValidatorService.VerifyTX(Rewrite("Update()", uid, Body(uid, null), _owner, _owner.Address));
            Assert.False(ok);
            Assert.StartsWith(Refused, msg);
        }

        [Fact]
        public async Task RewritesKeepingTheRoyalty_PassAtGate()
        {
            var (uid, body) = SoldNft();
            TipAtGate();
            // Same royalty, different description (what an honest evolve changes: anything but the royalty).
            var (okEvolve, msgEvolve) = await TransactionValidatorService.VerifyTX(Rewrite("Evolve()", uid, Body(uid, 0.05M, description: "stage 2"), _minter, _owner.Address));
            Assert.True(okEvolve, msgEvolve);
            // The stored body resent unchanged.
            var (okUpdate, msgUpdate) = await TransactionValidatorService.VerifyTX(Rewrite("Update()", uid, body, _owner, _owner.Address));
            Assert.True(okUpdate, msgUpdate);
        }

        [Fact]
        public void Rule_IsPureAndInertBelowGate()
        {
            var (uid, _) = SoldNft();
            var greedy = Rewrite("Evolve()", uid, Body(uid, 0.9999M), _minter, _owner.Address);
            Assert.Null(LedgerIntegrityRules.RoyaltyUnchangedByRewrite(greedy, Gate - 1));
            Assert.NotNull(LedgerIntegrityRules.RoyaltyUnchangedByRewrite(greedy, Gate));
            Assert.Null(LedgerIntegrityRules.RoyaltyUnchangedByRewrite(Rewrite("Evolve()", "never:1", Body("never:1", 0.9999M), _minter, _owner.Address), Gate)); // missing: the function's own rule

            Assert.Equal((0.05M, _minter.Address), LedgerIntegrityRules.RoyaltyOf(Body(uid, 0.05M)));
            Assert.Null(LedgerIntegrityRules.RoyaltyOf(Body(uid, null)));
            Assert.Null(LedgerIntegrityRules.RoyaltyOf("not a body"));
        }
    }
}
