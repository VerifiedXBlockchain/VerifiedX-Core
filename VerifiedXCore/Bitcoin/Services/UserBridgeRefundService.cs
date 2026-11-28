using System.Numerics;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Newtonsoft.Json;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// The holder's side of a refund the chain recorded (VBTCBridgeRefundState): collects the validators' signatures over
    /// the vBTCb contract's Refund(to, amount, burnId) as the mint collects them (UserBridgeMintService), and sends
    /// refundBurn from this node when it holds the key whose Base address is the refund's recipient. The signatures are
    /// also returned, so a wallet holding that key elsewhere can send it itself.
    /// </summary>
    public static class UserBridgeRefundService
    {
        public sealed record RefundSignatures(VBTCBridgeRefundState Refund, IReadOnlyList<string> Signatures, long ChainId, string ContractAddress);

        /// <summary>Tests only: stands in for the contract's isBurnRefunded.</summary>
        internal static Func<string, Task<bool?>>? IsRefundedForTests;

        /// <summary>The contract's isBurnRefunded(refundId); null when Base could not be asked.</summary>
        public static async Task<bool?> IsRefundedOnBaseAsync(string refundId)
        {
            if (IsRefundedForTests != null) return await IsRefundedForTests(refundId);
            if (!BaseBridgeService.IsBridgeConfigured) return null;
            try
            {
                var c = new Web3(BaseBridgeService.BaseRpcUrl).Eth.GetContract(BaseBridgeService.CONTRACT_ABI, BaseBridgeService.ContractAddress);
                return await c.GetFunction("isBurnRefunded").CallAsync<bool>(refundId);
            }
            catch { return null; }
        }

        /// <summary>
        /// The validators' and casters' signatures over the refund, enough for both of the contract's thresholds, or why
        /// there are none: the refund must be recorded under exactly <paramref name="refundId"/>, and the contract must read
        /// the typed messages (only version 4 has a refund).
        /// </summary>
        public static async Task<(RefundSignatures? Signed, string? Reason)> CollectSignaturesAsync(string refundId)
        {
            var refund = VBTCBridgeRefundState.Get(refundId);
            if (refund == null || refund.RefundId != refundId) return (null, "No refund is recorded under this id.");
            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null) return (null, "Could not read the Base contract's validators.");
            if (view.MessageVersion != VbtcbTypedData.MessageVersion) return (null, $"The Base contract reads message version {view.MessageVersion}; refunds need {VbtcbTypedData.MessageVersion}.");

            var chainId = BaseBridgeService.BaseChainId;
            var contract = BaseBridgeService.ContractAddress;
            var digest = VbtcbTypedData.RefundDigest(refund.BaseBurner, refund.AmountSats, refund.RefundId, chainId, contract);
            var body = JsonConvert.SerializeObject(new RefundAttestationRequest
            {
                RefundId = refund.RefundId,
                To = refund.BaseBurner,
                AmountSats = refund.AmountSats,
                ChainId = chainId,
                ContractAddress = contract,
                MessageVersion = view.MessageVersion,
            });
            var signers = UserBridgeMintService.ContractSigners(view);
            var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < 3 && !UserBridgeMintService.Enough(view, view.RequiredMintSignatures, signatures.Keys); attempt++)
            {
                if (attempt > 0) await Task.Delay(15_000);
                await UserBridgeMintService.AskSignersAsync(signers, body, digest, typed: true, signatures,
                    signed => UserBridgeMintService.Enough(view, view.RequiredMintSignatures, signed), refund.RefundId, route: "SignRefundAttestation");
            }
            if (!UserBridgeMintService.Enough(view, view.RequiredMintSignatures, signatures.Keys))
                return (null, $"{view.Validators.Count(signatures.ContainsKey)}/{view.RequiredMintSignatures} validator and {view.CasterList.Count(signatures.ContainsKey)}/{view.RequiredCasterSignatures} caster signatures.");
            return (new RefundSignatures(refund, signatures.Values.ToList(), chainId, contract), null);
        }

        /// <summary>The local account whose Base address (derived from its own key) is <paramref name="baseAddress"/>, or null.</summary>
        internal static VerifiedXCore.Models.Account? AccountForBaseAddress(string baseAddress)
        {
            foreach (var account in AccountData.GetAccounts()?.FindAll() ?? Enumerable.Empty<VerifiedXCore.Models.Account>())
            {
                try
                {
                    var key = account.GetKey;
                    if (string.IsNullOrEmpty(key)) continue;
                    if (key.Length % 2 != 0) key = "0" + key;
                    if (string.Equals(ValidatorEthKeyService.DeriveBaseAddress(BoundedDecode.Hex(key, 33)), baseAddress, StringComparison.OrdinalIgnoreCase))
                        return account;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Sends refundBurn for the refund from the local account whose Base address is its recipient (the sender pays the
        /// gas): nothing when the contract already refunded it. Returns the Base transaction hash, or why nothing was sent.
        /// </summary>
        public static async Task<(bool Success, string TxHashOrReason)> SubmitRefundAsync(string refundId)
        {
            var refund = VBTCBridgeRefundState.Get(refundId);
            if (refund == null || refund.RefundId != refundId) return (false, "No refund is recorded under this id.");
            if (await IsRefundedOnBaseAsync(refund.RefundId) == true) return (false, "This burn is already refunded on Base.");
            var account = AccountForBaseAddress(refund.BaseBurner);
            if (account == null) return (false, $"This node holds no key for {refund.BaseBurner}; send refundBurn with the signatures from the wallet that holds it.");

            var (signed, reason) = await CollectSignaturesAsync(refundId);
            if (signed == null) return (false, reason ?? "Not enough signatures.");
            try
            {
                var key = account.GetKey;
                if (key.Length % 2 != 0) key = "0" + key;
                var ethAccount = new Account(new EthECKey(BoundedDecode.Hex(key, 33), true), signed.ChainId);
                var web3 = new Web3(ethAccount, BaseBridgeService.BaseRpcUrl);
                var refundBurn = web3.Eth.GetContract(BaseBridgeService.CONTRACT_ABI, signed.ContractAddress).GetFunction("refundBurn");
                var sigBytes = signed.Signatures.Select(s => BoundedDecode.Hex(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 65)).ToList();
                var args = new object[] { refund.BaseBurner, new BigInteger(refund.AmountSats), refund.RefundId, sigBytes };
                var gas = await refundBurn.EstimateGasAsync(ethAccount.Address, null, null, args);
                var txHash = await refundBurn.SendTransactionAsync(ethAccount.Address, new HexBigInteger(new BigInteger((double)gas.Value * 1.2)), null, null, args);
                LogUtility.Log($"[UserBridgeRefund] refundBurn sent for {refund.RefundId}: {txHash}", "UserBridgeRefundService");
                return (true, txHash);
            }
            catch (Exception ex)
            {
                return (false, $"refundBurn not sent: {ApiErrorText.For(ex)}");
            }
        }
    }
}
