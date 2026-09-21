using VerifiedXCore.Utilities;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// WATCHDOG (Sep 2026, testnet 975,533): a stall detector that nothing but real chain progress
    /// can satisfy. The existing STALL-HEAL counter was reset by every "successful" round — and for
    /// 16 hours every round looked successful while its block was silently rejected — so it never
    /// fired. This watches one thing only: has <c>Globals.LastBlock.Height</c> changed. It is fed
    /// from the periodic loops on every node role; the pure <see cref="Evaluate"/> is unit-tested.
    ///
    /// Warn → a log line. Escalate → error log + console, and the recovery paths that are safe to
    /// poke from anywhere are kicked: the add-only caster heal and the majority-verdict fork check.
    /// Escalation repeats on a cadence so a long outage keeps making noise.
    /// </summary>
    public static class ChainProgressWatchdog
    {
        public const int WarnAfterSeconds = 180;
        public const int EscalateAfterSeconds = 600;
        public const int RepeatEverySeconds = 300;

        public enum Level { Moving, Quiet, Warn, Escalate }

        /// <summary>Tracking state — a separate instance per test; one static instance live.</summary>
        public sealed class State
        {
            public long LastHeight = -1;
            public long LastChangeUnix;
            public long LastWarnUnix;
            public long LastEscalateUnix;
        }

        private static readonly State _live = new();
        private static int _ticking;

        public static long StalledSeconds
        {
            get
            {
                var s = _live;
                if (s.LastHeight < 0 || s.LastChangeUnix <= 0) return 0;
                return Math.Max(0, TimeUtil.GetTime() - s.LastChangeUnix);
            }
        }

        public static bool IsStalled => StalledSeconds >= WarnAfterSeconds;

        /// <summary>
        /// Pure decision over <paramref name="s"/>. Returns Moving when the height changed (and
        /// resets the clocks), Warn/Escalate exactly when a notification is due, Quiet otherwise.
        /// </summary>
        public static Level Evaluate(State s, long height, long nowUnix)
        {
            if (height != s.LastHeight)
            {
                s.LastHeight = height;
                s.LastChangeUnix = nowUnix;
                s.LastWarnUnix = 0;
                s.LastEscalateUnix = 0;
                return Level.Moving;
            }

            var stalled = nowUnix - s.LastChangeUnix;
            if (stalled >= EscalateAfterSeconds)
            {
                if (s.LastEscalateUnix == 0 || nowUnix - s.LastEscalateUnix >= RepeatEverySeconds)
                {
                    s.LastEscalateUnix = nowUnix;
                    return Level.Escalate;
                }
                return Level.Quiet;
            }

            if (stalled >= WarnAfterSeconds && s.LastWarnUnix == 0)
            {
                s.LastWarnUnix = nowUnix;
                return Level.Warn;
            }

            return Level.Quiet;
        }

        /// <summary>Called from the periodic loops. Cheap; safe to call from any role at any cadence.</summary>
        public static async Task TickAsync(string caller)
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
                return;
            try
            {
                var tip = Globals.LastBlock;
                if (tip == null || tip.Height < 0)
                    return;

                var now = TimeUtil.GetTime();
                var level = Evaluate(_live, tip.Height, now);
                var stalled = now - _live.LastChangeUnix;

                switch (level)
                {
                    case Level.Warn:
                        LogUtility.Log(
                            $"[{caller}] WATCHDOG: no new block for {stalled}s (tip h={tip.Height} {Short(tip.Hash)}). " +
                            $"Escalating at {EscalateAfterSeconds}s.",
                            "ChainProgressWatchdog");
                        break;

                    case Level.Escalate:
                        var msg = $"[{caller}] WATCHDOG-STALL: no new block for {stalled}s (tip h={tip.Height} {Short(tip.Hash)}). " +
                                  $"casters={Globals.BlockCasters.Count} isCaster={Globals.IsBlockCaster} bootstrap={Globals.IsBootstrapMode} " +
                                  $"forkState={ForkDetectionService.SyncState}/{ForkDetectionService.ForkStatusText}. Kicking heal + fork check.";
                        ErrorLogUtility.LogError(msg, "ChainProgressWatchdog");
                        ConsoleWriterService.Output(msg);

                        if (Globals.IsBlockCaster)
                        {
                            try { await CasterMembershipService.TryHealBlockCastersFromRecordAsync(); }
                            catch (Exception ex) { LogUtility.Log($"WATCHDOG heal error: {ex.Message}", "ChainProgressWatchdog"); }
                        }
                        _ = ForkDetectionService.CheckAsync("ChainProgressWatchdog");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogUtility.Log($"WATCHDOG error: {ex.Message}", "ChainProgressWatchdog");
            }
            finally
            {
                Interlocked.Exchange(ref _ticking, 0);
            }
        }

        private static string Short(string? h) => string.IsNullOrEmpty(h) ? "" : h[..Math.Min(16, h.Length)];
    }
}
