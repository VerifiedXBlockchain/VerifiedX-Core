using System;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>BB-6 (NEW-04, NEW-05, NEW-06): stateless ledger-integrity predicates shared by consensus and the replay scan.</summary>
    public class BB6_LedgerIntegrityRulesTests
    {
        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        [Fact]
        public void BB6_TokenTransfer()
        {
            Assert.Null(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xA", "xB", 1M));
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xVICTIM", "xB", 1M));
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xA", "xC", 1M));
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xA", "xB", 0M));
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xA", "xB", -1M));
            Assert.NotNull(LedgerIntegrityRules.TokenTransfer("xA", "xB", "xA", "xB", null));
        }

        [Fact]
        public void BB6_TokenBurnAndVote()
        {
            Assert.Null(LedgerIntegrityRules.TokenBurn("xA", "xA", 1M));
            Assert.NotNull(LedgerIntegrityRules.TokenBurn("xA", "xVICTIM", 1M));
            Assert.NotNull(LedgerIntegrityRules.TokenBurn("xA", "xA", -1M));
            Assert.Null(LedgerIntegrityRules.TokenVoteCast("xA", "xA"));
            Assert.NotNull(LedgerIntegrityRules.TokenVoteCast("xA", "xVICTIM"));
        }

        [Fact]
        public void BB6_V1Amount()
        {
            Assert.Null(LedgerIntegrityRules.V1TransferAmount(0.5M));
            Assert.NotNull(LedgerIntegrityRules.V1TransferAmount(0M));
            Assert.NotNull(LedgerIntegrityRules.V1TransferAmount(-1M));
            Assert.NotNull(LedgerIntegrityRules.V1TransferAmount(0.000000001M)); // > 8 decimals
        }

        [Fact]
        public void BB6_V1MultiInput_SignatureMustVerify()
        {
            var holder = NewKey();
            const string sigInput = "abc123";
            var good = new VBTCTransferInput { SCUID = "sc", FromAddress = holder.Address, Amount = 1M,
                Signature = SignatureService.CreateSignature(sigInput + "xTO" + "xFROM", holder.Key, holder.Pub) };
            Assert.Null(LedgerIntegrityRules.V1TransferMultiInput(good, sigInput, "xTO", "xFROM"));

            var forged = new VBTCTransferInput { SCUID = "sc", FromAddress = holder.Address, Amount = 1M, Signature = "junk" };
            Assert.NotNull(LedgerIntegrityRules.V1TransferMultiInput(forged, sigInput, "xTO", "xFROM"));

            var otherTx = new VBTCTransferInput { SCUID = "sc", FromAddress = holder.Address, Amount = 1M, Signature = good.Signature };
            Assert.NotNull(LedgerIntegrityRules.V1TransferMultiInput(otherTx, sigInput, "xOTHER", "xFROM")); // bound to this tx

            var negative = new VBTCTransferInput { SCUID = "sc", FromAddress = holder.Address, Amount = -1M, Signature = good.Signature };
            Assert.NotNull(LedgerIntegrityRules.V1TransferMultiInput(negative, sigInput, "xTO", "xFROM"));
        }

        [Fact]
        public void BB6_CreatedContractUid()
        {
            Transaction Tx(TransactionType t, string fn) => new Transaction
            {
                TransactionType = t,
                Data = JsonConvert.SerializeObject(new[] { new { Function = fn, ContractUID = "uid-1", Data = "x" } }),
            };
            Assert.Equal("uid-1", LedgerIntegrityRules.CreatedContractUid(Tx(TransactionType.FTKN_MINT, "TokenDeploy()")));
            Assert.Equal("uid-1", LedgerIntegrityRules.CreatedContractUid(Tx(TransactionType.NFT_MINT, "Mint()")));
            Assert.Null(LedgerIntegrityRules.CreatedContractUid(Tx(TransactionType.NFT_TX, "Transfer()")));
        }
    }
}
