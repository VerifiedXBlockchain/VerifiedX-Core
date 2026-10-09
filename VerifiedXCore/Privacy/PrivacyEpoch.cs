namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// The proof-rules epoch of the shielded pool (fund-loss audit item 1, stage 2; Globals.PrivateTxProofRulesHeight).
    ///
    /// Before the height nothing changes: the chained-Poseidon note hashes, the dynamic-depth tree and proof-less
    /// private transactions stay exactly as mined, and replay reproduces them. At the height every shielded pool is
    /// reset (its notes can never be proven under the circuits: their on-chain note hashes are not what the circuits
    /// compute), the circuit-compatible tree starts with the public dummy note at leaf 0, and from then on a private
    /// transaction must carry a proof that verifies against its on-chain fields.
    ///
    /// The dummy note (amount 0, randomness 0, owner key of nullifier key 0, spent with nullifier key 0) fills the circuits' second input when a wallet
    /// spends a single note. Its nullifier is one public constant: consensus never records it as spent and never counts
    /// it as a spend, and a transaction may carry it at most once.
    /// </summary>
    public static class PrivacyEpoch
    {
        private static readonly object Lock = new();
        private static byte[]? _dummyOwnerPk;
        private static byte[]? _dummyNoteHash;
        private static byte[]? _dummyNullifier;
        private static byte[]? _dummyCommitment;

        /// <summary>Whether the proof rules (and the new tree) apply to a transaction mined at <paramref name="height"/>.</summary>
        public static bool ProofRulesActive(long height) => height >= Globals.PrivateTxProofRulesHeight;

        /// <summary>The one block in which the pools are reset. No private transaction is valid in it.</summary>
        public static bool IsResetBlock(long height) => height == Globals.PrivateTxProofRulesHeight;

        /// <summary><c>Poseidon(OwnerPkDomain, 0)</c>: the dummy note's owner key (nullifier key 0).</summary>
        public static byte[] DummyOwnerPk
        {
            get { lock (Lock) { return (byte[])(_dummyOwnerPk ??= PoseidonV1.OwnerPk(PrivacyField.Zero32)).Clone(); } }
        }

        /// <summary><c>Poseidon(0, 0, DummyOwnerPk)</c>: the dummy note's hash, leaf 0 of every epoch tree (v2 owner-bound note).</summary>
        public static byte[] DummyNoteHash
        {
            get { lock (Lock) { return (byte[])(_dummyNoteHash ??= PoseidonV1.NoteHashV2(0, PrivacyField.Zero32, DummyOwnerPk)).Clone(); } }
        }

        /// <summary><c>Poseidon(0, DummyNoteHash, 0)</c>: the one nullifier every single-note spend carries.</summary>
        public static byte[] DummyNullifier
        {
            get { lock (Lock) { return (byte[])(_dummyNullifier ??= PoseidonV1.Nullifier(PrivacyField.Zero32, DummyNoteHash, 0)).Clone(); } }
        }

        public static string DummyNullifierB64 => Convert.ToBase64String(DummyNullifier);

        /// <summary>The Pedersen commitment of the dummy note (amount 0, randomness 0), stored with leaf 0.</summary>
        public static byte[] DummyCommitment
        {
            get
            {
                lock (Lock)
                {
                    if (_dummyCommitment == null)
                    {
                        var g1 = new byte[PlonkNative.G1CompressedSize];
                        var code = PlonkNative.pedersen_commit(0, PrivacyField.Zero32, g1);
                        if (code != PlonkNative.Success) throw new InvalidOperationException($"pedersen_commit failed: {code}");
                        _dummyCommitment = g1;
                    }
                    return (byte[])_dummyCommitment.Clone();
                }
            }
        }

        public static bool IsDummyNullifier(string? nullifierB64) =>
            !string.IsNullOrEmpty(nullifierB64) && string.Equals(nullifierB64, DummyNullifierB64, StringComparison.Ordinal);
    }
}
