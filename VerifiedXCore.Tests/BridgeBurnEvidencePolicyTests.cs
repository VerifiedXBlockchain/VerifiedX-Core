using Newtonsoft.Json.Linq;
using Nethereum.RPC.Eth.DTOs;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Consensus determinism: a live Base RPC answer must never decide block validity (outage,
    /// reorg, or provider skew would split the chain). RPC evidence is consulted only when a
    /// transaction is first admitted; block validation relies on the committee-bound votes.
    /// Also: the "was this a bridge burn" test keys on the emitting log address, not receipt.to,
    /// so burns made through smart-contract wallets are not silently rejected.
    /// </summary>
    public class BridgeBurnEvidencePolicyTests
    {
        [Fact]
        public void BlockValidation_NeverQueriesBase_EvenWhenConfigured()
        {
            Assert.False(BridgeBurnEvidencePolicy.ShouldQueryBase(blockHeight: 123, bridgeConfigured: true));
            Assert.False(BridgeBurnEvidencePolicy.ShouldQueryBase(blockHeight: 0, bridgeConfigured: true));
        }

        [Fact]
        public void Admission_QueriesBase_OnlyWhenConfigured()
        {
            Assert.True(BridgeBurnEvidencePolicy.ShouldQueryBase(blockHeight: null, bridgeConfigured: true));
            Assert.False(BridgeBurnEvidencePolicy.ShouldQueryBase(blockHeight: null, bridgeConfigured: false));
        }

        [Fact]
        public void ReceiptLogCheck_MatchesEmittingAddress_CaseInsensitive()
        {
            var prior = BaseBridgeService.ContractAddress;
            try
            {
                BaseBridgeService.ContractAddress = "0xAbCdEf0000000000000000000000000000000001";
                var viaSafe = new TransactionReceipt
                {
                    To = "0x9999999999999999999999999999999999999999", // a smart-contract wallet, not the bridge
                    Logs = new JArray(
                        new JObject { ["address"] = "0x9999999999999999999999999999999999999999" },
                        new JObject { ["address"] = "0xabcdef0000000000000000000000000000000001" })
                };
                Assert.True(BaseBridgeService.ReceiptHasLogFromContract(viaSafe));

                var unrelated = new TransactionReceipt
                {
                    To = "0xAbCdEf0000000000000000000000000000000001", // sent TO the bridge but emitted nothing from it
                    Logs = new JArray(new JObject { ["address"] = "0x1111111111111111111111111111111111111111" })
                };
                Assert.False(BaseBridgeService.ReceiptHasLogFromContract(unrelated));

                Assert.False(BaseBridgeService.ReceiptHasLogFromContract(new TransactionReceipt { Logs = null }));
                Assert.False(BaseBridgeService.ReceiptHasLogFromContract(null!));
            }
            finally
            {
                BaseBridgeService.ContractAddress = prior;
            }
        }
    }
}
