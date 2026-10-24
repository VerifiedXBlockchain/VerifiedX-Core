using NBitcoin;
using VerifiedXCore.Bitcoin.Services;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// From Globals.BtcNameSignerOwnsAddressHeight a BTC name create is signed by the key of the Bitcoin address it names,
    /// in any address form a wallet makes from that key.
    /// </summary>
    public class BtcNameSignerOwnsAddressTests
    {
        private static readonly Network Net = Network.Main;

        private static (string Signature, PubKey PubKey) Signed(Key key) =>
            (SignatureService.CreateSignature(key.ToHex(), "1760000000"), key.PubKey);

        [Theory]
        [InlineData(ScriptPubKeyType.Legacy)]
        [InlineData(ScriptPubKeyType.Segwit)]
        [InlineData(ScriptPubKeyType.SegwitP2SH)]
        [InlineData(ScriptPubKeyType.TaprootBIP86)]
        public void TheSignersOwnAddress_InEachForm_Passes(ScriptPubKeyType type)
        {
            var (signature, pubKey) = Signed(new Key());
            var address = pubKey.GetAddress(type, Net).ToString();
            Assert.True(SignatureService.SignerOwnsAddress(signature, address, Net));
        }

        [Fact]
        public void AnUntweakedTaprootAddressOfTheSignersKey_Passes()
        {
            // Some wallets make the Taproot output from the key itself, without the BIP86 tweak.
            var (signature, pubKey) = Signed(new Key());
            var xOnly = pubKey.ToBytes().AsSpan(1).ToArray();
            var address = new TaprootPubKey(xOnly).GetAddress(Net).ToString();
            Assert.True(SignatureService.SignerOwnsAddress(signature, address, Net));
        }

        [Theory]
        [InlineData(ScriptPubKeyType.Segwit)]
        [InlineData(ScriptPubKeyType.Legacy)]
        public void AnotherKeysAddress_IsRefused(ScriptPubKeyType type)
        {
            var (signature, _) = Signed(new Key());
            var someoneElses = new Key().PubKey.GetAddress(type, Net).ToString();
            Assert.False(SignatureService.SignerOwnsAddress(signature, someoneElses, Net));
        }

        [Fact]
        public void AnAddressOfAnotherNetwork_IsRefused()
        {
            var (signature, pubKey) = Signed(new Key());
            var testnetAddress = pubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();
            Assert.False(SignatureService.SignerOwnsAddress(signature, testnetAddress, Net));
        }

        [Theory]
        [InlineData("not-a-signature")]
        [InlineData("3044.zz")]
        public void AMalformedSignature_IsRefused(string signature)
        {
            var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Net).ToString();
            Assert.False(SignatureService.SignerOwnsAddress(signature, address, Net));
        }

        [Fact]
        public void TheRuleActivatesWithTheLedgerRulesBatch()
        {
            Assert.Equal(Globals.LedgerRulesBatchHeight, Globals.BtcNameSignerOwnsAddressHeight);
        }
    }
}
