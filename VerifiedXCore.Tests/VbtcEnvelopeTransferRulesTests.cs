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
    /// From Globals.VbtcEnvelopeRulesHeight a vBTC V2 transfer sent as a contract call (Function "TransferVBTCV2()"
    /// under TKNZ_TX and the other contract-call types) is judged like the typed VBTC_V2_TRANSFER: its Data names each key
    /// once, and the recipient it names is the one the apply credits. Below the height it is judged as before.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VbtcEnvelopeTransferRulesTests : IDisposable
    {
        private const string V2 = "6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e6e:1790500020";
        private const long Tip = 1001;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorEnvelopeRule = Globals.VbtcEnvelopeRulesHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public VbtcEnvelopeTransferRulesTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vbtcenv_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Tip };

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
            Globals.VbtcEnvelopeRulesHeight = _priorEnvelopeRule;
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

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) signer, string to, TransactionType type, string data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = signer.Address, ToAddress = to, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = data,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private string Call(string amountPart, string? dataTo = null) =>
            $"{{\"Function\":\"TransferVBTCV2()\",\"ContractUID\":\"{V2}\",\"FromAddress\":\"{_holder.Address}\",\"ToAddress\":\"{dataTo ?? _other.Address}\",{amountPart}}}";

        /// <summary>The rule starts at the next block (in force) or the one after (not yet).</summary>
        private static void RuleInForce(bool inForce) => Globals.VbtcEnvelopeRulesHeight = inForce ? Tip + 1 : Tip + 2;

        [Theory]
        [InlineData("\"Amount\":0.5,\"Amount\":0.00000001", "twice")]
        [InlineData("\"Amount\":0.00000001,\"amount\":0.5", "two spellings")]
        [InlineData("\"Amount\":0.5,\"AMOUNT\":0.00000001", "two spellings")]
        public async Task ARepeatedOrRespelledKey_InAnEnvelopeTransfer_IsRefusedFromTheHeight(string amountPart, string reason)
        {
            var tx = Signed(_holder, _other.Address, TransactionType.TKNZ_TX, Call(amountPart));

            RuleInForce(false);
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(ok, "below the height the envelope transfer is judged as before: " + why);

            RuleInForce(true);
            (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains(reason, why);
        }

        [Fact]
        public async Task ACleanEnvelopeTransfer_IsAcceptedOnBothSidesOfTheHeight()
        {
            var tx = Signed(_holder, _other.Address, TransactionType.TKNZ_TX, Call("\"Amount\":0.5"));
            foreach (var inForce in new[] { false, true })
            {
                RuleInForce(inForce);
                var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
                Assert.True(ok, why);
            }
        }

        [Fact]
        public async Task AnEnvelopeTransferWhoseDataNamesAnotherRecipient_IsRefusedFromTheHeight()
        {
            // The apply credits the transaction's ToAddress; Data names a third address.
            var (_, _, third) = NewKey();
            var tx = Signed(_holder, _other.Address, TransactionType.TKNZ_TX, Call("\"Amount\":0.5", dataTo: third));

            RuleInForce(false);
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.True(ok, "below the height the envelope transfer is judged as before: " + why);

            RuleInForce(true);
            (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("To address in data must match transaction ToAddress", why);

            // The comparison is exact: a recipient spelled in another case is another recipient.
            tx = Signed(_holder, _other.Address, TransactionType.TKNZ_TX, Call("\"Amount\":0.5", dataTo: _other.Address.ToUpperInvariant()));
            (ok, why) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Contains("To address in data must match transaction ToAddress", why);
        }

        [Fact]
        public void TheRule_CoversEveryEnvelopeTypeAndTheMultiCall_AndNoOtherFunction()
        {
            RuleInForce(true);
            var height = Tip + 1;
            var dup = Call("\"Amount\":0.5,\"Amount\":0.00000001");
            foreach (var type in new[] { TransactionType.TKNZ_TX, TransactionType.NFT_TX, TransactionType.SC_TX, TransactionType.FTKN_TX })
            {
                Assert.Contains("twice", LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = type, Data = dup }, height));
                Assert.Contains("twice", LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = type, Data = "[" + dup + "]" }, height));
            }
            var multi = $"{{\"Function\":\"TransferVBTCMultiV2()\",\"Inputs\":[{{\"ContractUID\":\"{V2}\",\"Amount\":1,\"amount\":2}}]}}";
            Assert.Contains("two spellings", LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = TransactionType.TKNZ_TX, Data = multi }, height));
            // Other contract calls are not vBTC transfers and keep their own rules.
            var token = $"{{\"Function\":\"TokenTransfer()\",\"ContractUID\":\"{V2}\",\"Amount\":1,\"Amount\":2}}";
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = TransactionType.FTKN_TX, Data = token }, height));
            // Below the height an envelope transfer is not judged by this rule.
            Assert.Null(LedgerIntegrityRules.VbtcDataKeysAreDistinct(new Transaction { TransactionType = TransactionType.TKNZ_TX, Data = dup }, height - 1));
        }
    }
}
