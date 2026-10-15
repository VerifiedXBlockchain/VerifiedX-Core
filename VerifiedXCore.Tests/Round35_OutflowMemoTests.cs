using System.Linq;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Services;
using VerifiedXCore.Models;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>A vBTC transfer's outflows are read from its data once and remembered by the data (round 35).</summary>
    public class Round35_OutflowMemoTests
    {
        [Fact]
        public void Outflows_AreTheDatasEveryTime_AndACallersListIsItsOwn()
        {
            var single = new Transaction { TransactionType = TransactionType.VBTC_V2_TRANSFER, Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = "u1:1", Amount = 0.5M }) };
            var multi = new Transaction
            {
                TransactionType = TransactionType.VBTC_V2_TRANSFER,
                Data = JsonConvert.SerializeObject(new { Function = VBTCService.MultiTransferFunction, Inputs = new[] { new { SCUID = "u1:1", Amount = 0.1M }, new { SCUID = "u2:2", Amount = 0.2M } } }),
            };
            for (var i = 0; i < 3; i++)
            {
                var a = VBTCService.GetVbtcV2TransferOutflows(single);
                Assert.Equal(new[] { ("u1:1", 0.5M) }, a.ToArray());
                a.Add(("mutated", 9M));                                                   // the caller's own list
                Assert.Equal(new[] { ("u1:1", 0.1M), ("u2:2", 0.2M) }, VBTCService.GetVbtcV2TransferOutflows(multi).ToArray());
            }
            // Padded data is read, not remembered (round 36), and reads the same.
            var padded = new Transaction { TransactionType = TransactionType.VBTC_V2_TRANSFER, Data = JsonConvert.SerializeObject(new { Function = "TransferVBTCV2()", ContractUID = "u1:1", Amount = 0.5M, Pad = new string('p', VBTCService.MaxRememberedOutflowData) }) };
            Assert.Equal(new[] { ("u1:1", 0.5M) }, VBTCService.GetVbtcV2TransferOutflows(padded).ToArray());
            Assert.False(VBTCService.RemembersOutflowsOf(padded.Data));
            Assert.True(VBTCService.RemembersOutflowsOf(single.Data));
            // The same data on another type is not a vBTC transfer.
            Assert.Empty(VBTCService.GetVbtcV2TransferOutflows(new Transaction { TransactionType = TransactionType.TX, Data = single.Data }));
        }
    }
}
