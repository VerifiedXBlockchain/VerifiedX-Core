using System.Collections.Concurrent;

namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// Per-wallet semaphore pool that serialises all load-modify-save cycles
    /// on a <see cref="Models.Privacy.ShieldedWallet"/> row, preventing
    /// concurrent writes from clobbering each other (auto-scanner vs API).
    /// </summary>
    public static class ShieldedWalletLock
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns (or creates) the semaphore of the wallet row the address names. A row is found by either spelling of
        /// its address (v1 or v2) and the auto-scanner locks the stored one: a caller locking the other spelling took
        /// another semaphore and wrote over the scanner's changes (round 25).
        /// </summary>
        public static SemaphoreSlim For(string zfxAddress) =>
            _locks.GetOrAdd(KeyOf(zfxAddress), _ => new SemaphoreSlim(1, 1));

        private static string KeyOf(string zfxAddress)
        {
            try { return ShieldedWalletService.FindByZfxAddress(zfxAddress)?.ShieldedAddress ?? zfxAddress; }
            catch { return zfxAddress; }
        }

        /// <summary>Runs <paramref name="body"/> holding the lock of the wallet row the address names.</summary>
        public static T Holding<T>(string? zfxAddress, Func<T> body)
        {
            if (string.IsNullOrWhiteSpace(zfxAddress)) return body();
            var sem = For(zfxAddress);
            sem.Wait();
            try { return body(); }
            finally { try { sem.Release(); } catch (SemaphoreFullException) { } }
        }

        /// <summary>
        /// Takes the lock of the wallet row the address names and returns the semaphore taken: the caller releases that
        /// one. Resolving the address again at release could name another row (one created meanwhile under the other
        /// spelling), leaving this lock held for good and the auto scanner waiting on it (round 26).
        /// </summary>
        public static SemaphoreSlim Acquire(string zfxAddress)
        {
            var sem = For(zfxAddress);
            sem.Wait();
            return sem;
        }

        /// <summary>Releases a semaphore <see cref="Acquire"/> returned.</summary>
        public static void Release(SemaphoreSlim held)
        {
            try { held.Release(); } catch (SemaphoreFullException) { }
        }
    }
}