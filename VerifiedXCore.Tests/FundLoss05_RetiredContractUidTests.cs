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
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using SignatureService = VerifiedXCore.Services.SignatureService;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// C-169: a sale bid names only the contract's bare UID. A UID burned and then minted again under other content could
    /// complete a bid made for the first. From the path-audit rules a Burn() retires the UID, and no creation (Mint() under
    /// any type, TokenDeploy()) is accepted under it again, on the block path and at admission alike. Burns below the rules
    /// retire nothing, so history replays unchanged.
    ///
    /// Transactions are judged as block validation judges them (VerifyTX at a block height) and applied with UpdateTreis.
    /// </summary>
    [Collection("DbContextSequential")]
    public class FundLoss05_RetiredContractUidTests : IDisposable
    {
        private const long Gate = 20;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorGate = Globals.PathAuditRulesHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public FundLoss05_RetiredContractUidTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"retireduid_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 9 };
            Globals.PathAuditRulesHeight = Gate;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorGate;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static string NewUid() => Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();

        private Transaction Mint(string uid, TransactionType type = TransactionType.NFT_MINT) =>
            Signed(type, new[] { new { Function = "Mint()", ContractUID = uid, Data = VbtcTestContracts.BuildContractData(uid, _owner.Address, null), MD5List = "NA" } });

        private Transaction Deploy(string uid) =>
            Signed(TransactionType.TKNZ_MINT, new[] { new { Function = "TokenDeploy()", ContractUID = uid, Data = VbtcTestContracts.TokenContractData(uid, _owner.Address, 1000), MD5List = "NA" } });

        private Transaction Burn(string uid) =>
            Signed(TransactionType.NFT_BURN, new[] { new { Function = "Burn()", ContractUID = uid } });

        private Transaction Signed(TransactionType type, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = type, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Why)> Verify(Transaction tx, long height)
        {
            var (ok, why) = await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height);
            return (ok, why);
        }

        private static async Task Apply(long height, params Transaction[] txs)
        {
            var block = new Block { Height = height, Hash = $"b{height}", StateRoot = "root" + height, Timestamp = TimeUtil.GetTime(), Transactions = txs.ToList() };
            Assert.True(await StateData.UpdateTreis(block));
            Globals.LastBlock = block;
        }

        private async Task Minted(string uid, long height)
        {
            var mint = Mint(uid);
            var (ok, why) = await Verify(mint, height);
            Assert.True(ok, why);
            await Apply(height, mint);
            Assert.NotNull(SmartContractStateTrei.GetSmartContractState(uid));
        }

        private async Task Burned(string uid, long height)
        {
            var burn = Burn(uid);
            var (ok, why) = await Verify(burn, height);
            Assert.True(ok, why);
            await Apply(height, burn);
            Assert.Null(SmartContractStateTrei.GetSmartContractState(uid));
        }

        [Fact]
        public async Task AUidBurnedFromTheRules_IsNeverCreatedAgain_ByAnyCreation()
        {
            var uid = NewUid();
            await Minted(uid, Gate - 1);
            await Burned(uid, Gate);
            Assert.True(RetiredContractUid.IsRetired(uid));

            foreach (var creation in new[] { Mint(uid), Mint(uid, TransactionType.SC_MINT), Deploy(uid) })
            {
                var (ok, why) = await Verify(creation, Gate + 1);
                Assert.False(ok);
                Assert.Contains("was burned", why);
            }
            var (admitted, admittedWhy) = await TransactionValidatorService.VerifyTX(Mint(uid));   // admission and the raw path: tip + 1
            Assert.False(admitted);
            Assert.Contains("was burned", admittedWhy);

            var fresh = await Verify(Mint(NewUid()), Gate + 1);                                     // other UIDs are untouched
            Assert.True(fresh.Ok, fresh.Why);
        }

        [Fact]
        public async Task AUidBurnedBelowTheRules_IsNotRetired()
        {
            var uid = NewUid();
            await Minted(uid, Gate - 3);
            await Burned(uid, Gate - 2);
            Assert.False(RetiredContractUid.IsRetired(uid));
            var again = await Verify(Mint(uid), Gate + 1);
            Assert.True(again.Ok, again.Why);
        }

        [Fact]
        public async Task AFullStateRebuild_RetiresTheSameUids_AndNoOthers()
        {
            var uid = NewUid();
            var mint = Mint(uid);
            var burn = Burn(uid);
            var prev = "";
            for (long h = 0; h <= Gate; h++)
            {
                var txs = new List<Transaction>();
                if (h == Gate - 1) txs.Add(mint);
                if (h == Gate) txs.Add(burn);
                BlockchainData.GetBlocks().InsertSafe(new Block { Height = h, Hash = $"b{h}", PrevHash = prev, Timestamp = TimeUtil.GetTime(), Transactions = txs });
                prev = $"b{h}";
            }
            RetiredContractUid.Retire("ffff:1", 5, "not-on-chain");
            BlockRollbackUtility.StateWipedForTests = () => StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
            try { Assert.True(await BlockRollbackUtility.ResetTreis()); }
            finally { BlockRollbackUtility.StateWipedForTests = null; }

            Assert.True(RetiredContractUid.IsRetired(uid));
            Assert.False(RetiredContractUid.IsRetired("ffff:1"));
        }
    }
}
