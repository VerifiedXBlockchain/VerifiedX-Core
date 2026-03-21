using System.Collections.Concurrent;
using System.Numerics;

namespace ReserveBlockCore
{
    public static partial class Globals
    {
        public static decimal PrivateTxFixedFee { get; set; } = 0.000003M;
        public static int MaxPrivateTxPerBlock { get; set; } = 50;
        public static int MaxMerkleRootAge { get; set; } = 100;
        public static int MaxPrivateTxInputs { get; set; } = 2;
        public static int MaxPrivateTxOutputs { get; set; } = 2;
        public static int MaxPrivateTxDataSize { get; set; } = 8192;
        public static decimal MinShieldAmountVFX { get; set; } = 0.001M;
        public static decimal MinShieldAmountVBTC { get; set; } = 0.00001M;

        /// <summary>Nullifier (Base64) -> consuming TX hash.</summary>
        public static ConcurrentDictionary<string, string> MempoolNullifiers { get; } = new();

        /// <summary>Size in bytes of the loaded PLONK params file (diagnostic only — native FFI holds the actual data).</summary>
        public static long PLONKParamsFileSize { get; set; }

        /// <summary>When true, ZK-authorized private txs must carry a non-empty <c>proof_b64</c> (mempool / block validation).</summary>
        public static bool EnforcePlonkProofsForZk { get; set; }

        /// <summary>Circuit amounts use 10^18 fixed-point (see implementation plan).</summary>
        public const long PrivacyAmountScalingFactor = 1_000_000_000_000_000_000L;

        public static BigInteger ToCircuitAmount(decimal amount) =>
            (BigInteger)(amount * PrivacyAmountScalingFactor);

        public static decimal FromCircuitAmount(BigInteger circuitAmount) =>
            (decimal)circuitAmount / PrivacyAmountScalingFactor;
    }
}
