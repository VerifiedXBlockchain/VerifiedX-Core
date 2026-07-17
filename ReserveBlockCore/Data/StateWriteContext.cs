namespace ReserveBlockCore.Data
{
    /// <summary>
    /// Ambient block height used to stamp <c>LastModifiedHeight</c> on AccountStateTrei /
    /// SmartContractStateTrei records as they are written (see
    /// <see cref="Extensions.StateTreiStampExtensions"/>).
    ///
    /// Set by StateData.UpdateTreis for the block being applied, and by rollback/reserve paths.
    /// When no height is set, writes fall back to <c>Globals.LastBlock.Height + 1</c> — an
    /// intentional over-stamp: a too-high stamp only causes an unchanged record to be copied
    /// into the next snapshot cycle (harmless), while a too-low stamp would make the snapshot
    /// diff miss the change (silent corruption).
    ///
    /// All state mutations run under the block-processing/recovery guards (single writer),
    /// so a simple static is sufficient.
    /// </summary>
    public static class StateWriteContext
    {
        private const long Unset = -1;
        private static long _currentHeight = Unset;

        public static void SetHeight(long height) => Interlocked.Exchange(ref _currentHeight, height);

        public static void Clear() => Interlocked.Exchange(ref _currentHeight, Unset);

        /// <summary>Height to stamp on a state record being written right now.</summary>
        public static long StampHeight
        {
            get
            {
                var h = Interlocked.Read(ref _currentHeight);
                return h != Unset ? h : Globals.LastBlock.Height + 1;
            }
        }
    }
}
