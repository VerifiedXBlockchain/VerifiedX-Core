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
    /// From Globals.EvolveRulesHeight a minter's Evolve(), Devolve() or ChangeEvolveStateSpecific() applies only to a
    /// contract made with the Evolving feature, and carries a whole body that keeps it: the minter of any NFT could
    /// replace or wipe the body of one it had sold.
    /// </summary>
    [Collection("DbContextSequential")]
    public class EvolveNeedsEvolvingContractTests : IDisposable
    {
        private const long Rule = 5_000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.EvolveRulesHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _minter = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public EvolveNeedsEvolvingContractTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"evolve_rule_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.EvolveRulesHeight = Rule;
            Globals.LastBlock = new Block { Height = Rule - 1 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _minter.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.EvolveRulesHeight = _priorRule;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private string Body(string uid, bool evolving, string description = "fixture")
        {
            var features = evolving
                ? new List<SmartContractFeatures>
                {
                    new SmartContractFeatures
                    {
                        FeatureName = FeatureName.Evolving,
                        FeatureFeatures = JArray.FromObject(new[]
                        {
                            new EvolvingFeature { EvolutionState = 1, Name = "one", Description = "first" },
                            new EvolvingFeature { EvolutionState = 2, Name = "two", Description = "second" },
                        }),
                    },
                }
                : null;
            return VbtcTestContracts.BuildContractData(uid, _minter.Address, features, description: description);
        }

        private string SoldNft(bool evolving)
        {
            var uid = Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = Body(uid, evolving), MinterAddress = _minter.Address, OwnerAddress = _owner.Address, IsLocked = false, Nonce = 0,
            });
            return uid;
        }

        private Transaction Evolve(string uid, string body)
        {
            var tx = new Transaction
            {
                FromAddress = _minter.Address, ToAddress = _owner.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Evolve()", ContractUID = uid, Data = body, MD5List = "NA", ToAddress = _owner.Address } }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _minter.Key, _minter.Pub);
            return tx;
        }

        [Fact]
        public void FromTheRule_TheMinterCannotRewriteANonEvolvingNftItSold_AndBelowItStillCan()
        {
            var uid = SoldNft(evolving: false);
            var replaced = Evolve(uid, Body(uid, evolving: false, description: "replaced"));

            Assert.NotNull(LedgerIntegrityRules.EvolutionNeedsAnEvolvingContract(replaced, Rule));
            Assert.Null(LedgerIntegrityRules.EvolutionNeedsAnEvolvingContract(replaced, Rule - 1));
        }

        [Fact]
        public void FromTheRule_AnEvolutionMustKeepTheEvolvingBody_NotWipeIt()
        {
            var uid = SoldNft(evolving: true);

            Assert.NotNull(LedgerIntegrityRules.EvolutionNeedsAnEvolvingContract(Evolve(uid, ""), Rule));
            Assert.NotNull(LedgerIntegrityRules.EvolutionNeedsAnEvolvingContract(Evolve(uid, Body(uid, evolving: false)), Rule));
            Assert.Null(LedgerIntegrityRules.EvolutionNeedsAnEvolvingContract(Evolve(uid, Body(uid, evolving: true, description: "evolved")), Rule));
        }
    }
}
