namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// The Poseidon hashes the v1 PLONK circuits compute, through the native library's circuit-compatible exports
    /// (fund-loss audit item 1, stage 2). Inputs and outputs are canonical little-endian field elements
    /// (<see cref="PrivacyField"/>). These are the ONLY hashes that may produce a note hash, a Merkle parent or a
    /// nullifier for the proof-rules epoch: <c>PlonkNative.poseidon_hash</c> (chained, big-endian) does not match the
    /// circuits and every value it produced before the epoch can never be proven.
    /// </summary>
    public static class PoseidonV1
    {
        private static readonly object ZeroLock = new();
        private static byte[][]? _zeros;

        /// <summary>Whether the loaded native library has the circuit-compatible exports (set by the stage-2 library).</summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    var o = new byte[PlonkNative.ScalarSize];
                    return PlonkNative.poseidon_hash_2(PrivacyField.Zero32, PrivacyField.Zero32, o) == PlonkNative.Success;
                }
                catch (EntryPointNotFoundException) { return false; }
                catch (DllNotFoundException) { return false; }
            }
        }

        private static byte[] Call(Func<byte[], int> native, string what)
        {
            var out32 = new byte[PlonkNative.ScalarSize];
            var code = native(out32);
            if (code != PlonkNative.Success)
                throw new InvalidOperationException($"{what} failed: native code {code} (inputs must be canonical 32-byte little-endian field elements).");
            return out32;
        }

        private static void RequireCanonical(ReadOnlySpan<byte> fr, string name)
        {
            if (!PrivacyField.IsCanonicalLe(fr))
                throw new ArgumentException($"{name} must be a canonical 32-byte little-endian field element.", name);
        }

        /// <summary><c>Poseidon(left, right)</c>: the Merkle parent hash.</summary>
        public static byte[] Hash2(byte[] left, byte[] right)
        {
            RequireCanonical(left, nameof(left));
            RequireCanonical(right, nameof(right));
            return Call(o => PlonkNative.poseidon_hash_2(left, right, o), "poseidon_hash_2");
        }

        /// <summary><c>Poseidon(a, b, c)</c> (chained from zero): the nullifier hash.</summary>
        public static byte[] Hash3(byte[] a, byte[] b, byte[] c)
        {
            RequireCanonical(a, nameof(a));
            RequireCanonical(b, nameof(b));
            RequireCanonical(c, nameof(c));
            return Call(o => PlonkNative.poseidon_hash_3(a, b, c, o), "poseidon_hash_3");
        }

        /// <summary><c>note_hash = Poseidon(amount_scaled, randomness)</c>: the v1 (ownerless) leaf. No v2 circuit uses it; kept for tests and the probe harness.</summary>
        public static byte[] NoteHash(ulong amountScaled, byte[] randomness32)
        {
            RequireCanonical(randomness32, nameof(randomness32));
            return Call(o => PlonkNative.poseidon_note_hash(amountScaled, randomness32, o), "poseidon_note_hash");
        }

        /// <summary>
        /// Domain separator of the owner key, <c>b"VFX_OWNR"</c> read little-endian: <c>owner_pk = Poseidon(OwnerPkDomain, nullifier_key)</c>.
        /// Must equal <c>verifiedx-circuits::gadgets::note_hash::OWNER_PK_DOMAIN</c>.
        /// </summary>
        public const ulong OwnerPkDomain = 0x524E574F5F584656UL;

        /// <summary><c>owner_pk = Poseidon(OwnerPkDomain, nullifier_key)</c>: the owner key a v2 note carries (stage 3).</summary>
        public static byte[] OwnerPk(byte[] nullifierKeyCanonical32)
        {
            RequireCanonical(nullifierKeyCanonical32, nameof(nullifierKeyCanonical32));
            return Call(o => PlonkNative.poseidon_owner_pk(nullifierKeyCanonical32, o), "poseidon_owner_pk");
        }

        /// <summary><c>note_hash = Poseidon(amount_scaled, randomness, owner_pk)</c>: the v2 owner-bound leaf the circuits bind (stage 3).</summary>
        public static byte[] NoteHashV2(ulong amountScaled, byte[] randomness32, byte[] ownerPk32)
        {
            RequireCanonical(randomness32, nameof(randomness32));
            RequireCanonical(ownerPk32, nameof(ownerPk32));
            return Call(o => PlonkNative.poseidon_note_hash_v2(amountScaled, randomness32, ownerPk32, o), "poseidon_note_hash_v2");
        }

        /// <summary>
        /// <c>nullifier = Poseidon(nullifier_key, note_hash, position)</c> as the circuits' nullifier gadget computes it. In the
        /// v2 circuits the key is the owner's nullifier key, whose <see cref="OwnerPk"/> is inside the note.
        /// </summary>
        public static byte[] Nullifier(byte[] nullifierKeyCanonical32, byte[] noteHash32, ulong treePosition) =>
            Hash3(nullifierKeyCanonical32, noteHash32, PrivacyField.FromUInt64(treePosition));

        /// <summary>The digest of an empty subtree of height <paramref name="level"/> (0 = the zero field element, i.e. an empty leaf).</summary>
        public static byte[] MerkleZero(int level)
        {
            if (level < 0 || level > PlonkNative.TreeDepth)
                throw new ArgumentOutOfRangeException(nameof(level));
            lock (ZeroLock)
            {
                if (_zeros == null)
                {
                    var z = new byte[PlonkNative.TreeDepth + 1][];
                    for (var l = 0; l <= PlonkNative.TreeDepth; l++)
                        z[l] = Call(o => PlonkNative.poseidon_merkle_zero((uint)l, o), "poseidon_merkle_zero");
                    _zeros = z;
                }
                return (byte[])_zeros[level].Clone();
            }
        }

        /// <summary>Root from a leaf, its position and a flat <see cref="PlonkNative.TreeDepth"/> x 32-byte sibling path, computed natively.</summary>
        public static byte[] RootFromPath(byte[] leaf32, ulong position, byte[] path1024)
        {
            RequireCanonical(leaf32, nameof(leaf32));
            if (path1024 == null || path1024.Length != PlonkNative.ScalarSize * PlonkNative.TreeDepth)
                throw new ArgumentException($"Path must be {PlonkNative.ScalarSize * PlonkNative.TreeDepth} bytes.", nameof(path1024));
            return Call(o => PlonkNative.merkle_root_from_path_v1(leaf32, position, path1024, (nuint)path1024.Length, o), "merkle_root_from_path_v1");
        }
    }
}
