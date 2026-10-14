using System;
using System.Collections.Generic;
using System.IO;
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
    /// In a block, contract code is read as of the block (third pass, 14 October 2026).
    ///
    /// Every node reads a contract by running its code, and the code can ask whether a date has passed (dateProc,
    /// which date-evolving NFTs use). In block validation the answer came from the clock of the node doing the reading,
    /// so a contract written to change at a date (its identity, its minter, its royalty payee) was one thing to the
    /// validators of a block and another to a node reading the same block later: that node refused a block the network
    /// had accepted and could not follow the chain past it. From Globals.PathAuditRulesHeight the answer is the
    /// block's timestamp, on every node, whenever it reads the block.
    /// </summary>
    [Collection("DbContextSequential")]
    public class PathAudit3_ContractClockTests : IDisposable
    {
        private const string Uid = "7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c:1790900777";
        private const long Rule = 1000;
        private const long Day = 86400;
        private const string NotThisContract = "Smart contract body UID does not match the transaction ContractUID";
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly Block _priorLastBlock = Globals.LastBlock;
        private readonly long _priorRule = Globals.PathAuditRulesHeight;
        private readonly long _now = TimeUtil.GetTime();
        private readonly (PrivateKey Key, string Pub, string Address) _owner = NewKey();

        public PathAudit3_ContractClockTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"pa3clock_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
            Globals.LastBlock = new Block { Height = Rule, Timestamp = _now - 3 * Day };
            Globals.PathAuditRulesHeight = Rule;
            StateData.GetAccountStateTrei().InsertSafe(new AccountStateTrei { Key = _owner.Address, Balance = 100M, Nonce = 0 });
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.LastBlock = _priorLastBlock;
            Globals.PathAuditRulesHeight = _priorRule;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static (PrivateKey Key, string Pub, string Address) NewKey()
        {
            var key = new PrivateKey("secp256k1");
            var pub = "04" + Convert.ToHexString(key.publicKey().toString()).ToLowerInvariant();
            return (key, pub, AccountData.GetHumanAddress(pub));
        }

        private static long Ticks(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Ticks;

        /// <summary>A contract that is itself until a date, and says it is another contract from then on.</summary>
        private string ChangesAt(long unixSeconds)
        {
            var source = NEW29_ContractCodeTests.Source(VbtcTestContracts.BuildContractData(Uid, _owner.Address, null, name: "Plain"));
            var function = source.IndexOf("function GetNFTId() : string", StringComparison.Ordinal);
            Assert.True(function >= 0);
            var at = source.IndexOf("return SmartContractUID", function, StringComparison.Ordinal);
            Assert.True(at >= 0);
            var changed = source.Insert(at, "if(dateProc(\"" + Ticks(unixSeconds) + "\") == true)" + Environment.NewLine + "   {" + Environment.NewLine
                + "      return \"another-contract\"" + Environment.NewLine + "   }" + Environment.NewLine + "   ");
            return NEW29_ContractCodeTests.Body(changed);
        }

        private Transaction Mint(string body, long timestamp)
        {
            var tx = new Transaction
            {
                Timestamp = timestamp, FromAddress = _owner.Address, ToAddress = _owner.Address, Amount = 0.0M, Fee = 0, Nonce = 0,
                TransactionType = TransactionType.NFT_MINT,
                Data = JsonConvert.SerializeObject(new[] { new { Function = "Mint()", ContractUID = Uid, Data = body } }),
            };
            tx.Fee = FeeCalcService.CalculateTXFee(tx);
            tx.Build();
            tx.Signature = SignatureService.CreateSignature(tx.Hash, _owner.Key, _owner.Pub);
            return tx;
        }

        private static string UidRead(string body) => SmartContractMain.GenerateSmartContractInMemory(body).SmartContractUID;

        [Fact]
        public void TheDecompiler_AnswersADateFromTheBlock_WhenABlockIsBeingRead()
        {
            var changesTomorrow = ChangesAt(_now + Day);
            var changedYesterday = ChangesAt(_now - Day);

            // Outside a block (a wallet, the API) the node's clock is the time, as before.
            Assert.Equal(Uid, UidRead(changesTomorrow));
            Assert.Equal("another-contract", UidRead(changedYesterday));

            // In a block it is the block's time: a block of two days ago, read today, and one of the day after tomorrow.
            using (ContractClock.ForBlock(_now - 2 * Day, Rule + 1))
            {
                Assert.Equal(Uid, UidRead(changedYesterday));
                Assert.Equal(Uid, UidRead(changesTomorrow));
            }
            using (ContractClock.ForBlock(_now + 2 * Day, Rule + 1))
            {
                Assert.Equal("another-contract", UidRead(changesTomorrow));
                Assert.Equal("another-contract", UidRead(changedYesterday));
            }
            Assert.False(ContractClock.IsSet);
            Assert.Equal(Uid, UidRead(changesTomorrow));

            // Before the rules' height the clock is not set (only the block's height is recorded).
            using (ContractClock.ForBlock(_now - 2 * Day, Rule - 1))
            {
                Assert.False(ContractClock.IsSet);
                Assert.Equal(Rule - 1, ContractClock.BlockHeight);
            }
            Assert.Null(ContractClock.BlockHeight);
            // A date the code computes instead of naming has not passed, on any node.
            var source = NEW29_ContractCodeTests.Source(changedYesterday).Replace("dateProc(\"" + Ticks(_now - Day) + "\")", "dateProc(\"" + Ticks(_now - Day).ToString()[..9] + "\" + \"" + Ticks(_now - Day).ToString()[9..] + "\")");
            var computed = NEW29_ContractCodeTests.Body(source);
            Assert.Equal("another-contract", UidRead(computed));
            using (ContractClock.ForBlock(_now, Rule + 1))
                Assert.Equal(Uid, UidRead(computed));
            // Code that declares its own dateProc runs that one, in a block or not.
            var own = NEW29_ContractCodeTests.Body("function dateProc(d : string) : bool" + Environment.NewLine + "{" + Environment.NewLine + "   return false" + Environment.NewLine + "}"
                + Environment.NewLine + NEW29_ContractCodeTests.Source(changedYesterday));
            Assert.Equal(Uid, UidRead(own));
            using (ContractClock.ForBlock(_now, Rule + 1))
                Assert.Equal(Uid, UidRead(own));
        }

        [Fact]
        public async Task ABlocksTransaction_IsJudgedWithTheContractAsItReadAtTheBlocksTime_OnEveryNodeAlike()
        {
            // The contract changed yesterday. Its mint was in a block two days ago, when it still read as itself.
            var body = ChangesAt(_now - Day);
            var blockTime = _now - 2 * Day;
            var mint = Mint(body, blockTime);

            // Read today with the block's own time: accepted, live or in a download.
            var (ok, why) = await TransactionValidatorService.VerifyTX(mint, false, true, false, null, false, Rule + 1, blockTime);
            Assert.True(ok, why);
            (ok, why) = await TransactionValidatorService.VerifyTX(mint, true, true, false, null, false, Rule + 1, blockTime);
            Assert.True(ok, why);

            // The same mint in a block timed after the change is not this contract's mint, whoever reads it and when.
            var later = Mint(body, _now);
            (ok, why) = await TransactionValidatorService.VerifyTX(later, false, true, false, null, false, Rule + 1, _now);
            Assert.False(ok);
            Assert.Contains(NotThisContract, why);

            // Before the rule the reader's clock decided: the block of two days ago is refused by a node reading it today.
            Globals.PathAuditRulesHeight = 1_000_000;
            (ok, why) = await TransactionValidatorService.VerifyTX(mint, true, true, false, null, false, Rule + 1, blockTime);
            Assert.False(ok);
            Assert.Contains(NotThisContract, why);
        }

        [Theory]
        [InlineData("function Tell() : string { return send(\"a\", \"b\") }", "send")]
        [InlineData("function Sign() : string { return createSig(\"a\") }", "createSig")]
        public void CodeThatReachesIntoTheNodesProcess_IsRefusedFromTheRule(string code, string builtin)
        {
            var body = NEW29_ContractCodeTests.WithCode(VbtcTestContracts.BuildContractData(Uid, _owner.Address, null, name: "Plain"), code);
            var mint = Mint(body, _now);
            Assert.Null(LedgerIntegrityRules.ContractBodyAllowed(mint, Rule - 1));
            Assert.Equal($"Contract code may not call {builtin}().", LedgerIntegrityRules.ContractBodyAllowed(mint, Rule));
        }
    }
}
