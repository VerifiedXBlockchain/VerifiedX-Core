using Microsoft.AspNetCore.SignalR.Client;

namespace VerifiedXCore.P2P
{
    public class RetryPolicy : IRetryPolicy
    {
        private const int ReconnectionWaitSeconds = 4;

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            return TimeSpan.FromSeconds(ReconnectionWaitSeconds);
        }
    }
}
