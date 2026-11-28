using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using VerifiedXCore.Bitcoin.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// Reading the Base contract's message version, only a revert means the contract has no such function (an older
    /// contract). Any other failure of the call is the provider's and is not read as an older contract.
    /// </summary>
    public class BaseContractVersionReadTests
    {
        [Fact]
        public void OnlyARevert_SaysTheContractHasNoSuchFunction()
        {
            Assert.True(BaseBridgeService.SaysTheContractReverted(Rpc(3, "execution reverted")));
            Assert.True(BaseBridgeService.SaysTheContractReverted(Rpc(-32000, "execution reverted")));
            Assert.True(BaseBridgeService.SaysTheContractReverted(new Nethereum.ABI.FunctionEncoding.SmartContractRevertException("reverted", "0x")));

            Assert.False(BaseBridgeService.SaysTheContractReverted(Rpc(-32005, "limit exceeded")));
            Assert.False(BaseBridgeService.SaysTheContractReverted(Rpc(429, "Too Many Requests")));
            Assert.False(BaseBridgeService.SaysTheContractReverted(Rpc(-32603, "Internal error")));
            Assert.False(BaseBridgeService.SaysTheContractReverted(Rpc(-32000, "header not found")));
            Assert.False(BaseBridgeService.SaysTheContractReverted(new Nethereum.JsonRpc.Client.RpcClientTimeoutException("timed out")));
            Assert.False(BaseBridgeService.SaysTheContractReverted(new System.Net.Http.HttpRequestException("connection refused")));
        }


        private static Exception Rpc(int code, string message) => new Nethereum.JsonRpc.Client.RpcResponseException(new Nethereum.JsonRpc.Client.RpcError(code, message));

        private static BaseBridgeService.SignerView View(int version) =>
            new BaseBridgeService.SignerView(new List<string> { "0xv1", "0xv2" }, version, 2, BigInteger.One);

        [Fact]
        public async Task AProviderThatFailsOnTheVersion_IsPassedOver_AndTheVersionTwoOthersGiveIsUsed()
        {
            var asked = new List<string>();
            // Each provider's read, as the node makes it: the version call goes through ReadMessageVersionAsync.
            async Task<BaseBridgeService.SignerView> Read(string url)
            {
                asked.Add(url);
                var version = await BaseBridgeService.ReadMessageVersionAsync(() => url switch
                {
                    "limited" => throw Rpc(-32005, "limit exceeded"),
                    "down" => throw new System.Net.Http.HttpRequestException("connection refused"),
                    _ => Task.FromResult(new BigInteger(4)),
                });
                return View(version);
            }

            // The view is the one two providers give alike: one good provider beside the two that fail is not enough.
            Assert.Null(await BaseBridgeService.ReadSignerViewFromAsync(new[] { "limited", "down", "good" }, Read));
            asked.Clear();

            var view = await BaseBridgeService.ReadSignerViewFromAsync(new[] { "limited", "down", "good", "also good" }, Read);

            Assert.Equal(new[] { "also good", "down", "good", "limited" }, asked.OrderBy(a => a, StringComparer.Ordinal)); // every provider is asked
            Assert.NotNull(view);
            Assert.Equal(4, view!.MessageVersion); // not the older form, which the first provider's failure used to mean
        }

        [Fact]
        public async Task WhenEveryProviderFails_ThereIsNoView_AndAnOlderContractIsStillRead()
        {
            Assert.Null(await BaseBridgeService.ReadSignerViewFromAsync(new[] { "a", "b" }, async url => View(await BaseBridgeService.ReadMessageVersionAsync(() => throw Rpc(-32603, "Internal error")))));

            // A contract without the function reverts: that is the older contract, as both providers give it.
            var older = await BaseBridgeService.ReadSignerViewFromAsync(new[] { "a", "b" }, async url => View(await BaseBridgeService.ReadMessageVersionAsync(() => throw Rpc(3, "execution reverted"))));
            Assert.Equal(3, older!.MessageVersion);
        }
    }
}
