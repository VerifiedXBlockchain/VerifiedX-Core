using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VerifiedXCore.Bitcoin.Attestation
{
    /// <summary>
    /// SHA-256 taken up part-way through a message. The hash works through a message 64 bytes at a time and carries
    /// eight words of state from one block to the next; given that state after some whole number of blocks, the rest
    /// of the message and the message's total length, anyone can finish the hash and compare the result.
    ///
    /// That proves two things about a message whose hash is known, without the whole message: how it ends, and how
    /// long it is (the length is hashed in with the last block). The report verifier uses the second: a node of a
    /// Bitcoin block's Merkle tree whose content is longer than 64 bytes is a transaction, because every inner node is
    /// the hash of exactly 64 bytes. So the depth of a block's tree can be shown from its coinbase transaction without
    /// carrying the coinbase, however large a miner made it (<see cref="BtcTxInclusion.VerifyCoinbaseTail"/>).
    ///
    /// Nobody can invent a state that leads to a chosen hash: that is finding a preimage for the hash's compression
    /// function, which is what its security rests on.
    /// </summary>
    public static class BtcSha256
    {
        private static readonly uint[] K =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        private static readonly uint[] Initial =
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        };

        /// <summary>The state is eight 32-bit words, written big-endian: 32 bytes, 64 hex characters.</summary>
        public const int StateBytes = 32;

        /// <summary>
        /// The state after the first <paramref name="wholeBlocks"/> 64-byte blocks of <paramref name="message"/>,
        /// lowercase hex. What a report builder computes from a transaction it holds.
        /// </summary>
        public static string StateAfter(ReadOnlySpan<byte> message, int wholeBlocks)
        {
            if (wholeBlocks < 0 || (long)wholeBlocks * 64 > message.Length) throw new ArgumentOutOfRangeException(nameof(wholeBlocks));
            var state = (uint[])Initial.Clone();
            for (var i = 0; i < wholeBlocks; i++)
                Compress(state, message.Slice(i * 64, 64));
            var bytes = new byte[StateBytes];
            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), state[i]);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>
        /// Finishes a SHA-256 from <paramref name="state"/> (the state after <c>totalLength - tail.Length</c> bytes,
        /// which must be a whole number of blocks) over <paramref name="tail"/>, for a message of
        /// <paramref name="totalLength"/> bytes. Null when the arguments do not describe such a message.
        /// </summary>
        public static byte[]? Finish(ReadOnlySpan<byte> state, ReadOnlySpan<byte> tail, long totalLength)
        {
            if (state.Length != StateBytes || totalLength < tail.Length) return null;
            var done = totalLength - tail.Length;
            if (done % 64 != 0) return null;

            var words = new uint[8];
            for (var i = 0; i < 8; i++)
                words[i] = BinaryPrimitives.ReadUInt32BigEndian(state.Slice(i * 4, 4));

            // The tail, then the padding every SHA-256 ends with: a 1 bit, zeros, and the message length in bits.
            var padded = new byte[(tail.Length + 9 + 63) / 64 * 64];
            tail.CopyTo(padded);
            padded[tail.Length] = 0x80;
            BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), checked((ulong)totalLength * 8));
            for (var offset = 0; offset < padded.Length; offset += 64)
                Compress(words, padded.AsSpan(offset, 64));

            var digest = new byte[32];
            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), words[i]);
            return digest;
        }

        /// <summary>The whole hash, by this implementation (the tests hold it to the platform's).</summary>
        public static byte[] Hash(ReadOnlySpan<byte> message)
        {
            var blocks = message.Length / 64;
            return Finish(Convert.FromHexString(StateAfter(message, blocks)), message.Slice(blocks * 64), message.Length)!;
        }

        /// <summary>The second hash of Bitcoin's double SHA-256.</summary>
        public static byte[] Again(byte[] digest) => SHA256.HashData(digest);

        private static void Compress(uint[] state, ReadOnlySpan<byte> block)
        {
            Span<uint> w = stackalloc uint[64];
            for (var i = 0; i < 16; i++)
                w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
            for (var i = 16; i < 64; i++)
            {
                var s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                var s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = unchecked(w[i - 16] + s0 + w[i - 7] + s1);
            }

            uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
            for (var i = 0; i < 64; i++)
            {
                var s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                var ch = (e & f) ^ (~e & g);
                var t1 = unchecked(h + s1 + ch + K[i] + w[i]);
                var s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var t2 = unchecked(s0 + maj);
                h = g; g = f; f = e; e = unchecked(d + t1);
                d = c; c = b; b = a; a = unchecked(t1 + t2);
            }
            unchecked
            {
                state[0] += a; state[1] += b; state[2] += c; state[3] += d;
                state[4] += e; state[5] += f; state[6] += g; state[7] += h;
            }
        }

        private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
    }
}
