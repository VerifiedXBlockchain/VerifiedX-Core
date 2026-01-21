namespace ReserveBlockCore.Models
{
    public class MessageLock
    {
        public int BufferCost;

        public int ConnectionCount;

        public long LastRequestTime;

        public int DelayLevel;

        // HAL-16 Fix: Separate semaphores per message type to prevent TXs from blocking blocks
        // Block processing has highest priority - never wait for TXs
        public readonly SemaphoreSlim BlockSemaphore = new SemaphoreSlim(1, 1);
        
        // Allow 25 concurrent TX validations for better throughput (increased from 3 to handle batch broadcasts)
        public readonly SemaphoreSlim TxSemaphore = new SemaphoreSlim(25, 25);
        
        // General queries and other operations
        public readonly SemaphoreSlim QuerySemaphore = new SemaphoreSlim(2, 2);
        
        // Legacy semaphore - kept for backward compatibility but not used
        [Obsolete("Use message-type-specific semaphores instead")]
        public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
    }
}
