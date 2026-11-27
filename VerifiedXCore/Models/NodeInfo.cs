using Microsoft.AspNetCore.SignalR.Client;
using VerifiedXCore.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VerifiedXCore.Models
{
    public class NodeInfo
    {
        public HubConnection Connection;

        /// <summary>
        /// Puts <paramref name="replacement"/> in place and disposes the connection it replaces. A replaced connection was
        /// left as it was: nothing held it any more, but its socket, its buffers and its receive loop stayed until the peer
        /// closed it. Returns the disposal, which runs on its own.
        /// </summary>
        public Task ReplaceConnection(HubConnection replacement)
        {
            var replaced = Connection;
            Connection = replacement;
            if (replaced == null || ReferenceEquals(replaced, replacement))
                return Task.CompletedTask;
            var disposal = DisposeQuietly(replaced);
            SafeTask.Forget(disposal, "NodeInfo.ReplaceConnection");
            return disposal;
        }

        /// <summary>Disposes a connection nobody will use, whatever state it is in.</summary>
        public static async Task DisposeQuietly(HubConnection? connection)
        {
            if (connection == null) return;
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        public string NodeIP { get; set; } 

        public string Address { get; set; }

        public long NodeHeight { get; set; }

        public int MethodCode = -100;

        public bool IsFinalized = false;

        public long LastMethodCodeTime { get; set; }
        public int NodeLatency { get; set; }
        public DateTime? NodeLastChecked { get; set; }

        public int IsSendingBlock;
        
        public long SendingBlockTime;
        
        public long TotalDataSent;              
        public bool IsConnected { get { return Connection?.State == HubConnectionState.Connected; } }
        public bool IsValidator { get; set; }
        public string? WalletVersion { get; set; }


        public string[] Queue { get
            {
                return invokeQueue.Select(x => x.Item4).ToArray();
            } }

        private Task InvokeDelay = Task.CompletedTask;

        private int ProcessQueueLock = 0;

        private ConcurrentQueue<(Func<CancellationToken, Task<object>> invokeFunc, Func<CancellationToken> ctFunc, Action<object> setResult, string)> invokeQueue =
            new ConcurrentQueue<(Func<CancellationToken, Task<object>> invokeFunc, Func<CancellationToken> ctFunc, Action<object> setResult, string)>();

        private async Task ProcessQueue()
        {
            if (Interlocked.Exchange(ref ProcessQueueLock, 1) == 1)
                return;

            while(invokeQueue.Count != 0)
            {
                try
                {
                    if (invokeQueue.TryDequeue(out var RequestInfo))
                    {
                        var Fail = true;
                        try
                        {
                            var token = RequestInfo.ctFunc();
                            if(token.IsCancellationRequested)
                            {
                                RequestInfo.setResult(default);
                                continue;
                            }

                            if (Globals.AdjudicateAccount == null)
                            {
                                await InvokeDelay;                                
                            }

                            var timer = new Stopwatch();
                            timer.Start();
                            var Result = await RequestInfo.invokeFunc(token);
                            timer.Stop();
                            InvokeDelay = Task.Delay(1000);

                            if (timer.ElapsedMilliseconds > 3000)
                                LogUtility.LogQueue(RequestInfo.Item4 + " " + timer.ElapsedMilliseconds, "ProcessQueue", "slowrequest.txt", true);
                            RequestInfo.setResult(Result);                            
                            Fail = false;
                        }
                        catch { }
                        if (Fail)
                            RequestInfo.setResult(default);
                    }
                }
                catch { }
            }

            Interlocked.Exchange(ref ProcessQueueLock, 0);
            if (invokeQueue.Count != 0)
                await ProcessQueue();
        }

        public async Task<T> InvokeAsync<T>(string method, object[] args, Func<CancellationToken> ctFunc, string description)
        {
            try
            {
                if (Globals.AdjudicateAccount != null)
                    return await Connection.InvokeCoreAsync<T>(method, args, ctFunc());
                var Source = new TaskCompletionSource<T>();
                var InvokeFunc = async (CancellationToken ct) => {
                    try { return Connection != null ? (object)(await Connection.InvokeCoreAsync<T>(method, args, ct)) : (object)default(T); }
                    catch { } return (object)default(T); };
                invokeQueue.Enqueue((InvokeFunc, ctFunc, (object x) => Source.SetResult((T)x), TimeUtil.GetMillisecondTime() + " " + description));
                _ = ProcessQueue();

                var Result = await Source.Task;                
                return Result;
            }
            catch { }

            return default;
        }
    }
}
