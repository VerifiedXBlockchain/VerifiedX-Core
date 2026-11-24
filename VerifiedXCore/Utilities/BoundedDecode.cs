using NBitcoin;
using NBitcoin.DataEncoders;

namespace VerifiedXCore.Utilities
{
    /// <summary>
    /// The decoders for text another party writes, each refusing a text longer than any value of its kind before
    /// decoding it. Base58 decoding costs time that grows with the square of the text's length, and from a few million
    /// characters NBitcoin's decoder ends the process with a stack overflow that no code can catch. A caller
    /// reaches these and never the raw decoders: a test fails the build when a raw call appears anywhere else.
    /// </summary>
    public static class BoundedDecode
    {
        /// <summary>
        /// The longest a Bitcoin address can be: a Bech32 string is at most 90 characters, and a Base58 one longer than
        /// that decodes to more than an address's 25 bytes.
        /// </summary>
        public const int MaxBitcoinAddressChars = 90;

        /// <summary>
        /// <see cref="NBitcoin.BitcoinAddress.Create(string, Network)"/>, refusing with <see cref="FormatException"/> (as
        /// the decoder refuses any text that is not an address) a text longer than any address, without decoding it.
        /// </summary>
        public static BitcoinAddress BitcoinAddress(string text, Network network)
        {
            if (text != null && text.Length > MaxBitcoinAddressChars)
                throw new FormatException($"A Bitcoin address is at most {MaxBitcoinAddressChars} characters.");
            return NBitcoin.BitcoinAddress.Create(text, network);
        }

        /// <summary>The longest Base58 text that decodes to <paramref name="bytes"/> bytes: log(256)/log(58) characters a byte.</summary>
        public static int MaxBase58Chars(int bytes) => (int)Math.Ceiling(bytes * 1.3658);

        /// <summary>
        /// Base58 data of at most <paramref name="maxBytes"/> bytes, refusing with <see cref="FormatException"/> a text
        /// longer than such data is, without decoding it.
        /// </summary>
        public static byte[] Base58(string text, int maxBytes)
        {
            if (text != null && text.Length > MaxBase58Chars(maxBytes))
                throw new FormatException($"The Base58 text is longer than {maxBytes} bytes of data.");
            return Encoders.Base58.DecodeData(text);
        }
    }
}
