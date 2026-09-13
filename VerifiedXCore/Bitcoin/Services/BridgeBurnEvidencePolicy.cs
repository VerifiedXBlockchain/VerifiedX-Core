namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Where Base RPC evidence may be consulted.
    ///
    /// Consensus rules must be deterministic: every node validating a block must reach the same
    /// verdict from the same inputs. A live RPC call inside block validation breaks that — an
    /// outage, a reorg, or two nodes on different providers makes one block valid on one node and
    /// invalid on another (chain split). So block validation relies on the committee-bound caster
    /// votes carried by the transaction (the casters verified the burn on Base before signing), and
    /// the RPC lookup is an ADDITIONAL check performed only when a transaction is first admitted
    /// (mempool / API), where a wrong answer costs a retry rather than a fork.
    /// </summary>
    public static class BridgeBurnEvidencePolicy
    {
        /// <param name="blockHeight">Non-null when validating a transaction as part of a block.</param>
        /// <param name="bridgeConfigured">Whether this node has a Base RPC + contract configured.</param>
        public static bool ShouldQueryBase(long? blockHeight, bool bridgeConfigured)
            => blockHeight == null && bridgeConfigured;
    }
}
