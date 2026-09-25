using Newtonsoft.Json.Linq;
using VerifiedXCore.Bitcoin.Controllers;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// S3C §12: best-effort, IN-MEMORY orchestrator for one-click S3C → public companion → Base.
    /// Persists nothing; on restart the orchestration is lost but all on-chain/companion state
    /// survives and the user finishes via the manual path. No consensus surface — pure
    /// orchestration over the §5/§6 manual primitives. Must run on the node that owns the companion.
    /// </summary>
    public static class S3CAutoBridgeService
    {
        private static readonly ConcurrentDictionary<string, S3CAutoBridgeState> _orchestrations = new();

        private const int FeeRate = 10;                          // sat/vB default for the S3C withdrawal
        private const int CeremonyPollSeconds = 5;
        private const int CeremonyMaxSeconds = 600;              // ~10 min DKG ceiling
        private const int ContractFreePollSeconds = 15;
        private const int ContractFreeMaxSeconds = 1800;         // ~30 min wait for the §0 slot
        private const int BtcArrivalPollSeconds = 45;            // dedicated poll (§12.4)
        private const int BtcArrivalMaxSeconds = 3600;           // BTC confirmation ceiling
        private const int GasPollSeconds = 30;
        private const int GasMaxSeconds = 3600;                  // <= 1h gas window (§12.4)
        private const int CompanionConfirmMaxSeconds = 1800;     // NEW-26 follow-up: wait for the companion to be on chain

        public static S3CAutoBridgeState? GetStatus(string orchestrationId)
            => _orchestrations.TryGetValue(orchestrationId, out var s) ? s : null;

        /// <summary>
        /// Kick off an orchestration. Returns immediately with the initial state (incl. the derived
        /// Base gas address + ETH balance so the user can fund gas during the BTC-withdrawal window).
        /// </summary>
        public static S3CAutoBridgeState StartAutoBridge(string s3cContractUID, string requesterAddress,
            decimal amount, string evmDestination)
        {
            var now = TimeUtil.GetTime();
            var state = new S3CAutoBridgeState
            {
                OrchestrationId = Guid.NewGuid().ToString(),
                S3CContractUID = s3cContractUID,
                RequesterAddress = requesterAddress,
                RequestedAmount = amount,
                EvmDestination = evmDestination,
                Status = S3CAutoBridgeStatus.ResolvingCompanion,
                StartedTimestamp = now,
                UpdatedTimestamp = now
            };

            // Surface the gas address + balance up front so the user funds gas during the slow window.
            state.BaseGasAddress = ValidatorEthKeyService.DeriveBaseAddressFromAccount(requesterAddress);
            if (!string.IsNullOrEmpty(state.BaseGasAddress))
            {
                try
                {
                    var eth = BaseBridgeService.GetEthBalanceAsync(state.BaseGasAddress).GetAwaiter().GetResult();
                    if (eth.Success) state.BaseGasEthBalance = eth.BalanceEth;
                }
                catch { }
            }

            _orchestrations[state.OrchestrationId] = state;
            _ = Task.Run(() => RunOrchestration(state));
            return state;
        }

        private static void Set(S3CAutoBridgeState s, S3CAutoBridgeStatus status, string? error = null)
        {
            s.Status = status;
            if (error != null) s.Error = error;
            s.UpdatedTimestamp = TimeUtil.GetTime();
            LogUtility.Log($"[S3C AutoBridge] {s.OrchestrationId}: {status}{(error != null ? $" — {error}" : "")}",
                "S3CAutoBridgeService.RunOrchestration");
        }

        private static async Task RunOrchestration(S3CAutoBridgeState s)
        {
            try
            {
                // 1. Resolve or create the companion (§12.2).
                var companion = DiscoverCompanion(s.RequesterAddress, s.S3CContractUID);
                if (companion == null)
                {
                    Set(s, S3CAutoBridgeStatus.CreatingCompanion);
                    var created = await CreateCompanion(s.RequesterAddress, s.S3CContractUID);
                    if (created == null) { Set(s, S3CAutoBridgeStatus.Failed, "Companion creation failed (public DKG)."); return; }
                    s.PublicScUID = created.Value.scUID;
                    s.PublicDepositAddress = created.Value.depositAddress;
                    s.CompanionCreateTxHash = created.Value.txHash;
                    s.CompanionBalanceBefore = 0M;   // fresh companion
                }
                else
                {
                    s.PublicScUID = companion.SmartContractUID;
                    s.PublicDepositAddress = companion.DepositAddress;
                    // Snapshot the reused companion's balance so we bridge only the arrived delta (§12.3).
                    var bal = await VBTCService.TryGetAvailableTransparentVbtcBalance(s.PublicScUID, s.RequesterAddress);
                    s.CompanionBalanceBefore = bal.success ? bal.availableBalance : 0M;
                }
                Set(s, S3CAutoBridgeStatus.AwaitingCompanionReady);

                // NEW-26 (follow-up): BTC goes only to a companion that exists on chain. The create response (and a local
                // record) carries the deposit address before the creation is mined; if it never is, BTC sent there has no
                // contract and no withdrawal path. Wait for the contract, then take the address from the confirmation-gated
                // GetMPCDepositAddress. Nothing has moved yet if this gives up.
                var confirmedDeposit = await WaitForCompanionOnChain(s.PublicScUID!, CompanionConfirmMaxSeconds, CeremonyPollSeconds);
                if (string.IsNullOrEmpty(confirmedDeposit))
                {
                    // A companion whose creation is gone (not on chain, not pending) is forgotten locally so a retry creates a
                    // new one; one still pending is kept (a retry reuses it once it confirms). No BTC was sent to it.
                    var forgotten = s.CompanionCreateTxHash != null && ForgetUnconfirmedCompanion(s.PublicScUID!, s.CompanionCreateTxHash);
                    Set(s, S3CAutoBridgeStatus.Abandoned, "The companion contract was not confirmed on chain within the wait window; nothing moved."
                        + (forgotten ? " Its creation was not mined; a retry will create a new companion." : ""));
                    return;
                }
                s.PublicDepositAddress = confirmedDeposit;

                // 2. Wait for the S3C contract's withdrawal slot to be free (§0 / §12.4).
                Set(s, S3CAutoBridgeStatus.WaitingForContractFree);
                if (!await WaitForContractFree(s.S3CContractUID))
                { Set(s, S3CAutoBridgeStatus.Abandoned, "S3C contract stayed busy past the wait window; nothing moved."); return; }

                // 3. Submit + complete the S3C withdrawal → companion deposit address.
                Set(s, S3CAutoBridgeStatus.WithdrawingFromS3C);
                var (reqOk, reqTxHash) = await VBTCService.RequestWithdrawal(
                    s.S3CContractUID, s.RequesterAddress, s.PublicDepositAddress!, s.RequestedAmount, FeeRate);
                if (!reqOk)
                { Set(s, S3CAutoBridgeStatus.Failed, $"Withdrawal request failed: {reqTxHash}. Funds still in the S3C contract."); return; }

                // Give the request a moment to be mined so the contract's Active* fields are populated.
                await Task.Delay(TimeSpan.FromSeconds(CeremonyPollSeconds));
                var comp = await VBTCService.CompleteWithdrawal(s.S3CContractUID, reqTxHash);
                if (!comp.Success)
                { Set(s, S3CAutoBridgeStatus.Failed, $"Withdrawal completion failed: {comp.ErrorMessage}. Funds still in the S3C contract."); return; }

                // 4. Wait for the BTC to arrive at the companion deposit address (confirmed delta).
                Set(s, S3CAutoBridgeStatus.AwaitingBTCArrival);
                var arrived = await WaitForBtcArrival(s.PublicScUID!, s.RequesterAddress, s.CompanionBalanceBefore);
                if (arrived <= 0M)
                { Set(s, S3CAutoBridgeStatus.Abandoned, "BTC did not arrive within the window; funds safe in the companion — bridge manually."); return; }
                s.ArrivedAmount = arrived;

                // 5. Wait for ETH gas on the derived Base address (<= 1h).
                Set(s, S3CAutoBridgeStatus.AwaitingGas);
                if (!await WaitForGas(s))
                { Set(s, S3CAutoBridgeStatus.Abandoned, "Gas not funded within 1h; funds safe as vBTC in the companion — bridge manually."); return; }

                // 6. Bridge the arrived delta. ExecuteBridgeToBase hands off to the BridgeLockRecord flow.
                Set(s, S3CAutoBridgeStatus.Bridging);
                var bridge = await UserBridgeMintService.ExecuteBridgeToBase(
                    s.PublicScUID!, s.RequesterAddress, s.ArrivedAmount, s.EvmDestination);
                if (!bridge.Success)
                { Set(s, S3CAutoBridgeStatus.Failed, $"Bridge submission failed: {bridge.Message}. Funds safe as vBTC in the companion."); return; }
                s.LockId = bridge.LockId;
                Set(s, S3CAutoBridgeStatus.Completed);
            }
            catch (Exception ex)
            {
                Set(s, S3CAutoBridgeStatus.Failed, $"Unexpected error: {ex.Message}");
            }
        }

        // §12.2: scan the requester's owned contracts for an existing public companion linked to the S3C.
        // NEW-26 (follow-up): only a companion that is on chain. A local record whose creation was never mined would
        // otherwise be rediscovered on every retry and waited for forever.
        internal static VBTCContractV2? DiscoverCompanion(string requester, string s3cUID)
        {
            var owned = VBTCContractV2.GetContractsByOwner(requester);
            return owned?.FirstOrDefault(c => !c.IsS3C && c.LinkedContractUID == s3cUID
                && !string.IsNullOrEmpty(c.SmartContractUID) && SmartContractStateTrei.GetSmartContractState(c.SmartContractUID) != null);
        }

        /// <summary>
        /// NEW-26 (follow-up): removes the local records of a companion whose creation is not on chain and no longer in the
        /// mempool (dropped or refused), so it is neither shown nor waited for again. Keeps them while the creation is still
        /// pending or once it is on chain. Returns whether it removed them.
        /// </summary>
        internal static bool ForgetUnconfirmedCompanion(string companionScUID, string createTxHash)
        {
            try
            {
                if (SmartContractStateTrei.GetSmartContractState(companionScUID) != null) return false;
                if (!string.IsNullOrEmpty(createTxHash) && TransactionData.GetPool().FindOne(x => x.Hash == createTxHash) != null) return false;
                VBTCContractV2.DeleteContract(companionScUID);
                SmartContractMain.SmartContractData.DeleteSmartContract(companionScUID);
                return true;
            }
            catch { return false; }
        }

        // §12.2: create a public companion linked to the S3C contract via the live ceremony path
        // (forcePublic so the DKG uses the public pool even on an S3C-configured node).
        private static async Task<(string scUID, string depositAddress, string? txHash)?> CreateCompanion(string requester, string s3cUID)
        {
            try
            {
                var controller = new VBTCController();
                var initJson = await controller.InitiateMPCCeremony(requester, forcePublic: true);
                var initObj = JObject.Parse(initJson);
                if (initObj["Success"]?.ToObject<bool>() != true) return null;
                var ceremonyId = initObj["CeremonyId"]?.ToObject<string>();
                if (string.IsNullOrEmpty(ceremonyId)) return null;

                // Poll the shared in-memory ceremony state until completion.
                var waited = 0;
                while (waited < CeremonyMaxSeconds)
                {
                    var statusJson = VBTCController.GetCeremonyStatusStatic(ceremonyId);
                    var status = JObject.Parse(statusJson)["Status"]?.ToObject<string>();
                    if (status == "Completed") break;
                    if (status == "Failed" || status == "TimedOut") return null;
                    await Task.Delay(TimeSpan.FromSeconds(CeremonyPollSeconds));
                    waited += CeremonyPollSeconds;
                }
                if (waited >= CeremonyMaxSeconds) return null;

                var createJson = await controller.CreateVBTCContract(new VBTCContractPayload
                {
                    OwnerAddress = requester,
                    Name = "S3C Companion",
                    Ticker = "vBTC",
                    CeremonyId = ceremonyId,
                    LinkedContractUID = s3cUID
                });
                var createObj = JObject.Parse(createJson);
                if (createObj["Success"]?.ToObject<bool>() != true) return null;
                var scUID = createObj["SmartContractUID"]?.ToObject<string>();
                var deposit = createObj["DepositAddress"]?.ToObject<string>();
                if (string.IsNullOrEmpty(scUID) || string.IsNullOrEmpty(deposit)) return null;
                return (scUID, deposit, createObj["TransactionHash"]?.ToObject<string>());
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"[S3C AutoBridge] CreateCompanion error: {ex.Message}", "S3CAutoBridgeService.CreateCompanion");
                return null;
            }
        }

        /// <summary>
        /// NEW-26 (follow-up): the companion's deposit address once its contract is on chain, polling up to
        /// <paramref name="maxSeconds"/>; null if it never confirms. Uses GetMPCDepositAddress, which answers only for a
        /// contract in chain state.
        /// </summary>
        internal static async Task<string?> WaitForCompanionOnChain(string companionScUID, int maxSeconds, int pollSeconds)
        {
            var controller = new VBTCController();
            var waited = 0;
            while (true)
            {
                try
                {
                    var obj = JObject.Parse(await controller.GetMPCDepositAddress(companionScUID));
                    var address = obj["DepositAddress"]?.ToObject<string>();
                    if (obj["Success"]?.ToObject<bool>() == true && !string.IsNullOrEmpty(address))
                        return address;
                }
                catch { }
                if (waited >= maxSeconds) return null;
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
                waited += pollSeconds;
            }
        }

        // §12.4: turn §0's per-contract reject into a local wait-and-retry, bounded by a timeout.
        private static async Task<bool> WaitForContractFree(string s3cUID)
        {
            var waited = 0;
            while (waited < ContractFreeMaxSeconds)
            {
                var height = Globals.LastBlock?.Height ?? 0;
                if (!VBTCWithdrawalRequest.HasActiveContractRequest(s3cUID, height))
                    return true;
                await Task.Delay(TimeSpan.FromSeconds(ContractFreePollSeconds));
                waited += ContractFreePollSeconds;
            }
            // Final check after the loop.
            return !VBTCWithdrawalRequest.HasActiveContractRequest(s3cUID, Globals.LastBlock?.Height ?? 0);
        }

        // §12.4: dedicated poll of the companion deposit address for the confirmed arrival delta.
        private static async Task<decimal> WaitForBtcArrival(string companionScUID, string requester, decimal before)
        {
            var waited = 0;
            while (waited < BtcArrivalMaxSeconds)
            {
                var bal = await VBTCService.TryGetAvailableTransparentVbtcBalance(companionScUID, requester);
                if (bal.success && bal.availableBalance > before)
                    return bal.availableBalance - before;
                await Task.Delay(TimeSpan.FromSeconds(BtcArrivalPollSeconds));
                waited += BtcArrivalPollSeconds;
            }
            return 0M;
        }

        // §12.4: poll ETH gas on the derived Base address for up to 1h.
        private static async Task<bool> WaitForGas(S3CAutoBridgeState s)
        {
            if (string.IsNullOrEmpty(s.BaseGasAddress)) return false;
            var waited = 0;
            while (waited < GasMaxSeconds)
            {
                var eth = await BaseBridgeService.GetEthBalanceAsync(s.BaseGasAddress);
                if (eth.Success) s.BaseGasEthBalance = eth.BalanceEth;
                if (eth.Success && eth.BalanceEth > 0M) return true;
                await Task.Delay(TimeSpan.FromSeconds(GasPollSeconds));
                waited += GasPollSeconds;
            }
            return false;
        }
    }
}
