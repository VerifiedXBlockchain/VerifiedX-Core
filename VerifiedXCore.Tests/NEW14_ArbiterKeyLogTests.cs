using System;
using System.IO;
using System.Linq;
using VerifiedXCore;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-14 (found by the fifth independent review; pre-existing): BitcoinAccount.CreatePrivateKeyForArbiter logged its
    /// derivation input - the arbiter's signing private key followed by the contract UID - to sclog.txt in plaintext
    /// (as text and as bytes). ArbiterStartup calls it for any caller with a valid signature from its own address.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NEW14_ArbiterKeyLogTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public NEW14_ArbiterKeyLogTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"new14_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
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

        [Fact]
        public void NEW14_ArbiterDerivation_WritesNoKeyMaterialToAnyLog()
        {
            const string signingKey = "5eca1c0ffee0000000000000000000000000000000000000000000000000d00d";
            var key = BitcoinAccount.CreatePrivateKeyForArbiter(signingKey, "abab:1");
            Assert.NotNull(key);
            System.Threading.Thread.Sleep(300); // log writes are asynchronous

            var leaked = Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                .Where(f => { try { return File.ReadAllText(f).Contains(signingKey); } catch { return false; } })
                .ToList();
            Assert.Empty(leaked);
        }
    }
}
