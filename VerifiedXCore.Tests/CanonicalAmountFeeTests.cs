using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
    /// From Globals.CanonicalInputHeight a transaction's Amount and Fee each have one accepted spelling, the shortest
    /// decimal with at least one digit after the point, on admission and in blocks, and its carried hash is the hash of
    /// the values it carries. Below the height nothing changes.
    /// </summary>
    [Collection("DbContextSequential")]
    public class CanonicalAmountFeeTests : IDisposable
    {
        private const long Rule = 101; // the next block's height: the tip is 100

        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly Block _priorLastBlock;
        private readonly long _priorRule = Globals.CanonicalInputHeight;
        private readonly (PrivateKey Key, string Pub, string Address) _sender = NewKey();
        private readonly (PrivateKey Key, string Pub, string Address) _receiver = NewKey();

        public CanonicalAmountFeeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"canonical_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            _priorLastBlock = Globals.LastBlock;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            MempoolEvictionUtility.InvalidateAdmissionStats();
            Globals.LastBlock = new Block { Height = Rule - 1 };
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _sender.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            Globals.CanonicalInputHeight = _priorRule;
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

        private static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>A transfer hashed and signed over the Amount and Fee it carries, each spelled as given.</summary>
        private Transaction Signed(string amount, string fee = "0.000006")
        {
            var tx = new Transaction
            {
                Timestamp = TimeUtil.GetTime(),
                FromAddress = _sender.Address,
                ToAddress = _receiver.Address,
                Amount = Dec(amount),
                Fee = Dec(fee),
                Nonce = 0,
                TransactionType = TransactionType.TX,
            };
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _sender.Key, _sender.Pub);
            return tx;
        }

        private static Task<(bool, string)> Admit(Transaction tx) => TransactionValidatorService.VerifyTX(tx);

        private static Task<(bool, string)> InABlock(Transaction tx, long height) =>
            TransactionValidatorService.VerifyTX(tx, false, true, false, new Dictionary<string, long>(), false, height, tx.Timestamp + 10);

        // ── The spelling ─────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1.0", true)]
        [InlineData("1.5", true)]
        [InlineData("0.00002", true)]
        [InlineData("0.000006", true)]
        [InlineData("0.0", true)]
        [InlineData("10.0", true)]
        [InlineData("123.456", true)]
        [InlineData("-3.0", true)]
        [InlineData("1", false)]
        [InlineData("1.00", false)]
        [InlineData("0", false)]
        [InlineData("0.00", false)]
        [InlineData("10", false)]
        [InlineData("1.50", false)]
        [InlineData("0.00000600", false)]
        // Exponent forms read with the scale their digits give: 1E0 is "1", 10E-1 is "1.0", 100E-2 is "1.00".
        [InlineData("1E0", false)]
        [InlineData("10E-1", true)]
        [InlineData("1.0E0", true)]
        [InlineData("100E-2", false)]
        [InlineData("6E-6", true)]
        [InlineData("600E-8", false)]
        public void A_decimal_is_canonical_when_it_is_the_shortest_spelling_with_one_place(string text, bool canonical)
        {
            var value = Dec(text);

            Assert.Equal(canonical, LedgerIntegrityRules.IsCanonicalDecimal(value));
        }

        [Theory]
        [InlineData("1", "1.0")]
        [InlineData("1.00", "1.0")]
        [InlineData("0", "0.0")]
        [InlineData("0.00", "0.0")]
        [InlineData("1.50", "1.5")]
        [InlineData("0.00000600", "0.000006")]
        [InlineData("123456789", "123456789.0")]
        [InlineData("0.00002", "0.00002")]
        public void The_canonical_spelling_keeps_the_value(string text, string expected)
        {
            var canonical = LedgerIntegrityRules.CanonicalDecimal(Dec(text));

            Assert.Equal(expected, canonical.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(Dec(text), canonical);
            Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(canonical));
        }

        [Fact]
        public void The_canonical_spelling_survives_a_blocks_json()
        {
            foreach (var text in new[] { "1.0", "1.5", "0.00002", "0.0", "250000.12345678" })
            {
                var value = Dec(text);
                Assert.Equal(text, LedgerIntegrityRules.AsWrittenInABlock(value).ToString(CultureInfo.InvariantCulture));
                var overSignalR = System.Text.Json.JsonSerializer.Deserialize<decimal>(System.Text.Json.JsonSerializer.Serialize(value));
                Assert.Equal(text, overSignalR.ToString(CultureInfo.InvariantCulture));
            }
        }

        [Fact]
        public void The_fee_calculator_writes_the_canonical_spelling()
        {
            // Data lengths that give fees rounding to a trailing zero at the eighth place, and the minimum fee.
            for (var length = 0; length < 2_000; length += 7)
            {
                var tx = new Transaction { Timestamp = 1_790_000_000, FromAddress = _sender.Address, ToAddress = _receiver.Address, Amount = 1.0M, Nonce = 0, TransactionType = TransactionType.TX, Data = new string('a', length) };
                var fee = FeeCalcService.CalculateTXFee(tx);
                Assert.True(LedgerIntegrityRules.IsCanonicalDecimal(fee), $"fee {fee.ToString(CultureInfo.InvariantCulture)} for Data of {length}");
            }
        }

        // ── Admission ────────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1", "0.000006", LedgerIntegrityRules.NonCanonicalAmountReason)]
        [InlineData("1.00", "0.000006", LedgerIntegrityRules.NonCanonicalAmountReason)]
        [InlineData("0", "0.000006", LedgerIntegrityRules.NonCanonicalAmountReason)]
        [InlineData("1.0", "0.00000600", LedgerIntegrityRules.NonCanonicalFeeReason)]
        [InlineData("1.0", "0.0000060", LedgerIntegrityRules.NonCanonicalFeeReason)]
        public async Task From_the_height_admission_refuses_a_non_canonical_amount_or_fee(string amount, string fee, string reason)
        {
            Globals.CanonicalInputHeight = Rule;

            var (admitted, why) = await Admit(Signed(amount, fee));

            Assert.False(admitted);
            Assert.Equal(reason, why);
        }

        [Theory]
        [InlineData("1.0")]
        [InlineData("1.5")]
        [InlineData("0.00002")]
        public async Task From_the_height_admission_accepts_the_canonical_spelling(string amount)
        {
            Globals.CanonicalInputHeight = Rule;

            var (admitted, why) = await Admit(Signed(amount));

            Assert.True(admitted, why);
        }

        [Theory]
        [InlineData("1.00", "0.000006")]
        [InlineData("1.0", "0.00000600")]
        public async Task Below_the_height_admission_accepts_other_spellings_as_today(string amount, string fee)
        {
            Globals.CanonicalInputHeight = Rule + 1;

            var (admitted, why) = await Admit(Signed(amount, fee));

            Assert.True(admitted, why);
        }

        // ── Block validation ─────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1.00", "0.000006", LedgerIntegrityRules.NonCanonicalAmountReason)]
        [InlineData("1", "0.000006", LedgerIntegrityRules.NonCanonicalAmountReason)]
        [InlineData("1.0", "0.00000600", LedgerIntegrityRules.NonCanonicalFeeReason)]
        public async Task From_the_height_a_block_may_not_carry_a_non_canonical_amount_or_fee(string amount, string fee, string reason)
        {
            Globals.CanonicalInputHeight = Rule;
            var tx = Signed(amount, fee);

            var (atTheHeight, why) = await InABlock(tx, Rule);
            var (below, belowWhy) = await InABlock(Signed(amount, fee), Rule - 1);

            Assert.False(atTheHeight);
            Assert.Equal(reason, why);
            Assert.True(below, belowWhy);
        }

        [Fact]
        public async Task From_the_height_a_block_carries_the_canonical_spelling()
        {
            Globals.CanonicalInputHeight = Rule;

            var (accepted, why) = await InABlock(Signed("2.5"), Rule);

            Assert.True(accepted, why);
        }

        // ── The hash is that of the values carried ───────────────────────────────────────────────────────────────

        [Fact]
        public void From_the_height_a_zero_amount_signed_as_0_does_not_match_when_carried_as_0_0()
        {
            Globals.CanonicalInputHeight = Rule;
            var tx = new Transaction { Timestamp = 1_790_000_000, FromAddress = "xSender", ToAddress = "xRecipient", Amount = 0M, Fee = 0.00001M, Nonce = 3, TransactionType = TransactionType.TX };
            tx.Build();
            tx.Amount = 0.0M; // as a block carries it

            Assert.False(TransactionValidatorService.StandardHashMatches(tx, Rule));
            Assert.True(TransactionValidatorService.StandardHashMatches(tx, Rule - 1)); // kept for history below the height
        }

        [Fact]
        public async Task From_the_height_a_transfer_signed_as_0_and_carried_as_0_0_is_refused_in_a_block()
        {
            Globals.CanonicalInputHeight = Rule;
            var tx = Signed("0");
            tx.Amount = 0.0M; // canonical as carried, but signed over "0"

            var (accepted, why) = await InABlock(tx, Rule);
            var below = Signed("0");
            below.Amount = 0.0M;
            var (belowAccepted, belowWhy) = await InABlock(below, Rule - 1);

            Assert.False(accepted);
            Assert.Contains("hash is not equal to the original hash", why);
            Assert.True(belowAccepted, belowWhy);
        }

        // ── Private transactions ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_shield_is_held_to_its_amount_only_and_other_private_transactions_to_neither()
        {
            Globals.CanonicalInputHeight = Rule;
            Transaction Of(TransactionType type, string amount, string fee) =>
                new() { TransactionType = type, Amount = Dec(amount), Fee = Dec(fee), FromAddress = "a", ToAddress = "b" };

            // A shield hashes its Amount as written and its Fee by value.
            Assert.Equal(LedgerIntegrityRules.NonCanonicalAmountReason, LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.VFX_SHIELD, "1.00", "0.000006"), Rule));
            Assert.Null(LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.VFX_SHIELD, "1.0", "0.00000600"), Rule));
            // Unshields and private transfers hash neither.
            Assert.Null(LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.VFX_UNSHIELD, "1.00", "0"), Rule));
            Assert.Null(LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.VFX_PRIVATE_TRANSFER, "0", "0"), Rule));
            // And below the height nothing is checked.
            Assert.Null(LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.VFX_SHIELD, "1.00", "0.000006"), Rule - 1));
            Assert.Null(LedgerIntegrityRules.CanonicalAmountAndFee(Of(TransactionType.TX, "1", "0.00000600"), Rule - 1));
        }
    }
}
