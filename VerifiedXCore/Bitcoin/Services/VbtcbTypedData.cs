using System.Numerics;
using System.Text;
using Nethereum.Util;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// The EIP-712 messages the vBTCb V4 contract (deploy/contracts/vBTCb.sol) checks: domain ("vBTCb", "4", chainId,
    /// contract) and one type hash per action, so a signature over one action can never be read as another.
    /// </summary>
    public static class VbtcbTypedData
    {
        /// <summary>The contract's MESSAGE_VERSION() for these messages. The deployed V3 has no such function (version 3).</summary>
        public const int MessageVersion = 4;

        private static readonly byte[] DomainTypeHash = Keccak("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
        private static readonly byte[] DomainNameHash = Keccak("vBTCb");
        private static readonly byte[] DomainVersionHash = Keccak("4");
        private static readonly byte[] MintTypeHash = Keccak("Mint(address to,uint256 amount,string lockId,uint256 nonce)");

        public static byte[] DomainSeparator(long chainId, string contractAddress) =>
            Keccak(Concat(DomainTypeHash, DomainNameHash, DomainVersionHash, Uint(chainId), Address(contractAddress)));

        /// <summary>keccak256("\x19\x01" || domainSeparator || structHash), the digest the contract recovers signers from.</summary>
        public static byte[] Digest(long chainId, string contractAddress, byte[] structHash) =>
            Keccak(Concat(new byte[] { 0x19, 0x01 }, DomainSeparator(chainId, contractAddress), structHash));

        public static byte[] MintDigest(string to, long amountSats, string lockId, long nonce, long chainId, string contractAddress) =>
            Digest(chainId, contractAddress,
                Keccak(Concat(MintTypeHash, Address(to), Uint(amountSats), Keccak(lockId ?? ""), Uint(nonce))));

        internal static byte[] Keccak(string s) => Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes(s));
        internal static byte[] Keccak(byte[] b) => Sha3Keccack.Current.CalculateHash(b);

        internal static byte[] Concat(params byte[][] parts)
        {
            var all = new byte[parts.Sum(p => p.Length)];
            var at = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
            return all;
        }

        /// <summary>A 20-byte address left-padded to one 32-byte word.</summary>
        internal static byte[] Address(string? address)
        {
            var hex = (address ?? "").Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            var b = Convert.FromHexString(hex);
            if (b.Length != 20) throw new ArgumentException("Invalid EVM address length");
            var word = new byte[32];
            Buffer.BlockCopy(b, 0, word, 12, 20);
            return word;
        }

        /// <summary>A non-negative integer as one big-endian 32-byte word.</summary>
        internal static byte[] Uint(BigInteger v)
        {
            if (v.Sign < 0) throw new ArgumentOutOfRangeException(nameof(v));
            var be = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (be.Length > 32) throw new ArgumentOutOfRangeException(nameof(v));
            var word = new byte[32];
            Buffer.BlockCopy(be, 0, word, 32 - be.Length, be.Length);
            return word;
        }
    }
}
