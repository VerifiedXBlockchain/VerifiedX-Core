using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    public class ReserveService
    {
        static SemaphoreSlim RunLock = new SemaphoreSlim(1, 1);
        static SemaphoreSlim RunUnlockWipeLock = new SemaphoreSlim(1, 1);

        public static async Task Run()
        {
            try
            {
                await RunLock.WaitAsync();
                var latestBlockTime = Globals.LastBlock.Timestamp;
                var rTXDb = ReserveTransactions.GetReserveTransactionsDb();
                if (rTXDb != null)
                {
                    var reserveTxList = rTXDb.Query().Where(x => x.ConfirmTimestamp < latestBlockTime && x.ReserveTransactionStatus == ReserveTransactionStatus.Pending).ToList();
                    if (reserveTxList.Count() > 0)
                    {
                        // Awaited so RunLock actually covers the work — a fire-and-forget here let
                        // the next block's Run() re-select still-Pending rows and double-apply them
                        // (which for vBTC transfers would mint ledger entries).
                        await StateData.UpdateTreiFromReserve(reserveTxList);
                    }
                }
            }
            finally
            {
                RunLock.Release();
            }
        }

        public static async Task RunUnlockWipe()
        {
            // Loops forever: this sweep is the ONLY thing that expires session unlock keys,
            // which gate reserve sends (including the vBTC exit path). As a one-shot it ran
            // once at boot — when the dictionary is necessarily empty — so an unlocked
            // reserve account stayed "unlocked" for the life of the process.
            while (true)
            {
                // Acquire OUTSIDE the try: if WaitAsync ever threw inside it (shutdown/dispose),
                // the finally would Release an unheld semaphore → SemaphoreFullException from
                // the finally escapes the loop and this task dies silently forever.
                await RunUnlockWipeLock.WaitAsync();
                try
                {
                    var delay = Task.Delay(new TimeSpan(0, 1, 0));

                    if(Globals.ReserveAccountUnlockKeys.Any())
                    {
                        foreach(var rAUK in  Globals.ReserveAccountUnlockKeys)
                        {
                            if(rAUK.Value.DeleteAfterTime < TimeUtil.GetTime())
                            {
                                Globals.ReserveAccountUnlockKeys.TryRemove(rAUK.Key, out _);
                            }
                        }
                    }

                    await delay;
                }
                catch { }
                finally { RunUnlockWipeLock.Release(); }
            }
        }

    }
}
