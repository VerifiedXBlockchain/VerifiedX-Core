using System.Collections.Generic;
using System.Linq;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Services
{
    public static class ConsensusAttestationStore
    {
        private static readonly object Mut = new object();
        private static readonly Dictionary<long, Dictionary<string, CasterAttestation>> Store = new();
        public const int MaxPerHeight = 12;
        private const int RetainHeightsBehindTip = 64;

        private static void PruneUnsafe(long lastCommittedHeight)
        {
            var floor = lastCommittedHeight - RetainHeightsBehindTip;
            foreach (var key in Store.Keys.Where(k => k < floor).ToList())
                Store.Remove(key);
        }

        public static void Prune(long lastCommittedHeight)
        {
            lock (Mut)
                PruneUnsafe(lastCommittedHeight);
        }

        public static bool TryAdd(long height, string casterAddress, CasterAttestation attestation, out string? error)
        {
            error = null;
            lock (Mut)
            {
                PruneUnsafe(Globals.LastBlock.Height);

                if (!Store.TryGetValue(height, out var inner))
                {
                    inner = new Dictionary<string, CasterAttestation>(StringComparer.Ordinal);
                    Store[height] = inner;
                }

                if (inner.Count >= MaxPerHeight)
                {
                    error = "Attestation cap for height";
                    return false;
                }

                if (inner.ContainsKey(casterAddress))
                {
                    error = "Duplicate attestation for caster";
                    return false;
                }

                inner[casterAddress] = attestation;
                return true;
            }
        }

        public static IReadOnlyList<CasterAttestation> GetForHeight(long height)
        {
            lock (Mut)
            {
                if (!Store.TryGetValue(height, out var inner))
                    return Array.Empty<CasterAttestation>();
                return inner.Values.ToList();
            }
        }
    }
}
