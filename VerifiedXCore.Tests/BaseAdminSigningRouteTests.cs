using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// SignBaseAdminAction: the operator's signature on a vBTCb contract change, through the validator API as the node
    /// serves it. Only the node's own operator (loopback) gets one; the node reads the contract's version and admin nonce
    /// itself and signs for its configured contract and chain in the form that contract reads. The digests equal ethers'
    /// (deploy/ Hardhat tests sign with it): reference values for nonce 5 on the mainnet proxy, chain 8453.
    /// </summary>
    [Collection("DbContextSequential")]
    public class BaseAdminSigningRouteTests : IDisposable
    {
        private const string Proxy = "0x09096c6eBf35F0aDAEeC02D06a0dE77E790F132D";
        private const string Implementation = "0xd2bB4BdA89A23c63eaaD464F15658845a2114Ffa";
        private const string Dark = "0x00000000000000000000000000000000000000aa";
        private const string Seed = "0xD140DfAB7F08B5ce8E54A8b364E447b49E6EEda6";
        private static readonly byte[] ImplementationCode = { 0x60, 0x80, 0x60, 0x40, 0x52 };

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly string _priorValidator = Globals.ValidatorAddress;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly string _priorContract = BaseBridgeService.ContractAddress;
        private readonly int _priorChainId = BaseBridgeService.BaseChainId;
        private readonly string _myBase;
        private readonly string _myPub;

        public BaseAdminSigningRouteTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"baseadmin_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            var key = new PrivateKey("secp256k1");
            _myPub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            var me = AccountData.GetHumanAddress(_myPub);
            AccountData.GetAccounts().InsertSafe(new Account { Address = me, PublicKey = _myPub, PrivateKey = "0" + key.secret.ToString("x") });
            _myBase = ValidatorEthKeyService.DeriveBaseAddressFromVfxPublicKey(_myPub);
            Globals.ValidatorAddress = me;
            // This node is a registered vBTC validator.
            BlockchainData.GetBlocks().InsertSafe(new Block
            {
                Height = 10, Hash = "h10",
                Transactions = new List<Transaction> { new Transaction
                {
                    TransactionType = TransactionType.VBTC_V2_VALIDATOR_REGISTER, FromAddress = me, ToAddress = me, Hash = "r10",
                    Data = JsonConvert.SerializeObject(new { ValidatorAddress = me, IPAddress = "10.0.0.1", FrostPublicKey = _myPub, IsS3C = false }),
                } },
            });
            Globals.LastBlock = new Block { Height = 20 };
            BaseBridgeService.ContractAddress = Proxy;
            BaseBridgeService.BaseChainId = 8453;
            BaseAdminSigningService.ReadCodeForTests = a => Task.FromResult<byte[]?>(
                string.Equals(a, Implementation, StringComparison.OrdinalIgnoreCase) ? ImplementationCode : Array.Empty<byte>());
            Contract(version: 4, nonce: 5);
        }

        public void Dispose()
        {
            BaseBridgeService.ReadSignerViewForTests = null;
            BaseAdminSigningService.ReadCodeForTests = null;
            BaseBridgeService.ContractAddress = _priorContract;
            BaseBridgeService.BaseChainId = _priorChainId;
            Globals.ValidatorAddress = _priorValidator;
            Globals.LastBlock = _priorLastBlock;
            VBTCValidatorRegistry.InvalidateCache();
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private void Contract(int version, int nonce) =>
            BaseBridgeService.ReadSignerViewForTests = () => Task.FromResult<BaseBridgeService.SignerView?>(
                new BaseBridgeService.SignerView(new[] { Seed, Dark, _myBase }, version, 2, nonce));

        private static async Task<(HttpStatusCode Code, JObject Body)> Sign(object request, string from = "127.0.0.1")
        {
            using var server = new TestServer(new WebHostBuilder().UseStartup<StartupP2PCaster>());
            var ctx = await server.SendAsync(c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/valapi/validator/SignBaseAdminAction";
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request)));
                c.Connection.RemoteIpAddress = IPAddress.Parse(from);
            });
            using var reader = new StreamReader(ctx.Response.Body);
            var text = await reader.ReadToEndAsync();
            var json = text.Trim().StartsWith("\"") ? JsonConvert.DeserializeObject<string>(text)! : text;
            return ((HttpStatusCode)ctx.Response.StatusCode, JObject.Parse(json));
        }

        private void AssertSignedBy(JObject body, string digest, bool personal)
        {
            Assert.True(body.Value<bool>("Success"), body.ToString());
            Assert.Equal(digest, body.Value<string>("Digest"));
            Assert.Equal(_myBase, body.Value<string>("Signer"), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(_myBase, ValidatorEthKeyService.RecoverSigner(Convert.FromHexString(digest[2..]), body.Value<string>("Signature"), personal), StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("203.0.113.7")]
        [InlineData("10.0.0.5")]
        public async Task ARemoteCaller_GetsNoSignature(string from)
        {
            var (code, body) = await Sign(new { Action = "CANCEL_PENDING" }, from);
            Assert.Equal(HttpStatusCode.Unauthorized, code);
            Assert.Null(body["Signature"]);
        }

        [Fact]
        public async Task TheOperator_SignsEachV4Change_ForTheConfiguredContract_AtItsNonce()
        {
            var (code, upgrade) = await Sign(new { Action = "PROPOSE_UPGRADE", Implementation });
            Assert.Equal(HttpStatusCode.OK, code);
            AssertSignedBy(upgrade, "0xb694ca78bf709372a9c002ec5050002af5f53bc1ac8d39302efbda93d26dea80", personal: false);
            Assert.Equal("0x" + Convert.ToHexString(Nethereum.Util.Sha3Keccack.Current.CalculateHash(ImplementationCode)).ToLowerInvariant(),
                upgrade.Value<string>("ImplementationCodeHash"));
            Assert.Equal("5", upgrade.Value<string>("AdminNonce"));

            AssertSignedBy((await Sign(new { Action = "PROPOSE_UPGRADE_DELAY", DelaySeconds = 259200 })).Body,
                "0x2f8e23b6339231ba7614c8c1ba01809e19decc443d28ba6ced53ce85e36661f4", personal: false);
            AssertSignedBy((await Sign(new { Action = "cancel_pending" })).Body,
                "0x60b2be5f3ad61d24cce4698888a6f7d469245106c4fe285b4cde24e009ba635c", personal: false);
            AssertSignedBy((await Sign(new { Action = "PROPOSE_REMOVAL", Validators = new[] { Seed, Dark }, VfxBlockHeight = 7_603_200 })).Body,
                "0x46abf760aabbd9655c290b4eb61a650d926734167d65207ff0092a90867146fc", personal: false);
        }

        [Fact]
        public async Task TheOperator_SignsTheV3Upgrade_OnlyOnAV3Contract()
        {
            var (_, v3OnV4) = await Sign(new { Action = "UPGRADE", Implementation });
            Assert.False(v3OnV4.Value<bool>("Success"));
            Assert.Null(v3OnV4["Signature"]?.Value<string>());

            Contract(version: 3, nonce: 0);
            var (code, upgrade) = await Sign(new { Action = "UPGRADE", Implementation });
            Assert.Equal(HttpStatusCode.OK, code);
            AssertSignedBy(upgrade, "0x7e9be30dd6e5cc5e6f9b93990bbee27dca0433f445e15dcac26d5ccc6d57ecc5", personal: true);
            var (_, v4OnV3) = await Sign(new { Action = "PROPOSE_UPGRADE", Implementation });
            Assert.False(v4OnV3.Value<bool>("Success"));
        }

        [Fact]
        public async Task NothingIsSigned_ForAnAddressWithoutCode_ARemovalOfALiveOrForeignValidator_OrADelayOutOfRange()
        {
            async Task<string?> Refusal(object request)
            {
                var (code, body) = await Sign(request);
                Assert.Equal(HttpStatusCode.BadRequest, code);
                Assert.Null(body["Signature"]?.Value<string>());
                return body.Value<string>("Error");
            }

            Assert.Contains("No contract", await Refusal(new { Action = "PROPOSE_UPGRADE", Implementation = "0x" + new string('3', 40) }));
            Assert.Contains("still a registered vBTC validator", await Refusal(new { Action = "PROPOSE_REMOVAL", Validators = new[] { _myBase }, VfxBlockHeight = 20 }));
            Assert.Contains("validator on the contract", await Refusal(new { Action = "PROPOSE_REMOVAL", Validators = new[] { "0x" + new string('4', 40) }, VfxBlockHeight = 20 }));
            Assert.Contains("repeat", await Refusal(new { Action = "PROPOSE_REMOVAL", Validators = new[] { Dark, Dark.ToUpperInvariant().Replace("0X", "0x") }, VfxBlockHeight = 20 }));
            Assert.Contains("DelaySeconds", await Refusal(new { Action = "PROPOSE_UPGRADE_DELAY", DelaySeconds = 3600 }));
            Assert.Contains("Unknown action", await Refusal(new { Action = "MINT" }));
        }
    }
}
