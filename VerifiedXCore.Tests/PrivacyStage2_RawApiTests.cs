using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VerifiedXCore;
using VerifiedXCore.Controllers;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.Privacy;
using VerifiedXCore.Privacy;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Fund-loss audit item 1, stage 2: the raw shielded-VFX API for wallets that hold their own keys (web wallet and
    /// integrators). The node builds and proves; the caller signs a shield's hash or just submits a ZK-authorised spend.
    /// Proof-bearing tests need the VXPLNK03 params (VFX_PLONK_PARAMS, %LOCALAPPDATA%/RBX/PlonkParams, or the repository's
    /// VerifiedXCore/DBs/PlonkParams) and return early without them; the key, scan, cache and status tests always run.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PrivacyStage2_RawApiTests : IDisposable
    {
        private const long Height = 1000;
        private const string SeedHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40";
        private static readonly object ParamsLock = new();
        private static bool? _paramsLoaded;

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorHeight, _priorSupplyGate;
        private readonly Dictionary<string, decimal> _priorCorrections;
        private readonly ShieldedKeyMaterial _alice;
        private readonly ShieldedKeyMaterial _bob;
        private readonly (PrivateKey Key, string Pub, string Address) _funder;

        public PrivacyStage2_RawApiTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ps2raw_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            _priorLastBlock = Globals.LastBlock;
            _priorHeight = Globals.PrivateTxProofRulesHeight;
            _priorSupplyGate = Globals.PrivateTxSupplyRulesHeight;
            _priorCorrections = new(Globals.ShieldedSupplyCorrections, StringComparer.Ordinal);
            Globals.ShieldedSupplyCorrections.Clear();
            Globals.PrivateTxProofRulesHeight = Height;
            Globals.PrivateTxSupplyRulesHeight = 1;
            Globals.LastBlock = new Block { Height = Height, Timestamp = 1_700_000_000 };
            Globals.MempoolNullifiers.Clear();
            PrivateRawTxService.ClearPending();
            DbContext.Initialize();
            PrivacyEpochService.ResetPools(DbContext.DB_Privacy, Height, 1_700_000_000);
            _alice = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 21);
            _bob = ShieldedHdDerivation.DeriveShieldedKeyMaterial(SeedHex, ShieldedAddressConstants.DefaultBip44CoinType, 22);
            _funder = NewKey();
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _funder.Address, Balance = 1000M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.MempoolNullifiers.Clear();
            PrivateRawTxService.ClearPending();
            Globals.LastBlock = _priorLastBlock;
            Globals.PrivateTxProofRulesHeight = _priorHeight;
            Globals.PrivateTxSupplyRulesHeight = _priorSupplyGate;
            Globals.ShieldedSupplyCorrections.Clear();
            foreach (var (k, v) in _priorCorrections) Globals.ShieldedSupplyCorrections[k] = v;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

        private static string? FindParams()
        {
            var env = Environment.GetEnvironmentVariable("VFX_PLONK_PARAMS");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RBX", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
            if (File.Exists(local)) return local;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "VerifiedXCore", "DBs", "PlonkParams", PLONKParamsDownloader.ParamsFileName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static bool ParamsLoaded()
        {
            lock (ParamsLock)
            {
                if (_paramsLoaded.HasValue) return _paramsLoaded.Value;
                var path = FindParams();
                if (path == null)
                {
                    Console.WriteLine("PrivacyStage2_RawApiTests: no VXPLNK03 params found (set VFX_PLONK_PARAMS); proof tests skipped.");
                    _paramsLoaded = false;
                    return false;
                }
                _paramsLoaded = PLONKSetup.TryLoadParamsFile(path) && PrivateTxProverV1.CanProve && PlonkProofVerifier.VerifierAvailable();
                return _paramsLoaded.Value;
            }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static LiteDB.LiteDatabase Db => DbContext.DB_Privacy;
        private static string B64(byte[] b) => Convert.ToBase64String(b);

        /// <summary>Mines a block holding <paramref name="txs"/> at <paramref name="height"/>: ledger apply + stored block + tip.</summary>
        private static async Task Mine(long height, params Transaction[] txs)
        {
            var block = new Block { Height = height, StateRoot = "root", Timestamp = 1_700_000_000 + height, Transactions = txs.ToList(), Hash = $"h{height}" };
            foreach (var tx in txs)
            {
                await PrivateTxLedgerService.ApplyBlockTransactionAsync(tx, block, Db);
                MempoolNullifierTracker.ReleaseClaimsForTxHash(tx.Hash);
            }
            BlockchainData.GetBlocks().InsertSafe(block);
            Globals.LastBlock = block;
        }

        private static (bool ok, string message) Consensus(Transaction tx, long height)
        {
            Assert.True(PrivateTxPayloadCodec.TryDecode(tx.Data, out var payload, out var err), err);
            var shape = PrivateTxProofRules.Check(tx, payload!, height);
            if (shape != null) return (false, shape);
            return PlonkProofVerifier.TryValidatePrivateProofs(tx, payload!, false, height);
        }

        private static JObject Json(string s) => JObject.Parse(s);

        /// <summary>A shield to <paramref name="recipient"/> built through the raw path, signed by the funder, verified and mined.</summary>
        private async Task<Transaction> RawShieldMined(ShieldedKeyMaterial recipient, decimal amount, long mineAt)
        {
            var build = PrivateRawTxService.BuildShield(_funder.Address, recipient.ZfxAddress, amount, null, null, Db, mineAt);
            Assert.True(build.Success, build.Message);
            var sig = SignatureService.CreateSignature(build.Hash!, _funder.Key, _funder.Pub);
            var verify = await PrivateRawTxService.VerifyAsync(build.Hash, sig);
            Assert.True(verify.ok, verify.message);
            Assert.True(PrivateRawTxService.TryGetPending(build.Hash, out var tx));
            await Mine(mineAt, tx!);
            PrivateRawTxService.ClearPending();
            return tx!;
        }

        // ── Keys ──────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ViewingKey_ParsesBase64AndHex_AndMustBelongToTheAddress()
        {
            var vk = _alice.ViewingKey32;
            Assert.True(PrivateRawTxService.TryParseKey32(B64(vk), out var fromB64, out _));
            Assert.True(PrivateRawTxService.TryParseKey32(Convert.ToHexString(vk), out var fromHex, out _));
            Assert.True(PrivateRawTxService.TryParseKey32("0x" + Convert.ToHexString(vk).ToLowerInvariant(), out var fromHex0x, out _));
            Assert.Equal(vk, fromB64);
            Assert.Equal(vk, fromHex);
            Assert.Equal(vk, fromHex0x);
            Assert.False(PrivateRawTxService.TryParseKey32("", out _, out var e1));
            Assert.False(PrivateRawTxService.TryParseKey32("not a key", out _, out var e2));
            Assert.False(PrivateRawTxService.TryParseKey32(B64(new byte[16]), out _, out var e3));
            Assert.All(new[] { e1, e2, e3 }, e => Assert.False(string.IsNullOrEmpty(e)));

            // The derived encryption key must be the one the zfx address encodes: Bob's key cannot act for Alice's address.
            Assert.True(PrivateRawTxService.TryKeyMaterial(_alice.ZfxAddress, vk, out var keys, out _));
            Assert.Equal(_alice.EncryptionPrivateKey32, keys!.EncryptionPrivateKey32);
            Assert.Equal(_alice.EncryptionPublicKey33, keys.EncryptionPublicKey33);
            Assert.Empty(keys.SpendingKey32);
            Assert.False(PrivateRawTxService.TryKeyMaterial(_alice.ZfxAddress, _bob.ViewingKey32, out _, out var wrong));
            Assert.Contains("does not belong", wrong);
            Assert.False(PrivateRawTxService.TryKeyMaterial("zfx_nonsense", vk, out _, out _));
            Assert.False(PrivateRawTxService.TryKeyMaterial(_alice.ZfxAddress, new byte[31], out _, out _));
        }

        // ── Status ────────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task PrivacyApi_GetPlonkStatus_ReportsTheStageTwoFields()
        {
            var json = Json(await new PrivacyV1Controller().GetPlonkStatus());
            Assert.True(json["Success"]!.Value<bool>());
            var r = json["Result"]!;
            foreach (var field in new[] { "VfxPi2VerifyAvailable", "V1ProvingAvailable", "CircuitPoseidonAvailable", "ProofRulesHeight", "ProofRulesActive", "CapVerifyV1", "CapProveV1" })
                Assert.NotNull(r[field]);
            Assert.Equal(Height, r["ProofRulesHeight"]!.Value<long>());
            Assert.True(r["ProofRulesActive"]!.Value<bool>()); // tip is Height, so the next block is in the epoch
            Assert.Equal(PoseidonV1.IsAvailable, r["CircuitPoseidonAvailable"]!.Value<bool>());
        }

        // ── Pending cache ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SendAndVerify_RefuseUnknownHashes_AndAShieldWithoutASignature()
        {
            var send = await PrivateRawTxService.SendAsync("nothing", "sig");
            Assert.False(send.ok);
            Assert.Contains("No pending raw private transaction", send.message);
            var verify = await PrivateRawTxService.VerifyAsync("", null);
            Assert.False(verify.ok);

            var ctrl = new PrivacyV1Controller();
            var j = Json(await ctrl.SendRawPrivateTx(new RawPrivateTxSubmission { Hash = "" }));
            Assert.False(j["Success"]!.Value<bool>());
            j = Json(await ctrl.SendRawPrivateTx(new RawPrivateTxSubmission { Hash = "unknown" }));
            Assert.False(j["Success"]!.Value<bool>());
            Assert.Contains("No pending", j["Message"]!.Value<string>());

            if (!ParamsLoaded()) return;
            var build = PrivateRawTxService.BuildShield(_funder.Address, _alice.ZfxAddress, 2M, null, null, Db, Height + 1);
            Assert.True(build.Success, build.Message);
            Assert.True(build.RequiresSignature);
            var noSig = await PrivateRawTxService.SendAsync(build.Hash, null);
            Assert.False(noSig.ok);
            Assert.Contains("signature", noSig.message, StringComparison.OrdinalIgnoreCase);
            var badSig = await PrivateRawTxService.SendAsync(build.Hash, "MEUCIQDnotarealsignature");
            Assert.False(badSig.ok);
            // The build survives a failed submit, so the corrected retry works.
            Assert.True(PrivateRawTxService.TryGetPending(build.Hash, out _));
        }

        [Fact]
        public void BuildShield_RefusesBadInputs_WithoutTouchingTheProver()
        {
            Assert.False(PrivateRawTxService.BuildShield("", _alice.ZfxAddress, 1M, null, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildShield("RBXnotanaddress", _alice.ZfxAddress, 1M, null, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildShield(_funder.Address, "zfx_bad", 1M, null, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildShield(_funder.Address, _alice.ZfxAddress, 0M, null, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildShield(_funder.Address, _alice.ZfxAddress, 1M, null, -1M, Db, Height + 1).Success);
            var reserve = "xRBX" + _funder.Address.Substring(4);
            Assert.Contains("xRBX", PrivateRawTxService.BuildShield(reserve, _alice.ZfxAddress, 1M, null, null, Db, Height + 1).Message);
        }

        [Fact]
        public void BuildSpends_RefuseBadInputs_BeforeScanningOrProving()
        {
            var vk = _alice.ViewingKey32;
            Assert.False(PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, vk, "", 1M, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, vk, _funder.Address, 0M, null, Db, Height + 1).Success);
            Assert.Contains("xRBX", PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, vk, "xRBX" + _funder.Address.Substring(4), 1M, null, Db, Height + 1).Message);
            Assert.Contains("does not belong", PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, _bob.ViewingKey32, _funder.Address, 1M, null, Db, Height + 1).Message);
            Assert.False(PrivateRawTxService.BuildPrivateTransfer(_alice.ZfxAddress, vk, "zfx_bad", 1M, null, Db, Height + 1).Success);
            Assert.False(PrivateRawTxService.BuildPrivateTransfer(_alice.ZfxAddress, vk, _bob.ZfxAddress, 0M, null, Db, Height + 1).Success);
            // Nothing shielded yet: an honest "no notes" answer, not a prover error.
            var none = PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, vk, _funder.Address, 1M, null, Db, Height + 1);
            Assert.False(none.Success);
            Assert.Contains("No shielded VFX notes", none.Message);
        }

        // ── Scan (no params needed: the empty pool and the key binding) ───────────────────────────────────────

        [Fact]
        public void ScanNotes_OnAnEmptyEpochPool_IsEmptyAndDefaultsToTheEpochRange()
        {
            var r = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            Assert.True(r.Success, r.Message);
            Assert.Empty(r.Notes);
            Assert.Equal(Height, r.FromHeight);   // the epoch's first block
            Assert.Equal(Height, r.ToHeight);     // the tip
            Assert.True(r.ProofRulesActive);
            Assert.Equal(0M, r.UnspentBalance);

            var wrongKey = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _bob.ViewingKey32, null, null, false, Db);
            Assert.False(wrongKey.Success);
            Assert.Contains("does not belong", wrongKey.Message);
        }

        // ── Full raw flows (real proofs) ──────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task RawShield_BuildSignSubmit_ThenTheViewingKeyFindsTheNote()
        {
            if (!ParamsLoaded()) return;

            var build = PrivateRawTxService.BuildShield(_funder.Address, _alice.ZfxAddress, 5M, "web wallet", null, Db, Height + 1);
            Assert.True(build.Success, build.Message);
            Assert.True(build.RequiresSignature);
            Assert.NotNull(build.Transaction);
            Assert.Equal(TransactionType.VFX_SHIELD, build.Transaction!.TransactionType);
            Assert.Equal(_funder.Address, build.Transaction.FromAddress);
            Assert.Equal("", build.Transaction.Signature);
            Assert.True(build.Fee > 0);
            PrivateTxPayloadCodec.TryDecode(build.Transaction.Data, out var payload, out _);
            Assert.NotNull(payload!.ProofB64);                                   // proven by the node
            Assert.True(Consensus(build.Transaction, Height + 1).ok);

            // The caller signs the Hash with the transparent key and submits: the node's full verifier accepts it.
            var sig = SignatureService.CreateSignature(build.Hash!, _funder.Key, _funder.Pub);
            var sent = await PrivateRawTxService.SendAsync(build.Hash, sig);
            Assert.True(sent.ok, sent.message);
            Assert.Equal(build.Hash, sent.hash);
            Assert.NotNull(TransactionData.GetPool().FindOne(x => x.Hash == build.Hash));
            // Claimed once: a second submit of the same hash is refused.
            var again = await PrivateRawTxService.SendAsync(build.Hash, sig);
            Assert.False(again.ok);

            // Mined: the recipient finds the note with its viewing key alone, with position and randomness, spendable.
            await Mine(Height + 1, build.Transaction);
            var scan = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            Assert.True(scan.Success, scan.Message);
            var note = Assert.Single(scan.Notes);
            Assert.Equal(5M, note.Amount);
            Assert.Equal(payload.Outs[0].CommitmentB64, note.Commitment);
            Assert.Equal(1, note.TreePosition);   // leaf 0 is the dummy
            Assert.Equal(Height + 1, note.BlockHeight);
            Assert.Equal(build.Hash, note.TxHash);
            Assert.True(note.Spendable, note.Reason);
            Assert.False(note.Spent);
            Assert.False(note.PendingSpend);
            Assert.Equal(5M, scan.SpendableBalance);
            Assert.True(PrivacyField.IsCanonicalLe(Convert.FromBase64String(note.RandomnessB64)));

            // Bob's key opens nothing of Alice's.
            var bob = PrivateRawTxService.ScanNotes(_bob.ZfxAddress, _bob.ViewingKey32, null, null, false, Db);
            Assert.True(bob.Success);
            Assert.Empty(bob.Notes);
        }

        [Fact]
        public async Task RawUnshield_NodeSelectsProvesAndTheCallerJustSubmits()
        {
            if (!ParamsLoaded()) return;
            await RawShieldMined(_alice, 5M, Height + 1);
            var payout = NewKey().Address;

            var build = PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, _alice.ViewingKey32, payout, 3M, null, Db, Height + 2);
            Assert.True(build.Success, build.Message);
            Assert.False(build.RequiresSignature);
            Assert.Equal(TransactionType.VFX_UNSHIELD, build.Transaction!.TransactionType);
            Assert.Equal(Globals.PrivateTxFixedFee, build.Fee);
            Assert.Single(build.SpentCommitments);
            Assert.Equal(5M - 3M - Globals.PrivateTxFixedFee, build.ChangeAmount);
            Assert.NotNull(build.MerkleRoot);
            PrivateTxPayloadCodec.TryDecode(build.Transaction.Data, out var payload, out _);
            Assert.Equal(2, payload!.NullsB64.Count);
            Assert.True(PrivacyEpoch.IsDummyNullifier(payload.NullsB64[1]));
            Assert.True(Consensus(build.Transaction, Height + 2).ok);

            // A signature is not needed and is ignored; the dry run and the submit both pass the node's verifier.
            var verify = await PrivateRawTxService.VerifyAsync(build.Hash, null);
            Assert.True(verify.ok, verify.message);
            var sent = await PrivateRawTxService.SendAsync(build.Hash, "ignored");
            Assert.True(sent.ok, sent.message);
            Assert.Equal(PrivacyConstants.PlonkSignatureSentinel, build.Transaction.Signature);

            // While it sits in the mempool the note shows as pending and cannot be chosen again.
            var pending = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            var note = Assert.Single(pending.Notes);
            Assert.True(note.PendingSpend);
            Assert.False(note.Spendable);
            Assert.Equal(0M, pending.SpendableBalance);
            var blocked = PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, _alice.ViewingKey32, payout, 1M, null, Db, Height + 2);
            Assert.False(blocked.Success);
            Assert.Contains("No spendable", blocked.Message);

            // Mined: the spent note is gone from the default view, visible with IncludeSpent, and the change is spendable.
            await Mine(Height + 2, build.Transaction);
            var after = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            var change = Assert.Single(after.Notes);
            Assert.Equal(build.ChangeAmount, change.Amount);
            Assert.True(change.Spendable, change.Reason);
            var withSpent = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, true, Db);
            Assert.Equal(2, withSpent.Notes.Count);
            Assert.Contains(withSpent.Notes, n => n.Spent && n.Amount == 5M);
            Assert.Equal(build.ChangeAmount, withSpent.UnspentBalance);
        }

        [Fact]
        public async Task RawTransfer_WithExplicitInputs_PaysBob_WhoCanSpendWithHisOwnKey()
        {
            if (!ParamsLoaded()) return;
            var s1 = await RawShieldMined(_alice, 2M, Height + 1);
            var s2 = await RawShieldMined(_alice, 4M, Height + 2);
            PrivateTxPayloadCodec.TryDecode(s1.Data, out var p1, out _);
            PrivateTxPayloadCodec.TryDecode(s2.Data, out var p2, out _);
            var inputs = new List<string> { p1!.Outs[0].CommitmentB64, p2!.Outs[0].CommitmentB64 };

            // Wrong explicit inputs are named, not silently replaced.
            var unknown = PrivateRawTxService.BuildPrivateTransfer(_alice.ZfxAddress, _alice.ViewingKey32, _bob.ZfxAddress, 1M, new List<string> { "AAAA" }, Db, Height + 3);
            Assert.False(unknown.Success);
            Assert.Contains("not a note of this address", unknown.Message);
            var tooLittle = PrivateRawTxService.BuildPrivateTransfer(_alice.ZfxAddress, _alice.ViewingKey32, _bob.ZfxAddress, 5M, new List<string> { inputs[0] }, Db, Height + 3);
            Assert.False(tooLittle.Success);
            Assert.Contains("is needed", tooLittle.Message);

            var build = PrivateRawTxService.BuildPrivateTransfer(_alice.ZfxAddress, _alice.ViewingKey32, _bob.ZfxAddress, 5M, inputs, Db, Height + 3);
            Assert.True(build.Success, build.Message);
            Assert.Equal(TransactionType.VFX_PRIVATE_TRANSFER, build.Transaction!.TransactionType);
            Assert.Equal(2, build.SpentCommitments.Count);
            Assert.Equal(6M - 5M - Globals.PrivateTxFixedFee, build.ChangeAmount);
            Assert.True(Consensus(build.Transaction, Height + 3).ok);
            var sent = await PrivateRawTxService.SendAsync(build.Hash, null);
            Assert.True(sent.ok, sent.message);
            await Mine(Height + 3, build.Transaction);

            // Bob sees 5 VFX with his viewing key and unshields through the raw path with no node-side wallet row.
            var bobNotes = PrivateRawTxService.ScanNotes(_bob.ZfxAddress, _bob.ViewingKey32, null, null, false, Db);
            var paid = Assert.Single(bobNotes.Notes);
            Assert.Equal(5M, paid.Amount);
            Assert.True(paid.Spendable, paid.Reason);
            var bobOut = PrivateRawTxService.BuildUnshield(_bob.ZfxAddress, _bob.ViewingKey32, NewKey().Address, 4M, null, Db, Height + 4);
            Assert.True(bobOut.Success, bobOut.Message);
            Assert.True(Consensus(bobOut.Transaction!, Height + 4).ok);

            // Alice keeps only the change, and Alice's key cannot build with Bob's note.
            var alice = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            Assert.Equal(build.ChangeAmount, Assert.Single(alice.Notes).Amount);
            var steal = PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, _alice.ViewingKey32, NewKey().Address, 1M, new List<string> { paid.Commitment }, Db, Height + 4);
            Assert.False(steal.Success);
        }

        [Fact]
        public async Task RawRoutes_ThroughTheController_ReturnTheDocumentedShapes()
        {
            if (!ParamsLoaded()) return;
            var ctrl = new PrivacyV1Controller();

            var shield = Json(await ctrl.GetRawShieldTxData(new RawShieldVfxRequest { FromAddress = _funder.Address, RecipientZfxAddress = _alice.ZfxAddress, ShieldAmount = 3M }));
            Assert.True(shield["Success"]!.Value<bool>(), shield.ToString());
            var hash = shield["Result"]!["Hash"]!.Value<string>()!;
            Assert.True(shield["Result"]!["RequiresSignature"]!.Value<bool>());
            Assert.NotNull(shield["Result"]!["Transaction"]);
            Assert.NotNull(shield["Result"]!["ExpiresUtc"]);

            var sig = SignatureService.CreateSignature(hash, _funder.Key, _funder.Pub);
            var verify = Json(await ctrl.VerifyRawPrivateTx(new RawPrivateTxSubmission { Hash = hash, Signature = sig }));
            Assert.True(verify["Success"]!.Value<bool>(), verify.ToString());
            var send = Json(await ctrl.SendRawPrivateTx(new RawPrivateTxSubmission { Hash = hash, Signature = sig }));
            Assert.True(send["Success"]!.Value<bool>(), send.ToString());
            Assert.Equal(hash, send["Result"]!["Hash"]!.Value<string>());
            Assert.True(PrivateRawTxService.TryGetPending(hash, out _) == false);

            var tx = TransactionData.GetPool().FindOne(x => x.Hash == hash);
            Assert.NotNull(tx);
            await Mine(Height + 1, tx!);

            var notes = Json(await ctrl.GetShieldedNotesRaw(new RawShieldedNotesRequest { ZfxAddress = _alice.ZfxAddress, ViewingKey = Convert.ToHexString(_alice.ViewingKey32) }));
            Assert.True(notes["Success"]!.Value<bool>(), notes.ToString());
            Assert.Equal(3M, notes["Result"]!["SpendableBalance"]!.Value<decimal>());
            var badKey = Json(await ctrl.GetShieldedNotesRaw(new RawShieldedNotesRequest { ZfxAddress = _alice.ZfxAddress, ViewingKey = "zzz" }));
            Assert.False(badKey["Success"]!.Value<bool>());

            var unshield = Json(await ctrl.GetRawUnshieldTxData(new RawUnshieldVfxRequest { ZfxAddress = _alice.ZfxAddress, ViewingKey = B64(_alice.ViewingKey32), TransparentToAddress = NewKey().Address, TransparentAmount = 1M }));
            Assert.True(unshield["Success"]!.Value<bool>(), unshield.ToString());
            Assert.False(unshield["Result"]!["RequiresSignature"]!.Value<bool>());
            var uHash = unshield["Result"]!["Hash"]!.Value<string>()!;
            var uSend = Json(await ctrl.SendRawPrivateTx(new RawPrivateTxSubmission { Hash = uHash }));
            Assert.True(uSend["Success"]!.Value<bool>(), uSend.ToString());

            var transfer = Json(await ctrl.GetRawPrivateTransferTxData(new RawPrivateTransferVfxRequest { ZfxAddress = _alice.ZfxAddress, ViewingKey = B64(_alice.ViewingKey32), RecipientZfxAddress = _bob.ZfxAddress, PaymentAmount = 1M }));
            Assert.False(transfer["Success"]!.Value<bool>()); // the only note is pending in the mempool
            Assert.Contains("No spendable", transfer["Message"]!.Value<string>());
        }

        [Fact]
        public async Task BeforeTheEpoch_TheRawPathStillBuildsLegacyTransactions()
        {
            // Pre-epoch regime: dynamic tree, legacy nullifiers, optional stub proofs, no params needed.
            Globals.PrivateTxProofRulesHeight = 999_999_999_999;
            Globals.LastBlock = new Block { Height = 10, Timestamp = 1_700_000_000 };
            DbContext.DB_Privacy.GetCollection<CommitmentRecord>(PrivacyDbContext.PRIV_COMMITMENTS).DeleteAll();
            DbContext.DB_Privacy.GetCollection<MerkleTreeNodeRecord>(PrivacyDbContext.PRIV_MERKLE_NODES).DeleteAll();
            DbContext.DB_Privacy.GetCollection<ShieldedPoolState>(PrivacyDbContext.PRIV_POOL_STATE).DeleteAll();

            var build = PrivateRawTxService.BuildShield(_funder.Address, _alice.ZfxAddress, 2M, null, null, Db, 11);
            Assert.True(build.Success, build.Message);
            var sig = SignatureService.CreateSignature(build.Hash!, _funder.Key, _funder.Pub);
            var sent = await PrivateRawTxService.SendAsync(build.Hash, sig);
            Assert.True(sent.ok, sent.message);
            await Mine(11, build.Transaction!);

            var scan = PrivateRawTxService.ScanNotes(_alice.ZfxAddress, _alice.ViewingKey32, null, null, false, Db);
            Assert.True(scan.Success, scan.Message);
            Assert.Equal(0, scan.FromHeight); // no epoch: from genesis, by the pool index
            Assert.False(scan.ProofRulesActive);
            var note = Assert.Single(scan.Notes);
            Assert.True(note.Spendable, note.Reason);

            var unshield = PrivateRawTxService.BuildUnshield(_alice.ZfxAddress, _alice.ViewingKey32, NewKey().Address, 1M, null, Db, 12);
            Assert.True(unshield.Success, unshield.Message);
            PrivateTxPayloadCodec.TryDecode(unshield.Transaction!.Data, out var payload, out _);
            Assert.Single(payload!.NullsB64); // legacy shape: one real nullifier, no dummy
            var verify = await PrivateRawTxService.VerifyAsync(unshield.Hash, null);
            Assert.True(verify.ok, verify.message);
        }
    }
}
