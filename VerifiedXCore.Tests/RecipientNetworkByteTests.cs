using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Controllers;
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
    /// A ToAddress must carry this network's version byte: from Globals.RecipientNetworkByteHeight in a block, and at
    /// every height at admission and in the address-validation API. A testnet address on mainnet, or a Bitcoin-style
    /// version-0 address, passed the checksum check and what was sent there was lost.
    /// </summary>
    [Collection("DbContextSequential")]
    public class RecipientNetworkByteTests : IDisposable
    {
        private const long Rule = 5_000;
        private const string Refusal = "address of another network";
        private const string BitcoinStyle = "1BgGZ9tcN4rm9KBzDn7KprQz87SZ26SAMH"; // version 0, valid checksum
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.RecipientNetworkByteHeight;
        private readonly byte _priorPrefix = Globals.AddressPrefix;
        private readonly (PrivateKey Key, string Pub, string Address) _sender;

        public RecipientNetworkByteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"net_byte_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.RecipientNetworkByteHeight = Rule;
            Globals.LastBlock = new Block { Height = Rule - 1 };
            _sender = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.AddressPrefix = _priorPrefix;
            Globals.RecipientNetworkByteHeight = _priorRule;
            Globals.LastBlock = _priorLastBlock;
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        /// <summary>An address of the other network (mainnet 'R' when this one is testnet 'x', and the reverse).</summary>
        private string OtherNetworksAddress()
        {
            var ours = Globals.AddressPrefix;
            Globals.AddressPrefix = ours == 0x89 ? (byte)0x3C : (byte)0x89;
            try { return NewKey().Address; }
            finally { Globals.AddressPrefix = ours; }
        }

        private Transaction SendTo(string to)
        {
            var tx = new Transaction { Timestamp = TimeUtil.GetTime(), FromAddress = _sender.Address, ToAddress = to, Amount = 1M, Fee = 0, Nonce = 0, TransactionType = TransactionType.TX };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        [Fact]
        public void OnlyAnotherNetworksWellFormedAddress_IsOneOfAnotherNetwork()
        {
            Assert.True(AddressValidateUtility.IsOtherNetworksAddress(OtherNetworksAddress()));
            Assert.True(AddressValidateUtility.IsOtherNetworksAddress(BitcoinStyle));
            Assert.False(AddressValidateUtility.IsOtherNetworksAddress(NewKey().Address));
            string reserve;
            do reserve = ReserveAccount.GetHumanAddress(NewKey().Pub); while (!reserve.StartsWith("xRBX", StringComparison.Ordinal));
            Assert.False(AddressValidateUtility.IsOtherNetworksAddress(reserve)); // xRBX: its own rule
            Assert.False(AddressValidateUtility.IsOtherNetworksAddress("alice.vfx"));
            Assert.False(AddressValidateUtility.IsOtherNetworksAddress("Adnr_Base"));
        }

        [Fact]
        public async Task InABlock_AnotherNetworksRecipient_IsRefusedFromTheRule_AndBelowItIsNot()
        {
            foreach (var to in new[] { OtherNetworksAddress(), BitcoinStyle })
            {
                Assert.Contains(Refusal, (await TransactionValidatorService.VerifyTX(SendTo(to), false, true, false, null, false, Rule)).Item2);
                Assert.DoesNotContain(Refusal, (await TransactionValidatorService.VerifyTX(SendTo(to), false, true, false, null, false, Rule - 1)).Item2);
            }
        }

        [Fact]
        public async Task AtAdmission_AnotherNetworksRecipient_IsRefusedAtEveryHeight_AndOurOwnIsNot()
        {
            Assert.Contains(Refusal, (await TransactionValidatorService.VerifyTX(SendTo(OtherNetworksAddress()))).Item2); // tip below the rule
            var (ok, why) = await TransactionValidatorService.VerifyTX(SendTo(NewKey().Address));
            Assert.True(ok, why);
        }

        [Fact]
        public async Task TheAddressApi_DoesNotCallAnotherNetworksAddressValid()
        {
            var api = new V1Controller();
            Assert.Equal("False", await api.ValidateAddress(OtherNetworksAddress()));
            Assert.Equal("False", await api.ValidateAddress(BitcoinStyle));
            Assert.Equal("True", await api.ValidateAddress(NewKey().Address));
        }
    }
}
