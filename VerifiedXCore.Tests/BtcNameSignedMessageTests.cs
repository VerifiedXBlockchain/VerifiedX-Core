using Key = NBitcoin.Key;
using ScriptPubKeyType = NBitcoin.ScriptPubKeyType;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using BtcSignatureService = VerifiedXCore.Bitcoin.Services.SignatureService;
using FeeCalcService = VerifiedXCore.Services.FeeCalcService;
using VfxSignatureService = VerifiedXCore.Services.SignatureService;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.BtcNameSignerOwnsAddressHeight a BTC name's Bitcoin signature is over "timestamp:owner:name.btc":
    /// a create is signed by the named address's key for its sender, and a transfer by the receiving address's key for
    /// the new owner. Judged as block validation judges them (VerifyTX at a block height).
    /// </summary>
    [Collection("DbContextSequential")]
    public class BtcNameSignedMessageTests : IDisposable
    {
        private const long Rule = 200;
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.BtcNameSignerOwnsAddressHeight;
        private readonly NBitcoin.Network _priorNetwork = Globals.BTCNetwork;
        private readonly (PrivateKey Key, string Pub, string Address) _alice = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _mallory = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _carol = NewKey();
        private readonly Key _aliceBtc = new Key();
        private readonly Key _carolBtc = new Key();

        public BtcNameSignedMessageTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"btc_name_signed_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Rule - 10 };
            Globals.BtcNameSignerOwnsAddressHeight = Rule;
            Globals.BTCNetwork = NBitcoin.Network.Main;
            foreach (var a in new[] { _alice, _mallory, _carol })
                StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = a.Address, Balance = 1_000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.BtcNameSignerOwnsAddressHeight = _priorRule;
            Globals.BTCNetwork = _priorNetwork;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static string BtcAddress(Key key) => key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Globals.BTCNetwork).ToString();

        private static (string Message, string Signature) BtcSigned(Key key, string message) =>
            (message, BtcSignatureService.CreateSignature(key.ToHex(), message));

        private static Transaction Signed((PrivateKey Key, string Pub, string Address) from, string to, object data)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = from.Address, ToAddress = to, Amount = Globals.ADNRRequiredRBX, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.ADNR, Data = JsonConvert.SerializeObject(data),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VfxSignatureService.CreateSignature(tx.Hash, from.Key, from.Pub);
            return tx;
        }

        private static async Task<(bool Ok, string Why)> Verify(Transaction tx, long height) =>
            await TransactionValidatorService.VerifyTX(tx, false, true, false, null, false, height, TimeUtil.GetTime());

        private Transaction Create((PrivateKey Key, string Pub, string Address) sender, string message, string signature) =>
            Signed(sender, "Adnr_Base", new { Function = "BTCAdnrCreate()", Name = "alice", BTCAddress = BtcAddress(_aliceBtc), Message = message, Signature = signature });

        // --- create ---

        [Fact]
        public async Task FromTheHeight_ACreateSignedForItsSenderAndName_Passes()
        {
            var (m, s) = BtcSigned(_aliceBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _alice.Address, "alice"));
            var (ok, why) = await Verify(Create(_alice, m, s), Rule);
            Assert.True(ok, why);
        }

        [Fact]
        public async Task FromTheHeight_ACreateSignatureCopiedIntoAnotherSendersCreate_IsRefused()
        {
            var (m, s) = BtcSigned(_aliceBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _alice.Address, "alice"));
            var (ok, why) = await Verify(Create(_mallory, m, s), Rule);
            Assert.False(ok);
            Assert.Contains("different VFX address", why);
        }

        [Fact]
        public async Task FromTheHeight_ACreateSignatureForAnotherName_IsRefused()
        {
            var (m, s) = BtcSigned(_aliceBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _alice.Address, "bob"));
            var (ok, why) = await Verify(Create(_alice, m, s), Rule);
            Assert.False(ok);
            Assert.Contains("different name", why);
        }

        [Fact]
        public async Task FromTheHeight_APlainTimestampMessage_IsRefused()
        {
            var (m, s) = BtcSigned(_aliceBtc, TimeUtil.GetTime().ToString());
            var (ok, why) = await Verify(Create(_alice, m, s), Rule);
            Assert.False(ok);
            Assert.Contains("Improper Message Format", why);
        }

        [Fact]
        public async Task BeforeTheHeight_APlainTimestampMessage_StillPasses()
        {
            var (m, s) = BtcSigned(_aliceBtc, TimeUtil.GetTime().ToString());
            var (ok, why) = await Verify(Create(_alice, m, s), Rule - 1);
            Assert.True(ok, why);
        }

        // --- transfer ---

        private void AliceOwnsTheName() =>
            BitcoinAdnr.GetBitcoinAdnr()!.InsertSafe(new BitcoinAdnr { Name = "alice.btc", BTCAddress = BtcAddress(_aliceBtc), RBXAddress = _alice.Address, TxHash = "c", Timestamp = 1 });

        private Transaction Transfer(string newOwner, string toBtc, string? message, string? signature)
        {
            object data = message == null
                ? new { Function = "BTCAdnrTransfer()", BTCToAddress = toBtc, BTCFromAddress = BtcAddress(_aliceBtc) }
                : new { Function = "BTCAdnrTransfer()", BTCToAddress = toBtc, BTCFromAddress = BtcAddress(_aliceBtc), BTCToMessage = message, BTCToSignature = signature };
            return Signed(_alice, newOwner, data);
        }

        [Fact]
        public async Task FromTheHeight_ATransferTheReceiverSigned_Passes()
        {
            AliceOwnsTheName();
            var (m, s) = BtcSigned(_carolBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _carol.Address, "alice.btc"));
            var (ok, why) = await Verify(Transfer(_carol.Address, BtcAddress(_carolBtc), m, s), Rule);
            Assert.True(ok, why);
        }

        [Fact]
        public async Task FromTheHeight_ATransferWithoutTheReceiversSignature_IsRefused()
        {
            AliceOwnsTheName();
            var (ok, why) = await Verify(Transfer(_carol.Address, BtcAddress(_carolBtc), null, null), Rule);
            Assert.False(ok);
            Assert.Contains("receiving BTC address must sign", why);
        }

        [Fact]
        public async Task FromTheHeight_ATransferToAnAddressWhoseKeyDidNotSign_IsRefused()
        {
            AliceOwnsTheName();
            // Signed by the sender's own key, pointing the name at someone else's address.
            var (m, s) = BtcSigned(_aliceBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _carol.Address, "alice.btc"));
            var (ok, why) = await Verify(Transfer(_carol.Address, BtcAddress(_carolBtc), m, s), Rule);
            Assert.False(ok);
            Assert.Contains("not from the key", why);
        }

        [Fact]
        public async Task FromTheHeight_AnAcceptanceForAnotherOwner_IsRefused()
        {
            AliceOwnsTheName();
            var (m, s) = BtcSigned(_carolBtc, BtcNameSignatures.Message(TimeUtil.GetTime(), _carol.Address, "alice.btc"));
            var (ok, why) = await Verify(Transfer(_mallory.Address, BtcAddress(_carolBtc), m, s), Rule);
            Assert.False(ok);
            Assert.Contains("different VFX address", why);
        }

        [Fact]
        public async Task FromTheHeight_AnAcceptanceOlderThanADay_IsRefused()
        {
            AliceOwnsTheName();
            var old = TimeUtil.GetTime() - BtcNameSignatures.TransferAcceptanceMaxAgeSeconds - 3600;
            var (m, s) = BtcSigned(_carolBtc, BtcNameSignatures.Message(old, _carol.Address, "alice.btc"));
            var (ok, why) = await Verify(Transfer(_carol.Address, BtcAddress(_carolBtc), m, s), Rule);
            Assert.False(ok);
            Assert.Contains("too old", why);
        }

        [Fact]
        public async Task BeforeTheHeight_AnUnsignedTransfer_StillPasses()
        {
            AliceOwnsTheName();
            var (ok, why) = await Verify(Transfer(_carol.Address, BtcAddress(_carolBtc), null, null), Rule - 1);
            Assert.True(ok, why);
        }

        // --- message ---

        [Theory]
        [InlineData("alice")]
        [InlineData("alice.btc")]
        [InlineData("Alice.BTC")]
        public void TheSignedName_IsComparedWithItsSuffixAndAnyCase(string signedName)
        {
            var (m, s) = BtcSigned(_aliceBtc, BtcNameSignatures.Message(1, _alice.Address, signedName));
            Assert.Null(BtcNameSignatures.Check(m, s, BtcAddress(_aliceBtc), _alice.Address, "alice", Globals.BTCNetwork, out var ts));
            Assert.Equal(1, ts);
        }
    }
}
