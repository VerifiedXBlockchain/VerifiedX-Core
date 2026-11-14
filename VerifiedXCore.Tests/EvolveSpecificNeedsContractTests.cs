using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
    /// ChangeEvolveStateSpecific() checks the stored contract's minter and owner, but a contract not yet in the state (one
    /// minted earlier in the same block) had no check at all, so anyone's change overwrote it - its royalty included.
    /// Such a change is refused at admission, and in a block from EvolveSpecificNeedsContractHeight; older blocks replay
    /// as they were judged.
    /// </summary>
    [Collection("DbContextSequential")]
    public class EvolveSpecificNeedsContractTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly (PrivateKey Key, string Pub, string Address) _stranger = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _minter = NewKey();

        public EvolveSpecificNeedsContractTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"evolve_specific_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 1000 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _stranger.Address, Balance = 1000M, Nonce = 0 });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _minter.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static Transaction Change(string scUid, (PrivateKey Key, string Pub, string Address) from)
        {
            var tx = new Transaction
            {
                FromAddress = from.Address, ToAddress = from.Address, Amount = 0M, Fee = 0.00000100M, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "ChangeEvolveStateSpecific()", ContractUID = scUid, Data = VbtcTestContracts.PlainNftContractData, MD5List = "NA" } }),
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private static string UnmintedUid() => Guid.NewGuid().ToString("N") + ":" + TimeUtil.GetTime();

        [Fact]
        public async Task AChangeToAContractNotInTheState_IsRefused_AtAdmission_AndInBlocksFromTheHeight()
        {
            var height = Globals.EvolveSpecificNeedsContractHeight;
            Assert.Equal((false, "SC does not exist."), await TransactionValidatorService.VerifyTX(Change(UnmintedUid(), _stranger)));
            Assert.Equal((false, "SC does not exist."), await TransactionValidatorService.VerifyTX(Change(UnmintedUid(), _stranger), false, true, false, null, false, height));
            // A block before the height is judged as older builds judged it.
            Assert.NotEqual("SC does not exist.", (await TransactionValidatorService.VerifyTX(Change(UnmintedUid(), _stranger), false, true, false, null, false, height - 1)).Item2);
        }

        [Fact]
        public async Task OnceTheContractIsStored_OnlyItsMinterAndOwnerGetPastTheOwnerCheck()
        {
            var uid = UnmintedUid();
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = uid, ContractData = VbtcTestContracts.PlainNftContractData, MinterAddress = _minter.Address,
                OwnerAddress = _minter.Address, Nonce = 0,
            });
            Assert.Equal((false, "You are attempting to devolve/evolve a Smart contract you don't own."),
                await TransactionValidatorService.VerifyTX(Change(uid, _stranger)));
            var (_, mine) = await TransactionValidatorService.VerifyTX(Change(uid, _minter));
            Assert.NotEqual("SC does not exist.", mine);
            Assert.NotEqual("You are attempting to devolve/evolve a Smart contract you don't own.", mine);
        }
    }
}
