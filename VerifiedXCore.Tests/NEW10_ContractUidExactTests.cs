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
    /// NEW-10 (found by the third independent review): contract records are looked up through LiteDB's default
    /// collation, which is culture-aware and ignores case and invisible characters, while in-memory guards (same-block
    /// creation and withdrawal sets, the reserve pending total, single-flight) compared raw strings. A reference such as
    /// "7B7B…" or "7b­7b…" hit the "7b7b…" record but escaped every guard (reviewer PoCs A and D: a reserve holding
    /// 1.0 paid 2.0; two creations of one contract in one block).
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW10_ContractUidExactTests : IDisposable
    {
        private const string V2 = "7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f:1790500010";
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public NEW10_ContractUidExactTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new10_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1001 };
            Globals.WithdrawalEscrowHeight = 1;

            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = V2, ContractData = VbtcTestContracts.VbtcV2ContractData,
                MinterAddress = _owner.Address, OwnerAddress = _owner.Address,
                SCStateTreiTokenizationTXes = new List<SmartContractStateTreiTokenizationTX>
                {
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _holder.Address, Amount = 1.0M },
                },
            });
            foreach (var k in new[] { _owner, _holder, _other })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = k.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.WithdrawalEscrowHeight = _priorEscrowHeight;
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, TransactionType type, object data, long nonce = 0)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = nonce,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private Transaction Transfer(string uid) =>
            Signed(_holder, _other.Address, TransactionType.TKNZ_TX,
                new { Function = "TransferVBTCV2()", ContractUID = uid, FromAddress = _holder.Address, ToAddress = _other.Address, Amount = 0.5M });

        [Fact]
        public void NEW10_Precondition_LookupIgnoresCaseAndInvisibleCharacters()
        {
            Assert.NotNull(SmartContractStateTrei.GetSmartContractState(V2.ToUpperInvariant()));
            Assert.NotNull(SmartContractStateTrei.GetSmartContractState("7f7f­" + V2.Substring(4)));
        }

        [Theory]
        [InlineData("upper")]
        [InlineData("softhyphen")]
        [InlineData("zwj")]
        public async Task NEW10_PoC_AliasedReference_Refused(string kind)
        {
            var uid = kind switch
            {
                "upper" => V2.ToUpperInvariant(),
                "softhyphen" => "7f7f­" + V2.Substring(4),
                _ => "7f7f‍" + V2.Substring(4),
            };
            var (ok, message) = await TransactionValidatorService.VerifyTX(Transfer(uid));
            Assert.False(ok);
            Assert.Contains("does not match the stored contract", message);
        }

        [Fact]
        public async Task NEW10_PoC_AliasedWithdrawalReference_Refused()
        {
            var tx = Signed(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2.ToUpperInvariant(), BTCAddress = BtcDest, Amount = 0.5M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() });
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("does not match the stored contract", message);
        }

        [Fact]
        public async Task NEW10_Control_ExactReference_Accepted()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(Transfer(V2));
            Assert.True(ok, message);
        }

        [Theory]
        [InlineData("ABCDEF0123456789ABCDEF0123456789:1790500011", false)]
        [InlineData("abcdef01­23456789abcdef0123456789:1790500011", false)]
        [InlineData("abc:def", false)]
        [InlineData("abcdef0123456789abcdef0123456789:1790500011", true)]
        public void NEW10_CreationUidFormat(string uid, bool allowed)
        {
            var deploy = new Transaction
            {
                FromAddress = _owner.Address, TransactionType = TransactionType.FTKN_MINT,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "TokenDeploy()", ContractUID = uid, Data = "x" } }),
            };
            var error = LedgerIntegrityRules.ContractUids(deploy);
            Assert.Equal(allowed, error == null);
        }
    }
}
