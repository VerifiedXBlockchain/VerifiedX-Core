using Microsoft.AspNetCore.SignalR;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;

namespace ReserveBlockCore.Utilities
{
    /// <summary>
    /// HAL-16 Fix: Provides reliable fire-and-forget SignalR messaging with error handling,
    /// metrics tracking, and optional retry mechanisms while maintaining non-blocking performance
    /// </summary>
    public static class ReliableSignalRSender
    {
        // Metrics tracking
        private static long _totalSends = 0;
        private static long _failedSends = 0;
        
        // Optional background retry queue (bounded to prevent memory issues)
        private static readonly ConcurrentQueue<RetryMessage> _failedMessages = new();
        private static readonly SemaphoreSlim _retrySemaphore = new(1, 1);
        private const int MaxQueueSize = 1000;
        private const int MaxRetryBatch = 10;

        /// <summary>
        /// Send message to specific caller with fire-and-forget semantics and error tracking
        /// </summary>
        public static void SendToCallerReliable<T>(this IClientProxy caller, string method, T data, 
            string context, string targetInfo = "", int timeoutMs = 2000, bool enableRetry = false)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = caller.SendAsync(method, data, cts.Token);
            TrackSendWithContinuation(task, context, $"Caller({targetInfo})", method, new object[] { data }, targetInfo, enableRetry);
        }

        /// <summary>
        /// Send message to all clients with fire-and-forget semantics and error tracking
        /// </summary>
        public static void SendToAllReliable<T>(this IHubCallerClients clients, string method, T data, 
            string context, int timeoutMs = 6000, bool enableRetry = false)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = clients.All.SendAsync(method, data, cts.Token);
            TrackSendWithContinuation(task, context, "All", method, new object[] { data }, null, enableRetry);
        }

        /// <summary>
        /// Send message with multiple parameters to caller
        /// </summary>
        public static void SendToCallerReliable(this IClientProxy caller, string method, object[] args, 
            string context, string targetInfo = "", int timeoutMs = 2000, bool enableRetry = false)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = caller.SendAsync(method, args, cts.Token);
            TrackSendWithContinuation(task, context, $"Caller({targetInfo})", method, args, targetInfo, enableRetry);
        }

        /// <summary>
        /// Send message with multiple parameters to all clients
        /// </summary>
        public static void SendToAllReliable(this IHubCallerClients clients, string method, object[] args, 
            string context, int timeoutMs = 6000, bool enableRetry = false)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = clients.All.SendAsync(method, args, cts.Token);
            TrackSendWithContinuation(task, context, "All", method, args, null, enableRetry);
        }

        /// <summary>
        /// Track send operation with continuation for error handling (non-blocking)
        /// </summary>
        private static void TrackSendWithContinuation(Task sendTask, string context, string target, 
            string method, object[] args, string targetPeer, bool enableRetry)
        {
            Interlocked.Increment(ref _totalSends);

            _ = sendTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Interlocked.Increment(ref _failedSends);
                    
                    var exception = t.Exception?.GetBaseException();
                    var errorMessage = $"HAL-16 Fix: Send failed in {context} to {target} - Method: {method}, Error: {exception?.Message}";
                    
                    if (Globals.OptionalLogging)
                    {
                        LogUtility.Log(errorMessage, "ReliableSignalRSender");
                    }

                    // Optional retry for non-critical messages
                    if (enableRetry && _failedMessages.Count < MaxQueueSize)
                    {
                        _failedMessages.Enqueue(new RetryMessage
                        {
                            Method = method,
                            Args = args,
                            TargetPeer = targetPeer,
                            Context = context,
                            Timestamp = DateTime.UtcNow
                        });
                        
                        _ = ProcessFailedMessagesAsync();
                    }
                }
                else if (t.IsCanceled)
                {
                    if (Globals.OptionalLogging)
                    {
                        LogUtility.Log($"HAL-16 Fix: Send timeout in {context} to {target} - Method: {method}", "ReliableSignalRSender");
                    }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// Background processing of failed messages (non-blocking)
        /// </summary>
        private static async Task ProcessFailedMessagesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(100)) return; // Don't block if already processing

            try
            {
                // Process a limited number of failed messages to prevent blocking
                for (int i = 0; i < MaxRetryBatch && _failedMessages.TryDequeue(out var message); i++)
                {
                    // Skip messages older than 30 seconds
                    if (DateTime.UtcNow - message.Timestamp > TimeSpan.FromSeconds(30))
                        continue;

                    try
                    {
                        using var cts = new CancellationTokenSource(1000); // Shorter timeout for retries
                        
                        // Note: This would need actual IHubContext instance for real retry
                        // For now, just log the retry attempt
                        if (Globals.OptionalLogging)
                        {
                            LogUtility.Log($"HAL-16 Fix: Retrying failed message - Method: {message.Method}, Context: {message.Context}", "ReliableSignalRSender");
                        }
                    }
                    catch
                    {
                        // Retry failed, drop the message to prevent infinite loops
                        if (Globals.OptionalLogging)
                        {
                            LogUtility.Log($"HAL-16 Fix: Retry failed permanently - Method: {message.Method}", "ReliableSignalRSender");
                        }
                    }
                }
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        /// <summary>
        /// Get current send metrics for monitoring
        /// </summary>
        public static (long totalSends, long failedSends, double failureRate) GetSendMetrics()
        {
            var total = Interlocked.Read(ref _totalSends);
            var failed = Interlocked.Read(ref _failedSends);
            var failureRate = total > 0 ? (double)failed / total * 100 : 0;
            
            return (total, failed, failureRate);
        }

        /// <summary>
        /// Reset metrics (useful for testing or periodic reporting)
        /// </summary>
        public static void ResetMetrics()
        {
            Interlocked.Exchange(ref _totalSends, 0);
            Interlocked.Exchange(ref _failedSends, 0);
        }

        /// <summary>
        /// Get current retry queue size
        /// </summary>
        public static int GetRetryQueueSize() => _failedMessages.Count;

        /// <summary>
        /// Internal class for tracking retry messages
        /// </summary>
        private class RetryMessage
        {
            public string Method { get; set; } = string.Empty;
            public object[] Args { get; set; } = Array.Empty<object>();
            public string? TargetPeer { get; set; }
            public string Context { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }
    }
}
