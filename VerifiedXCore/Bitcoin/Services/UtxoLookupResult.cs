using VerifiedXCore.Bitcoin.ElectrumX.Results;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Which data source produced an authoritative UTXO answer. ElectrumX is the only spend source;
    /// public Esplora APIs are used strictly as an advisory cross-check and never appear here.
    /// </summary>
    public enum UtxoSource
    {
        None,
        Electrum
    }

    /// <summary>
    /// Outcome of a resilient UTXO lookup. Distinguishes the three states a bare list cannot:
    /// - Success with UTXOs: coins found via Electrum.
    /// - Success with an empty set: CONFIRMED empty (Electrum quorum, or Electrum + advisory cross-check agreeing).
    /// - Success false: the lookup is inconclusive (servers unreachable / uncorroborated); callers
    ///   must treat this as transient and retryable, never as "the address has no coins".
    /// </summary>
    public sealed class UtxoLookupResult
    {
        public bool Success { get; private set; }
        public UtxoSource Source { get; private set; }
        public List<BlockchainScripthashListunspentResult> Utxos { get; private set; } = new();
        public int ServersTried { get; private set; }
        public string Error { get; private set; } = string.Empty;

        public bool HasUtxos => Success && Utxos.Count > 0;
        public bool IsConfirmedEmpty => Success && Utxos.Count == 0;

        public static UtxoLookupResult Found(List<BlockchainScripthashListunspentResult> utxos, UtxoSource source, int serversTried)
        {
            return new UtxoLookupResult { Success = true, Source = source, Utxos = utxos, ServersTried = serversTried };
        }

        public static UtxoLookupResult ConfirmedEmpty(int serversTried)
        {
            return new UtxoLookupResult { Success = true, Source = UtxoSource.Electrum, ServersTried = serversTried };
        }

        public static UtxoLookupResult Failed(string error, int serversTried)
        {
            return new UtxoLookupResult { Success = false, Source = UtxoSource.None, ServersTried = serversTried, Error = error };
        }
    }
}
