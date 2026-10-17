using System;
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
    /// From Globals.ReserveNonTransferAmountHeight a reserve's transaction other than a TX carries no Amount (Register()
    /// still pays the registration cost): the apply burned such an amount at once, beyond what CallBack() and Recover()
    /// can return.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ReserveNonTransferAmountTests : IDisposable
    {
        private const long Rule = 5_000;
        private const string Refusal = "carry no amount";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorRule;
        private readonly (PrivateKey Key, string Pub) _vault;
        private readonly string _reserve;

        public ReserveNonTransferAmountTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"rsrv_amount_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorRule = Globals.ReserveNonTransferAmountHeight;
            Globals.ReserveNonTransferAmountHeight = Rule;
            DbContext.Initialize();
            do
            {
                var key = new PrivateKey("secp256k1");
                _vault = (key, "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant());
                _reserve = ReserveAccount.GetHumanAddress(_vault.Pub);
            } while (!_reserve.StartsWith("xRBX", StringComparison.Ordinal));
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _reserve, Balance = 1000M, Nonce = 0, RecoveryAccount = "xRecovery" });
            Globals.LastBlock = new Block { Height = Rule - 1 };
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.ReserveNonTransferAmountHeight = _priorRule;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Signed(TransactionType type, decimal amount, object data, string to = "Reserve_Base")
        {
            var tx = new Transaction
            {
                FromAddress = _reserve, ToAddress = to, Amount = amount, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = type, UnlockTime = TimeUtil.GetTime() + 2 * 86_400,
                Data = JsonConvert.SerializeObject(data),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _vault.Key, _vault.Pub);
            return tx;
        }

        private static async Task<string> Why(Transaction tx, long height) =>
            (await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height)).Item2;

        [Fact]
        public async Task FromTheRule_ACallBackCarryingAnAmount_IsRefused_AndBelowItIsNotRefusedForIt()
        {
            var callBack = Signed(TransactionType.RESERVE, 900M, new { Function = "CallBack()", Hash = new string('a', 64) });

            Assert.Contains(Refusal, await Why(callBack, Rule));
            Assert.DoesNotContain(Refusal, await Why(callBack, Rule - 1));
        }

        [Fact]
        public void TheRule_CoversEveryNonTransferType_AndLeavesTransfersAndRegistrationAlone()
        {
            foreach (var type in new[] { TransactionType.RESERVE, TransactionType.NFT_TX, TransactionType.FTKN_TX, TransactionType.TKNZ_TX, TransactionType.SC_TX, TransactionType.VBTC_V2_TRANSFER })
            {
                Assert.NotNull(LedgerIntegrityRules.ReserveMovesVfxOnlyByTransfer(Signed(type, 1M, new { Function = "Transfer()" }), Rule));
                Assert.Null(LedgerIntegrityRules.ReserveMovesVfxOnlyByTransfer(Signed(type, 0M, new { Function = "Transfer()" }), Rule));
            }
            Assert.Null(LedgerIntegrityRules.ReserveMovesVfxOnlyByTransfer(Signed(TransactionType.TX, 25M, new { }, "xSomeone"), Rule));
            Assert.Null(LedgerIntegrityRules.ReserveMovesVfxOnlyByTransfer(Signed(TransactionType.RESERVE, Globals.RSRVAccountRegisterRBX, new { Function = "Register()", RecoveryAddress = "xRecovery" }), Rule));
            var ordinary = Signed(TransactionType.NFT_TX, 1M, new { Function = "Transfer()" });
            ordinary.FromAddress = "xNotAReserve";
            Assert.Null(LedgerIntegrityRules.ReserveMovesVfxOnlyByTransfer(ordinary, Rule));
        }
    }
}
