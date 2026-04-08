using System.Collections.Concurrent;
using System.Linq;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    public static class ConsensusAttestationStore
    {
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, CasterAttestation>> ByHeight = new();
        public const int MaxPerHeight = 12;
        private const int RetainHeightsBehindTip = 64;

        public static void Prune(long lastCommittedHeight)
        {
            var floor = lastCommittedHeight - RetainHeightsBehindTip;
            foreach (var key in ByHeight.Keys)
            {
                if (key < floor)
                    ByHeight.TryRemove(key, out _);
            }
        }

        public static bool TryAdd(long height, string casterAddress, CasterAttestation attestation, out string? error)
        {
            error = null;
            Prune(Globals.LastBlock.Height);

            var inner = ByHeight.GetOrAdd(height, _ => new ConcurrentDictionary<string, CasterAttestation>(StringComparer.Ordinal));
            if (inner.Count >= MaxPerHeight)
            {
                error = "Attestation cap for height";
                return false;
            }

            if (!inner.TryAdd(casterAddress, attestation))
            {
                error = "Duplicate attestation for caster";
                return false;
            }

            return true;
        }

        public static IReadOnlyList<CasterAttestation> GetForHeight(long height)
        {
            if (!ByHeight.TryGetValue(height, out var inner))
                return Array.Empty<CasterAttestation>();
            return inner.Values.ToList();
        }
    }
}
