using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Finishes reclaim withdrawals of this node's own wallet. A withdrawal whose Bitcoin tx took back coins of a
    /// withheld transaction is completed on VFX only after that tx confirms (VBTCService.CompleteWithdrawal records
    /// CompleteAfterBtcTxId and returns "awaiting confirmation"). This timer calls CompleteWithdrawal again for those
    /// rows, which submits the COMPLETE once the tx has confirmed, rebroadcasts it if no server knows it, or rebuilds
    /// if the withheld tx won. Local-only: reads its own rows, signs with its own keys, changes no consensus data.
    /// </summary>
    public class VBTCDeferredCompletionService
    {
        private static Timer? _timer;
        private static int _running;

        private const int INTERVAL_SECONDS = 120;

        public static void Start()
        {
            if (_timer != null)
                return;
            _timer = new Timer(_ => _ = RunOnce(), null, TimeSpan.FromSeconds(INTERVAL_SECONDS), TimeSpan.FromSeconds(INTERVAL_SECONDS));
            LogUtility.Log($"vBTC deferred-completion service started (interval: {INTERVAL_SECONDS}s)", "VBTCDeferredCompletionService.Start");
        }

        public static async Task RunOnce()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return; // previous pass still running
            try
            {
                if (Globals.StopAllTimers)
                    return;

                var rows = VBTCWithdrawalRequest.GetVBTCWithdrawalRequestDb()?.Query()
                    .Where(x => !x.IsCompleted)
                    .ToList()
                    .Where(x => !string.IsNullOrEmpty(x.CompleteAfterBtcTxId) && !string.IsNullOrEmpty(x.TransactionHash))
                    .ToList() ?? new List<VBTCWithdrawalRequest>();

                foreach (var row in rows)
                {
                    if (AccountData.GetSingleAccount(row.RequestorAddress) == null)
                        continue; // a web wallet's row on this API node: the wallet completes it itself

                    var (success, vfxTxHash, _, error, _) = await VBTCService.CompleteWithdrawal(row.SmartContractUID, row.TransactionHash);
                    if (success)
                        LogUtility.Log($"Deferred completion submitted for withdrawal {row.TransactionHash} on {row.SmartContractUID}: {vfxTxHash}", "VBTCDeferredCompletionService.RunOnce");
                    else if (!error.StartsWith(VBTCService.AwaitingConfirmationMarker))
                        LogUtility.Log($"Deferred completion for withdrawal {row.TransactionHash} on {row.SmartContractUID}: {error}", "VBTCDeferredCompletionService.RunOnce");
                }
            }
            catch (Exception ex)
            {
                ErrorLogUtility.LogError($"Deferred completion pass failed: {ex.Message}", "VBTCDeferredCompletionService.RunOnce");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
