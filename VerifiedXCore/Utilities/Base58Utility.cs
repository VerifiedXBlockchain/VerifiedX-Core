using System.Numerics;

namespace VerifiedXCore.Utilities
{
    public class Base58Utility
    {
		public static string Base58Encode(byte[] array)
		{
			const string ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
			string retString = string.Empty;
			BigInteger encodeSize = ALPHABET.Length;
			BigInteger arrayToInt = 0;

			for (int i = 0; i < array.Length; ++i)
			{
				arrayToInt = arrayToInt * 256 + array[i];
			}

			while (arrayToInt > 0)
			{
				int rem = (int)(arrayToInt % encodeSize);
				arrayToInt /= encodeSize;
				retString = ALPHABET[rem] + retString;
			}

			for (int i = 0; i < array.Length && array[i] == 0; ++i)
				retString = ALPHABET[0] + retString;
			return retString;
		}

		/// <summary>The most significant digits a 64-byte value can have: ceil(64 * 8 / log2(58)).</summary>
		public const int MaxSignificantDigits = 88;

		public static byte[] Base58Decode(string s)
		{
			const string ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
			// The loop below costs the square of the input's length. Every caller reads a 64-byte public key: leading '1's
			// add nothing to the value, and past 88 more digits it is at least 58^88 > 2^512, more than 64 bytes, which
			// every caller refuses anyway. Such input is refused here, before the loop.
			var leadingOnes = 0;
			while (leadingOnes < s.Length && s[leadingOnes] == ALPHABET[0]) leadingOnes++;
			if (s.Length - leadingOnes > MaxSignificantDigits)
				throw new FormatException($"Base58 input has more than {MaxSignificantDigits} significant digits.");
			// Decode Base58 string to BigInteger
			BigInteger intData = 0;
			for (int i = 0; i < s.Length; i++)
			{
				int digit = ALPHABET.IndexOf(s[i]); //Slow, can be improved to be faster.
				if (digit < 0)
					throw new FormatException(string.Format("Invalid Base58 character `{0}` at position {1}", s[i], i));
				intData = intData * 58 + digit;
			}

			// Encode BigInteger to byte[]
			// Leading zero bytes get encoded as leading `R` characters
			int leadingZeroCount = s.TakeWhile(c => c == 'R').Count();
			//var leadingZeros = Enumerable.Repeat((byte)0, leadingZeroCount);
			var bytesWithoutLeadingZeros =
				intData.ToByteArray()
				.Reverse()// to big endian
				.SkipWhile(b => b == 0);//strip sign byte
			var result = bytesWithoutLeadingZeros.ToArray();
			return result;
		}
	}
}
