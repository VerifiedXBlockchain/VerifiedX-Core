using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore;
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
    /// VX-02 (CRITICAL): "Token deployment credits an attacker authored supply against another
    /// party's contract".
    ///
    /// Audit PoC: attacker B submits TokenDeploy() under a fresh tx ContractUID N with a body whose
    /// embedded SmartContractUID is the victim's existing token X, MinterAddress = B and
    /// TokenSupply = 1,000,000,000. Mined; B then held 1e9 units of X while X's state record still
    /// showed supply 1,000 owned by A. Audit control: an unmodified body (embedded UID == tx UID)
    /// credited its own UID with its own supply.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX02_TokenDeployBindingTests : IDisposable
    {
        private const string VictimTokenX = "32cd1ec89bec418da66ccc6ee54fed14:1790194628";
        private const string FreshUidN = "0a208eb1e9c9446988041c6216415d0d:1790194777";

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;

        private readonly (PrivateKey Key, string Pub, string Address) _victimA;
        private readonly (PrivateKey Key, string Pub, string Address) _attackerB;
        private readonly (PrivateKey Key, string Pub, string Address) _thirdC;

        public VX02_TokenDeployBindingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx02_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = 100 };

            _victimA = NewKey();
            _attackerB = NewKey();
            _thirdC = NewKey();

            // Victim token X already exists: state record owned by A, A holds the full 1,000 supply.
            SmartContractStateTrei.SaveSmartContract(new SmartContractStateTrei
            {
                SmartContractUID = VictimTokenX,
                ContractData = VbtcTestContracts.TokenContractData(VictimTokenX, _victimA.Address, 1000),
                MinterAddress = _victimA.Address,
                OwnerAddress = _victimA.Address,
                IsToken = true,
                TokenDetails = new TokenDetails { TokenName = "X", TokenTicker = "X", StartingSupply = 1000, CurrentSupply = 1000, ContractOwner = _victimA.Address, DecimalPlaces = 2 },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei
            {
                Key = _victimA.Address, Balance = 100M, Nonce = 0,
                TokenAccounts = new List<TokenAccount> { TokenAccount.CreateTokenAccount(VictimTokenX, "X", "X", 1000M, 2) },
            });
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _attackerB.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
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

        /// <summary>A mint/deploy TX exactly as SmartContractService.MintSmartContractTx builds it.</summary>
        private static Transaction MintTx(string function, string txUid, string body, (PrivateKey Key, string Pub, string Address) signer,
            TransactionType type = TransactionType.TKNZ_MINT, long height = 0)
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = signer.Address,
                ToAddress = signer.Address,
                Amount = 0.0M,
                Fee = 0,
                Nonce = 0,
                TransactionType = type,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = txUid, Data = body, MD5List = "NA" } }),
                Height = height,
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, signer.Key, signer.Pub);
            return tx;
        }

        private static decimal TokenBalance(string address, string scUid) =>
            StateData.GetSpecificAccountStateTrei(address)?.TokenAccounts?
                .Where(t => t.SmartContractUID == scUid).Sum(t => t.Balance) ?? 0M;

        private static async Task ApplyDeploy(Transaction tx)
        {
            var m = typeof(StateData).GetMethod("DeployTokenContract", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            await (Task)m!.Invoke(null, new object[] { tx, new Block { Height = tx.Height, StateRoot = "root" } })!;
        }

        // ── Audit PoC and control, consensus validator ─────────────────────────────────────────

        [Fact]
        public async Task VX02_AuditAttack_BodyNamesExistingToken_UnderFreshTxUid_Rejected()
        {
            var body = VbtcTestContracts.TokenContractData(VictimTokenX, _attackerB.Address, 1_000_000_000);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.False(ok);
            Assert.Contains("body UID does not match", message);
        }

        [Fact]
        public async Task VX02_BodyMinterIsAnotherAddress_Rejected()
        {
            // Body UID matches the tx, but the embedded minter (the address the supply was credited
            // to, and the TokenDetails owner) is a third party.
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _thirdC.Address, 1000);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.False(ok);
            Assert.Contains("MinterAddress does not match", message);
        }

        [Fact]
        public async Task VX02_Control_UnmodifiedBody_Accepted()
        {
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _attackerB.Address, 777);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.True(ok, message);
        }

        [Theory]
        [InlineData("TokenDeploy()", TransactionType.TKNZ_MINT)]
        [InlineData("Mint()", TransactionType.NFT_MINT)]
        [InlineData("Mint()", TransactionType.VBTC_V2_CONTRACT_CREATE)]
        public async Task VX02_FollowUp_UnsignedCreationNamingAFundedAddress_RefusedBeforeTheBodyRuns(string function, TransactionType type)
        {
            // Owner's audit review: the binding decompiled - ran in the Trillium interpreter - the submitted body before the
            // signature check, so a transaction nobody signed, naming any funded address, reached the interpreter at
            // admission. The body here would fail the binding (third-party minter): the refusal must be the signature, i.e.
            // the body never ran.
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _thirdC.Address, 1000);
            var tx = MintTx(function, FreshUidN, body, _victimA, type);

            tx.Signature = "forged";                                             // the hash still matches the content
            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Equal("Signature Failed to verify.", message);

            tx.Signature = null;
            (ok, message) = await TransactionValidatorService.VerifyTX(tx);
            Assert.False(ok);
            Assert.Equal("Signature cannot be null.", message);
        }

        [Fact]
        public async Task VX02_SupplyAboveDocumentedBound_Rejected()
        {
            // Above int.MaxValue the decompiler cannot even read the body (Token.TotalSupply is int).
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _attackerB.Address, SmartContractDeployBinding.MaxTokenSupply + 1);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.False(ok);
        }

        [Fact]
        public async Task VX02_SupplyAtDocumentedBound_Accepted()
        {
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _attackerB.Address, SmartContractDeployBinding.MaxTokenSupply);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.True(ok, message);
        }

        [Fact]
        public async Task VX02_NegativeSupply_Rejected()
        {
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _attackerB.Address, -5);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.False(ok);
            Assert.Contains("Token supply must be between", message);
        }

        [Fact]
        public async Task VX02_UndecompilableBody_Rejected()
        {
            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, "bm90LWEtY29udHJhY3Q=", _attackerB));

            Assert.False(ok);
            Assert.Contains("could not be decompiled", message);
        }

        [Fact]
        public async Task VX02_ObjectShapedPayload_IsBoundToo()
        {
            // The validator and dispatcher also accept a single-object payload; it must not be a bypass.
            var body = VbtcTestContracts.TokenContractData(VictimTokenX, _attackerB.Address, 1_000_000_000);
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(), FromAddress = _attackerB.Address, ToAddress = _attackerB.Address,
                Amount = 0M, Fee = 0, Nonce = 0, TransactionType = TransactionType.TKNZ_MINT,
                Data = JsonConvert.SerializeObject(new { Function = "TokenDeploy()", ContractUID = FreshUidN, Data = body, MD5List = "NA" }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = VerifiedXCore.Services.SignatureService.CreateSignature(tx.Hash, _attackerB.Key, _attackerB.Pub);

            var (ok, message) = await TransactionValidatorService.VerifyTX(tx);

            Assert.False(ok);
            Assert.Contains("body UID does not match", message);
        }

        // ── Mint() and vBTC V2 create carry a body through the same dispatcher ─────────────────

        [Fact]
        public async Task VX02_Mint_BodyUidMismatch_Rejected()
        {
            var body = VbtcTestContracts.BuildContractData(VictimTokenX, _attackerB.Address, null);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("Mint()", FreshUidN, body, _attackerB, TransactionType.NFT_MINT));

            Assert.False(ok);
            Assert.Contains("body UID does not match", message);
        }

        [Fact]
        public async Task VX02_Mint_Control_UnmodifiedBody_Accepted()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _attackerB.Address, null);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("Mint()", FreshUidN, body, _attackerB, TransactionType.NFT_MINT));

            Assert.True(ok, message);
        }

        [Fact]
        public async Task VX02_VbtcV2Create_BodyMinterMismatch_Rejected()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _thirdC.Address, null);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("Mint()", FreshUidN, body, _attackerB, TransactionType.VBTC_V2_CONTRACT_CREATE));

            Assert.False(ok);
            Assert.Contains("MinterAddress does not match", message);
        }

        // ── Follow-up found by the AUDIT-PREP replay scan (testnet heights 71287-71340) ──────────

        /// <summary>The shape of the four historical testnet NFTs: a description with line breaks and emoji.</summary>
        private const string LegacyDescription = "Get Your Martian. Join the Rebellion.\n\nClaim your identity \U0001F30C\n\U0001F331 Unlock merch";

        [Fact]
        public void VX02_LegacyBody_ReallyCannotBeDecompiled()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _attackerB.Address, null, description: LegacyDescription);
            Assert.ThrowsAny<Exception>(() => VerifiedXCore.Models.SmartContracts.SmartContractMain.GenerateSmartContractInMemory(body));
            Assert.Equal((FreshUidN, _attackerB.Address), SmartContractDeployBinding.ReadDeclaredIdentity(body));
        }

        [Fact]
        public async Task VX02_Mint_UndecompilableLegacyBody_WithMatchingDeclaredIdentity_Accepted()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _attackerB.Address, null, description: LegacyDescription);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("Mint()", FreshUidN, body, _attackerB, TransactionType.NFT_MINT));

            Assert.True(ok, message);
        }

        [Fact]
        public async Task VX02_Mint_UndecompilableBody_WithForeignDeclaredMinter_Rejected()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _thirdC.Address, null, description: LegacyDescription);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("Mint()", FreshUidN, body, _attackerB, TransactionType.NFT_MINT));

            Assert.False(ok);
            Assert.Contains("MinterAddress does not match", message);
        }

        [Fact]
        public async Task VX02_TokenDeploy_UndecompilableBody_StillRejected()
        {
            var body = VbtcTestContracts.BuildContractData(FreshUidN, _attackerB.Address, null, description: LegacyDescription);

            var (ok, message) = await TransactionValidatorService.VerifyTX(MintTx("TokenDeploy()", FreshUidN, body, _attackerB));

            Assert.False(ok);
            Assert.Contains("could not be decompiled", message);
        }

        // ── StateData apply ────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task VX02_Apply_MismatchedBody_CreditsOnlyTheTransactionIdentity()
        {
            // Even if such a TX were ever mined, the apply credits the tx sender under the tx UID —
            // never the body's UID (the victim's token) or the body's minter.
            var body = VbtcTestContracts.TokenContractData(VictimTokenX, _thirdC.Address, 1_000_000_000);
            var tx = MintTx("TokenDeploy()", FreshUidN, body, _attackerB, height: 73);

            await ApplyDeploy(tx);

            Assert.Equal(1000M, TokenBalance(_victimA.Address, VictimTokenX));   // victim untouched
            Assert.Equal(0M, TokenBalance(_attackerB.Address, VictimTokenX));    // no inflation of X
            Assert.Equal(0M, TokenBalance(_thirdC.Address, VictimTokenX));
            Assert.Null(StateData.GetSpecificAccountStateTrei(_thirdC.Address)); // body minter never credited
            Assert.Equal(1_000_000_000M, TokenBalance(_attackerB.Address, FreshUidN)); // own UID only

            var rec = SmartContractStateTrei.GetSmartContractState(FreshUidN);
            Assert.NotNull(rec);
            Assert.Equal(_attackerB.Address, rec!.OwnerAddress);
            Assert.Equal(_attackerB.Address, rec.TokenDetails!.ContractOwner);
            Assert.Equal(_victimA.Address, SmartContractStateTrei.GetSmartContractState(VictimTokenX)!.OwnerAddress);
        }

        [Fact]
        public async Task VX02_Apply_Control_LegitimateDeploy_CreditsDeployer()
        {
            var body = VbtcTestContracts.TokenContractData(FreshUidN, _attackerB.Address, 777);

            await ApplyDeploy(MintTx("TokenDeploy()", FreshUidN, body, _attackerB, height: 91));

            Assert.Equal(777M, TokenBalance(_attackerB.Address, FreshUidN));
            Assert.Equal(_attackerB.Address, SmartContractStateTrei.GetSmartContractState(FreshUidN)!.TokenDetails!.ContractOwner);
        }

        [Fact]
        public async Task VX02_Apply_NewAccount_IsKeyedOnSender_NotToAddress()
        {
            var fresh = NewKey(); // no AccountStateTrei row yet
            var body = VbtcTestContracts.TokenContractData(FreshUidN, fresh.Item3, 50);
            var tx = MintTx("TokenDeploy()", FreshUidN, body, fresh, height: 5);

            await ApplyDeploy(tx);

            Assert.Equal(50M, TokenBalance(fresh.Item3, FreshUidN));
        }

        [Fact]
        public async Task VX02_Apply_MalformedPayload_DoesNotThrow()
        {
            var tx = new Transaction { FromAddress = _attackerB.Address, ToAddress = _attackerB.Address, Data = "{not json", Hash = "h" };
            await ApplyDeploy(tx); // must not throw: UpdateTreis now awaits this handler
            Assert.Null(SmartContractStateTrei.GetSmartContractState(FreshUidN));
        }
    }
}
