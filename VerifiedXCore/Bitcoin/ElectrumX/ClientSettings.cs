using NBitcoin;

namespace VerifiedXCore.Bitcoin.ElectrumX
{
    public class ClientSettings
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
        public ulong Count { get; set; }

        /// <summary>Consecutive failed attempts; reset by the next success. Drives the cooldown (see ElectrumServerPool).</summary>
        public ulong FailCount { get; set; }

        /// <summary>Not offered to callers before this time (UTC) after a failure. The health probe may still try it.</summary>
        public DateTime CooldownUntilUtc { get; set; }

        public DateTime? LastSuccessUtc { get; set; }

        /// <summary>Round trip of the last successful attempt; the pool prefers the fastest healthy server.</summary>
        public long? LastLatencyMs { get; set; }

        /// <summary>Server's block height at the last health probe.</summary>
        public int? TipHeight { get; set; }

        /// <summary>More than ElectrumServerPool.MaxTipLagBlocks behind the best tip at the last probe; used only as a last resort.</summary>
        public bool IsLagging { get; set; }

        public string Label => $"{Host}:{Port}";
    }
}
