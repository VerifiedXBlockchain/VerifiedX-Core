using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// NEW-06 (found by the independent review; not in the audit; same end state as VX-02): TokenDeploy()'s "already
    /// deployed" check reads committed state only, and nothing de-duplicated creations within a block. With A's
    /// TokenDeploy(U, 1,000) pending, attacker B submits TokenDeploy(U) with a body naming U and minter B and supply
    /// int.MaxValue — both pass VerifyTX (the VX-02 binding holds for each). In one block, B was credited 2.1 billion U
    /// while U's record showed owner A and supply 1,000.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW06_DuplicateContractCreationTests : IDisposable
    {
        private const string U = "5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e:1790400000";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _a = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _b = NewKey();

        public NEW06_DuplicateContractCreationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new06_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };
            foreach (var k in new[] { _a, _b })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey, string, string) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static Transaction Deploy((PrivateKey Key, string Pub, string Address) signer, long supply, long nonce = 0)
        {
            var body = VbtcTestContracts.TokenContractData(U, signer.Address, supply);
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = signer.Address, Amount = 0.0M, Fee = 0, Nonce = nonce,
                TransactionType = TransactionType.FTKN_MINT,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "TokenDeploy()", ContractUID = U, Data = body, MD5List = "NA" } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        [Fact]
        public async Task NEW06_Precondition_EachDeployAlonePassesVerifyTX()
        {
            // Why a block-level rule is needed: each transaction on its own is valid.
            Assert.True((await TransactionValidatorService.VerifyTX(Deploy(_a, 1000))).Item1);
            Assert.True((await TransactionValidatorService.VerifyTX(Deploy(_b, int.MaxValue))).Item1);
        }

        [Fact]
        public void NEW06_PoC_SecondCreationOfTheSameUidInABlock_Refused()
        {
            var seen = new HashSet<string>();
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(Deploy(_a, 1000), seen));
            var second = LedgerIntegrityRules.RegisterCreationInBlock(Deploy(_b, int.MaxValue), seen);
            Assert.NotNull(second);
            Assert.Contains("Duplicate creation", second);
        }

        [Fact]
        public void NEW06_BlockProposal_KeepsTheFirst_DropsTheDuplicateAndThatSendersLaterTxs()
        {
            var aDeploy = Deploy(_a, 1000);
            var bDeploy = Deploy(_b, int.MaxValue, nonce: 0);
            var bLater = new Transaction { FromAddress = _b.Address, Nonce = 1, Hash = "b-later", TransactionType = TransactionType.TX, Data = null };
            var other = new Transaction { FromAddress = "xOTHER", Nonce = 0, Hash = "other", TransactionType = TransactionType.TX, Data = null };

            var kept = TransactionData.DropDuplicateContractCreations(new List<Transaction> { aDeploy, bDeploy, bLater, other });

            Assert.Equal(new[] { aDeploy.Hash, "other" }, kept.Select(t => t.Hash).ToArray());
        }

        [Fact]
        public void NEW06_Control_DifferentUids_BothKept()
        {
            var seen = new HashSet<string>();
            var first = Deploy(_a, 1000);
            var other = new Transaction
            {
                TransactionType = TransactionType.FTKN_MINT,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "TokenDeploy()", ContractUID = "another-uid:1", Data = "x" } }),
            };
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(first, seen));
            Assert.Null(LedgerIntegrityRules.RegisterCreationInBlock(other, seen));
        }
    }
}
