using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Data;
using VerifiedXCore.Extensions;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-29 (review round 7): the node runs contract code (Trillium) to read a contract, in consensus too (VX-01, VX-02,
    /// NEW-26, NEW-28, token-deploy apply). The language's loop/recursion ban was never switched on, so a body with an
    /// endless loop hung the thread reading it and one that called itself overflowed the stack and killed the node - from
    /// one signed mint, which VX-02 decompiles at admission. The ban is now on for every run. Bodies are real ones from the
    /// node's writer with a loop or recursion added at top level, which runs as soon as the body is loaded.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW29_ContractCodeTests : IDisposable
    {
        private const string Uid = "9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f9f:1790900000";
        private const string Minter = "RMinterAddressForNew29Tests00000000";
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public NEW29_ContractCodeTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new29_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        internal static string Source(string body) => Encoding.Unicode.GetString(Utilities.SmartContractUtility.Decompress(Convert.FromBase64String(body)));
        internal static string Body(string source) => Encoding.Unicode.GetBytes(source).ToCompress().ToBase64();
        internal static string WithCode(string body, string code) => Body(Source(body) + Environment.NewLine + code + Environment.NewLine);

        internal const string EndlessWhile = "var spin = 0\nwhile true { spin = spin + 1 }";
        internal const string EndlessFor = "var total = 0\nfor i = 0 to 2000000000 { total = total + 1 }";
        internal const string SelfCall = "function Again() : int { return Again() }\nvar r = Again()";

        private static string Plain() => VbtcTestContracts.BuildContractData(Uid, Minter, null, name: "Plain");

        /// <summary>Runs the node's decompiler; true when it finished (returned or threw) within the limit.</summary>
        private static bool Finishes(string body)
        {
            var run = Task.Run(() => { try { SmartContractMain.GenerateSmartContractInMemory(body); } catch { } });
            return run.Wait(Limit);
        }

        [Fact]
        public void AnOrdinaryBody_StillDecompiles()
        {
            var sc = SmartContractMain.GenerateSmartContractInMemory(Plain());
            Assert.Equal(Uid, sc.SmartContractUID);
            Assert.Equal(Minter, sc.MinterAddress);
            Assert.True(VBTCService.IsVbtcV2Contract(new SmartContractStateTrei { SmartContractUID = "x:1", ContractData = VbtcTestContracts.VbtcV2ContractData }));
        }

        [Theory]
        [InlineData(EndlessWhile)]
        [InlineData(EndlessFor)]
        [InlineData(SelfCall)]
        public void ABodyWithALoopOrRecursion_IsNeverRun(string code)
        {
            var body = WithCode(Plain(), code);
            Assert.True(Finishes(body), "decompiling the body must not hang or crash the node");
            Assert.ThrowsAny<Exception>(() => SmartContractMain.GenerateSmartContractInMemory(body)); // unreadable, like any bad body
        }

        [Theory]
        [InlineData(EndlessWhile)]
        [InlineData(SelfCall)]
        public void ConsensusReadersAnswerAtOnce_AndTheSameWayEveryTime(string code)
        {
            var body = WithCode(Plain(), code);
            // VX-02: a mint falls back to the identity the body declares (as for any body the decompiler cannot read);
            // a token deploy, which credits a supply read from the body, is refused.
            Assert.Null(SmartContractDeployBinding.Validate(body, Uid, Minter, isTokenDeploy: false, out var mintDecompiled));
            Assert.Null(mintDecompiled);
            Assert.Equal("Smart contract body could not be decompiled for token deploy.",
                SmartContractDeployBinding.Validate(body, Uid, Minter, isTokenDeploy: true, out _));
            // VX-01 / NEW-28: a stored body that cannot be read is neither a V2 vault nor a V1 contract.
            var stored = new SmartContractStateTrei { SmartContractUID = Uid, ContractData = body };
            Assert.False(VBTCService.IsVbtcV2Contract(stored));
            Assert.False(VBTCService.IsVbtcV1Contract(stored));
        }
    }
}
