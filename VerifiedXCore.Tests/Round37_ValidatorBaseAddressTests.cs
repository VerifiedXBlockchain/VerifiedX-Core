using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Round 37: a validator's registered Base address is the one its own key derives, and this node signs no vBTCb
    /// validator ADD/REMOVE message. A registration or heartbeat naming another BaseAddress is refused at admission at
    /// every height and in blocks from the path-audit height; the address an honest node sends (derived from its account)
    /// passes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class Round37_ValidatorBaseAddressTests : IDisposable
    {
        private const long Gate = 1000;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorGate;
        private readonly bool _priorEncrypted = Globals.IsWalletEncrypted;
        private readonly SecureString _priorPassword = Globals.EncryptPassword;
        private readonly Account _validator;

        public Round37_ValidatorBaseAddressTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"r37base_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorGate = Globals.PathAuditRulesHeight;
            Globals.PathAuditRulesHeight = Gate;
            DbContext.Initialize();
            Globals.IsWalletEncrypted = false;
            Globals.EncryptPassword = new SecureString();
            Globals.LastBlock = new Block { Height = 10, Timestamp = TimeUtil.GetTime() }; // admission judges at tip + 1, below the gate

            _validator = AccountData.CreateNewAccount(skipSave: true)!;
            AccountData.GetAccounts().Insert(_validator);
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _validator.Address, Balance = 10_000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorGate;
            Globals.IsWalletEncrypted = _priorEncrypted;
            Globals.EncryptPassword = _priorPassword;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private Transaction Signed(TransactionType type, string? baseAddress)
        {
            var tx = new Transaction
            {
                FromAddress = _validator.Address, ToAddress = _validator.Address, Amount = 0M, Fee = 0M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = type,
                Data = JsonConvert.SerializeObject(new { ValidatorAddress = _validator.Address, IPAddress = "203.0.113.10", FrostPublicKey = _validator.PublicKey, BaseAddress = baseAddress, IsS3C = false }),
            };
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _validator.GetPrivKey, _validator.PublicKey);
            return tx;
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_VALIDATOR_REGISTER)]
        [InlineData(TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)]
        public async Task ABaseAddressNotDerivedFromTheValidatorsKey_IsRefused(TransactionType type)
        {
            var chosen = "0x414444" + new string('1', 34);
            var tx = Signed(type, chosen);

            var (poolOk, poolMsg) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(poolOk);
            Assert.Equal(TransactionValidatorService.RegisteredBaseAddressReason, poolMsg);

            var (blockOk, blockMsg) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, Gate);
            Assert.False(blockOk);
            Assert.Equal(TransactionValidatorService.RegisteredBaseAddressReason, blockMsg);

            var (_, belowMsg) = await TransactionValidatorService.VerifyTX(tx, true, true, false, null, false, Gate - 1);
            Assert.NotEqual(TransactionValidatorService.RegisteredBaseAddressReason, belowMsg); // replay below the height
        }

        [Theory]
        [InlineData(TransactionType.VBTC_V2_VALIDATOR_REGISTER)]
        [InlineData(TransactionType.VBTC_V2_VALIDATOR_HEARTBEAT)]
        public async Task TheAddressAnHonestNodeSends_Passes(TransactionType type)
        {
            // What the register / heartbeat builders send: Globals.ValidatorBaseAddress, derived from the node's account.
            var honest = ValidatorEthKeyService.DeriveBaseAddressFromAccount(_validator.Address);
            Assert.False(string.IsNullOrEmpty(honest));
            Assert.Equal(honest, ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(_validator.PublicKey), StringComparer.OrdinalIgnoreCase);

            var (_, msg) = await TransactionValidatorService.VerifyTX(Signed(type, honest), false, true, false, null, false, Gate);
            Assert.NotEqual(TransactionValidatorService.RegisteredBaseAddressReason, msg);
            var (_, noAddressMsg) = await TransactionValidatorService.VerifyTX(Signed(type, null), false, true, false, null, false, Gate);
            Assert.NotEqual(TransactionValidatorService.RegisteredBaseAddressReason, noAddressMsg);
        }

        [Fact]
        public void NoValidatorAddOrRemoveMessageIsSigned()
        {
            var request = new BaseValidatorSyncService.ValidatorUpdateSignRequest { Action = "ADD", TargetAddresses = new[] { "0x" + new string('2', 40) }, VfxBlockHeight = 10, Timestamp = TimeUtil.GetTime() };
            var (ok, reason) = BaseValidatorSyncService.AuthorizeSignRequest(request);
            Assert.False(ok);
            Assert.Equal(BaseValidatorSyncService.AdminChangesNotSignedReason, reason);

            Globals.ValidatorAddress = _validator.Address;
            try
            {
                Assert.Null(BaseValidatorSyncService.SignValidatorUpdateLocally("ADD", new[] { "0x" + new string('2', 40) }, 10));
                Assert.Null(BaseValidatorSyncService.SignValidatorUpdateLocally("REMOVE", new[] { "0x" + new string('2', 40) }, 10));
            }
            finally { Globals.ValidatorAddress = ""; }
        }
    }
}
