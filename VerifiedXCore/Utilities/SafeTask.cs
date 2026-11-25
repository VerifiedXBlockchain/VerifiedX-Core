namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// Work started and not awaited. An exception thrown by an <c>async void</c> method is raised on the thread pool,
    /// where nothing can catch it, and ends the process; work handed to <see cref="Forget"/> starts the same way (on the
    /// caller's thread, up to its first await) but a fault is logged and the node runs on.
    /// </summary>
    public static class SafeTask
    {
        private static long _faults;

        /// <summary>Faults logged since the process started.</summary>
        public static long Faults => Interlocked.Read(ref _faults);

        /// <summary>Lets <paramref name="task"/> run; if it fails, the failure is logged under <paramref name="where"/>.</summary>
        public static void Forget(Task task, string where)
        {
            if (task == null) return;
            if (task.IsCompleted)
            {
                if (task.IsFaulted) Log(task.Exception, where);
                return;
            }
            task.ContinueWith(t => Log(t.Exception, where), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void Log(AggregateException? ex, string where)
        {
            Interlocked.Increment(ref _faults);
            var message = $"Background work {where} failed: {ex?.GetBaseException().Message}";
            try { Console.Error.WriteLine(message); } catch { }
            // The error log's own writer is work of this kind: its failure is not logged through itself.
            if (where.StartsWith("ErrorLogUtility.", StringComparison.Ordinal)) return;
            try { ErrorLogUtility.LogError(message, where); } catch { }
        }
    }
}
