namespace ReserveBlockCore.Services
{
    /// <summary>Refreshes hardcoded/seed caster peers and <see cref="Globals.KnownCasters"/> (plan §7.3).</summary>
    public static class CasterDiscoveryService
    {
        private static long _lastRefreshAtHeight = -1;

        /// <summary>Idempotent refresh when crossing a 100-block boundary (caster monitor loop).</summary>
        public static async Task RefreshIfDueAsync()
        {
            var h = Globals.LastBlock.Height;
            if (h <= 0 || h % 100 != 0 || h == _lastRefreshAtHeight)
                return;
            _lastRefreshAtHeight = h;
            await Nodes.ValidatorNode.GetBlockcasters().ConfigureAwait(false);
            Globals.SyncKnownCastersFromBlockCasters();
        }
    }
}
