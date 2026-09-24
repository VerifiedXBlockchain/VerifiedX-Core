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
    /// NEW-08 (found by the second independent review; pre-existing; possible chain split): the single-shape vBTC V2
    /// withdrawal request's per-contract mempool guard also ran during block verification. A node whose own mempool held
    /// a competing request for the same contract rejected a block its peers accepted. The multi-contract shape already
    /// limited the same guard to mempool admission.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW08_WithdrawalGuardAtBlockVerifyTests : IDisposable
    {
        private const string V2 = "7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e:1790500003";
        private const string BtcDest = "tb1qw508d6qejxtdg4y5r3zarvary0c5xw7kxpjzsx";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorEscrowHeight = Globals.WithdrawalEscrowHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _holder = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _other = NewKey();

        public NEW08_WithdrawalGuardAtBlockVerifyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new08_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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
                    new SmartContractStateTreiTokenizationTX { FromAddress = "+", ToAddress = _other.Address, Amount = 1.0M },
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

        private Transaction V2Withdrawal(decimal amount, long nonce = 0) =>
            Signed(_holder, _holder.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2, BTCAddress = BtcDest, Amount = amount, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() }, nonce);

        [Fact]
        public async Task NEW08_BlockVerification_DoesNotReadTheLocalMempool()
        {
            // Another requester's withdrawal for the same contract sits in THIS node's mempool. The block carrying the
            // holder's request must verify the same here as on a node without it.
            var otherHolderTx = Signed(_other, _other.Address, TransactionType.VBTC_V2_WITHDRAWAL_REQUEST,
                new { Function = "WithdrawalRequest()", ContractUID = V2, BTCAddress = BtcDest, Amount = 0.1M, FeeRate = 10, UniqueId = Guid.NewGuid().ToString() });
            TransactionData.GetPool().InsertSafe(otherHolderTx);

            var mine = V2Withdrawal(0.5M);
            var atAdmission = await TransactionValidatorService.VerifyTX(mine);
            Assert.False(atAdmission.Item1); // mempool admission still refuses a second pending request for the contract

            var (ok, message) = await TransactionValidatorService.VerifyTX(mine, blockDownloads: false, blockVerify: true);
            Assert.True(ok, message);
        }
    }
}
