using System.Collections.Concurrent;

namespace ReserveBlockCore.Privacy
{
    /// <summary>
    /// Per-wallet semaphore pool that serialises all load-modify-save cycles
    /// on a <see cref="Models.Privacy.ShieldedWallet"/> row, preventing
    /// concurrent writes from clobbering each other (auto-scanner vs API).
    /// </summary>
    public static class ShieldedWalletLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

        /// <summary>Returns (or creates) the per-address semaphore.</summary>
        public static SemaphoreSlim For(string zfxAddress) =>
            _locks.GetOrAdd(zfxAddress, _ => new SemaphoreSlim(1, 1));

        /// <summary>Acquire the lock synchronously (auto-scanner path).</summary>
        public static void Wait(string zfxAddress) =>
            For(zfxAddress).Wait();

        /// <summary>Release the lock.</summary>
        public static void Release(string zfxAddress)
        {
            if (_locks.TryGetValue(zfxAddress, out var sem))
                try { sem.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>Acquire the lock asynchronously.</summary>
        public static Task WaitAsync(string zfxAddress, CancellationToken ct = default) =>
            For(zfxAddress).WaitAsync(ct);
    }
}