using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// A node applies the evolve calls of the NFTs it holds from each block. Whatever the call's Data holds, applying
    /// it never ends the node's process: a call it cannot read changes nothing and the block goes on.
    /// </summary>
    [Collection("DbContextSequential")]
    public class NftEvolveIncomingCallTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath = Globals.CustomPath;
        private readonly int _priorTimeout = Globals.NFTTimeout;

        public NftEvolveIncomingCallTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"evolvein_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            Globals.CustomPath = _tempRoot;
            Globals.NFTTimeout = 15;   // the config default
            DbContext.Initialize();
        }

        public void Dispose()
        {
            try { DbContext.CloseDB(); } catch { }
            Globals.NFTTimeout = _priorTimeout;
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("Evolve()", "not base64 at all")]
        [InlineData("Devolve()", "not base64 at all")]
        [InlineData("ChangeEvolveStateSpecific()", "not base64 at all")]
        [InlineData("Evolve()", "AAECAwQFBgcICQ==")]                    // base64, but not a compressed body
        [InlineData("ChangeEvolveStateSpecific()", "AAECAwQFBgcICQ==")]
        public async Task AnEvolveCallWhoseDataCannotBeRead_IsAppliedWithoutEndingTheNode(string function, string data)
        {
            var to = "xRecipient" + Guid.NewGuid().ToString("N")[..8];
            var tx = new Transaction
            {
                Hash = Guid.NewGuid().ToString("N"), FromAddress = "xSender", ToAddress = to, Amount = 0, Fee = 0, Nonce = 0,
                Timestamp = TimeUtil.GetTime(), TransactionType = TransactionType.NFT_TX,
                Data = JsonConvert.SerializeObject(new[] { new { Function = function, ContractUID = Guid.NewGuid().ToString("N") + ":1790000000", Data = data } }),
            };

            await BlockTransactionValidatorService.ProcessIncomingTransactions(tx, new Account { Address = to }, 500);
            // Gives a fault on a pool thread time to end the process, as it did.
            await Task.Delay(500);
        }
    }
}
