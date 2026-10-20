namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// A background loop that pauses while <see cref="Globals.StopAllTimers"/> is set and goes on once it is cleared.
    /// Startup holds StopAllTimers true until the first sync ends (and a rollback holds it while it runs); a loop written as
    /// <c>while (!Globals.StopAllTimers)</c> ended at its first look during a sync longer than its interval and never ran
    /// again, so the Base exit watcher stopped seeing burns. Every shutdown path exits the process after setting the flag,
    /// which ends the loop.
    /// </summary>
    public static class PausableLoop
    {
        public static async Task RunAsync(Func<Task> step, int intervalMs, string name, CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!Globals.StopAllTimers)
                {
                    try { await step(); }
                    catch (Exception ex) { LogUtility.Log($"[{name}] loop step failed: {ex.Message}", name); }
                }
                try { await Task.Delay(intervalMs, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
